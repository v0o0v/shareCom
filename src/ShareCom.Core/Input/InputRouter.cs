using ShareCom.Core.Layout;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Input;

/// <summary>InputRouter 가 바깥(네트워크·커서)에 시키는 일.</summary>
public interface IInputSink
{
    void Send(Guid peer, Message message);
    void SetLocalCursor(int x, int y);
    void HideLocalCursor();
    void ShowLocalCursor();
    /// <summary>이 PC가 조작당하던 중에 실제 입력이 생겨 주인을 되찾았다. 재현 중이던 키·버튼을 모두 뗀다.</summary>
    void ReleaseInjectedInput();
    /// <summary>내가 조작하는 PC가 바뀌었다(null 이면 내 PC).</summary>
    void ControllingChanged(Guid? peer);
}

/// <summary>
/// 이 PC의 실제 마우스·키보드 입력을 내 PC로 흘릴지, 다른 PC로 보낼지 정한다.
/// 상대 화면 안의 가상 커서 위치는 여기서 계산해 절대 좌표로 보낸다.
/// 모든 On* 메서드는 "이 입력을 내 PC에서 삼킬지"를 돌려준다(입력 훅이 그대로 쓴다).
/// </summary>
public sealed class InputRouter
{
    public const ushort VkEscape = 0x1B, VkScrollLock = 0x91;
    public const ushort VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5, VkControl = 0x11, VkMenu = 0x12;
    public const ushort Vk1 = 0x31, Vk4 = 0x34;

    /// <summary>화면 위·아래 모서리에서 이만큼은 넘어가지 않는다(최대화 창의 닫기 버튼 누르기 등).</summary>
    public int CornerDeadZone { get; set; } = 10;
    /// <summary>조작당하는 PC에서 실제 마우스가 이만큼 움직이면 주인을 되찾는다(책상 진동 무시).</summary>
    public int TakeOverMoveThreshold { get; set; } = 12;
    /// <summary>다른 PC로 넘어간 직후, 커서를 화면 가운데로 옮기기 전에 쌓인 이동은 이 거리 밖이면 버린다.</summary>
    public int ParkSettleRadius { get; set; } = 200;

    private readonly IInputSink _sink;
    private readonly object _gate = new();

    private Guid _self;
    private LayoutEntry[] _layout = [];          // 내 항목의 화면은 _localMonitors 로 바꿔 둔다
    private readonly HashSet<Guid> _connected = new();

    private ScreenRect[] _localMonitors = [new ScreenRect(0, 0, 1920, 1080)];
    private int _localScale = 100;
    private (int X, int Y) _park = (960, 540);
    // 내가 조작 중인 PC
    private Guid? _controlling;
    private int _rx, _ry;
    private bool _awaitingPark;

    // 나를 조작 중인 PC
    private Guid? _controlledBy;
    private int _takeOverAccum;

    private bool _edgeLocked;
    private int _lastX, _lastY;
    private bool _hasLast;

    private readonly HashSet<MouseButtonKind> _localButtons = new();
    private readonly HashSet<MouseButtonKind> _remoteButtons = new();
    private readonly HashSet<ushort> _localKeys = new();
    private readonly Dictionary<ushort, KeyMessage> _remoteKeys = new();
    private readonly HashSet<ushort> _physicalKeys = new();
    private readonly HashSet<ushort> _swallowUps = new();

    public InputRouter(IInputSink sink) => _sink = sink;

    public Guid? Controlling { get { lock (_gate) return _controlling; } }
    public Guid? ControlledBy { get { lock (_gate) return _controlledBy; } }

    public bool EdgeLocked
    {
        get { lock (_gate) return _edgeLocked; }
        set { lock (_gate) _edgeLocked = value; }
    }

    public event Action<bool>? EdgeLockChanged;

    // ───────────── 화면·상대 정보 ─────────────

    /// <summary>이 PC의 ID(배치에서 내 항목을 찾는 데 쓴다).</summary>
    public void SetSelf(Guid id)
    {
        lock (_gate)
        {
            _self = id;
            RebuildLayoutLocked(_layout);
        }
    }

    /// <summary>내 모니터 목록과 배율. 첫 번째가 주 모니터라고 보고 그 가운데에 커서를 숨겨 둔다.</summary>
    public void SetLocalScreens(ScreenRect[] monitors, int scalePercent = 100)
    {
        if (monitors.Length == 0) return;
        lock (_gate)
        {
            _localMonitors = monitors;
            _localScale = scalePercent;
            var p = monitors[0];
            _park = (p.X + p.Width / 2, p.Y + p.Height / 2);
            RebuildLayoutLocked(_layout);
        }
    }

    /// <summary>모든 PC의 배치(위치와 화면).</summary>
    public void SetLayout(IReadOnlyList<LayoutEntry> entries)
    {
        lock (_gate)
        {
            RebuildLayoutLocked(entries);
            if (_controlling is { } id && Monitors(id) is { Length: > 0 } monitors)
                (_rx, _ry) = Clamp(monitors, _rx, _ry).Point;
        }
    }

    private void RebuildLayoutLocked(IReadOnlyList<LayoutEntry> entries) =>
        _layout = entries.Select(e => e.Id == _self ? e with { Screen = new PcScreen(_localMonitors, _localScale) } : e).ToArray();

    private ScreenRect[]? Monitors(Guid id) => _layout.FirstOrDefault(e => e.Id == id)?.Screen.Monitors;

    public void RemovePeer(Guid id) => SetPeerConnected(id, false);

    public void SetPeerConnected(Guid id, bool connected)
    {
        lock (_gate)
        {
            if (connected) { _connected.Add(id); return; }
            _connected.Remove(id);
            if (_controlling == id)
            {
                // 보낼 수 없으므로 상대 쪽 키 떼기는 상대가 스스로 한다.
                _remoteKeys.Clear();
                _remoteButtons.Clear();
                EndControlLocked(sendLeave: false, _park);
            }
            if (_controlledBy == id)
            {
                _controlledBy = null;
                _sink.ReleaseInjectedInput();
            }
        }
    }

    /// <summary>넘어갈 수 있는 PC(나 또는 연결되고 배치에 화면이 있는 PC).</summary>
    private bool AvailableLocked(Guid id) => id == _self || (_connected.Contains(id) && Monitors(id) is { Length: > 0 });
    // ───────────── 상대가 나를 조작 ─────────────

    /// <summary>상대 PC가 나를 조작하기 시작했다(EnterControl 받음).</summary>
    public void BeginControlledBy(Guid peer)
    {
        lock (_gate)
        {
            // 서로 상대를 조작하는 상태가 되지 않게, 내가 상대를 조작 중이었으면 놓는다.
            if (_controlling is not null) EndControlLocked(sendLeave: true, _park);
            _controlledBy = peer;
            _takeOverAccum = 0;
        }
    }

    /// <summary>상대 PC가 나를 놓았다(LeaveControl 받음).</summary>
    public void EndControlledBy(Guid peer)
    {
        lock (_gate)
        {
            if (_controlledBy != peer) return;
            _controlledBy = null;
        }
    }

    /// <summary>내가 조작하던 PC에서 그 PC 사용자가 주인을 되찾았다(TakeOver 받음).</summary>
    public void OnTakeOver(Guid peer)
    {
        lock (_gate)
        {
            if (_controlling != peer) return;
            _remoteKeys.Clear();
            _remoteButtons.Clear();
            EndControlLocked(sendLeave: false, _park);
        }
    }

    /// <summary>긴급 복귀 또는 화면 잠금: 무조건 내 PC로 돌아온다.</summary>
    public void ReturnHome()
    {
        lock (_gate)
        {
            if (_controlling is not null) EndControlLocked(sendLeave: true, _park);
        }
    }

    private void CheckTakeOverLocked(int movement)
    {
        if (_controlledBy is not { } by) return;
        _takeOverAccum += movement;
        if (movement == int.MaxValue || _takeOverAccum >= TakeOverMoveThreshold)
        {
            _controlledBy = null;
            _sink.Send(by, new TakeOverMessage());
            _sink.ReleaseInjectedInput();
        }
    }

    // ───────────── 마우스 ─────────────

    public bool OnMouseMove(int x, int y, bool injected)
    {
        lock (_gate)
        {
            int moved = _hasLast ? Math.Abs(x - _lastX) + Math.Abs(y - _lastY) : 0;
            _lastX = x; _lastY = y; _hasLast = true;
            if (injected) return false;

            if (_controlling is { } peerId) return MoveRemoteLocked(peerId, x, y);

            CheckTakeOverLocked(moved);
            if (_controlledBy is not null) return false;
            TrySwitchAtEdgeLocked(x, y);
            return _controlling is not null;
        }
    }

    public bool OnMouseButton(MouseButtonKind button, bool down, bool injected)
    {
        lock (_gate)
        {
            if (injected) return false;
            if (_controlling is { } peer)
            {
                if (!down && _localButtons.Remove(button)) return false; // 내 PC에서 눌렀던 버튼은 내 PC에서 뗀다
                if (down) _remoteButtons.Add(button); else _remoteButtons.Remove(button);
                _sink.Send(peer, new MouseButtonMessage(button, down));
                return true;
            }
            CheckTakeOverLocked(int.MaxValue);
            if (down) _localButtons.Add(button); else _localButtons.Remove(button);
            return false;
        }
    }

    public bool OnMouseWheel(int delta, bool horizontal, bool injected)
    {
        lock (_gate)
        {
            if (injected) return false;
            if (_controlling is { } peer)
            {
                _sink.Send(peer, new MouseWheelMessage(delta, horizontal));
                return true;
            }
            CheckTakeOverLocked(int.MaxValue);
            return false;
        }
    }

    private bool MoveRemoteLocked(Guid peerId, int x, int y)
    {
        int dx = x - _park.X, dy = y - _park.Y;
        if (_awaitingPark)
        {
            // 커서를 가운데로 옮기기 전에 쌓였던 이동은 버린다.
            if (Math.Abs(dx) > ParkSettleRadius || Math.Abs(dy) > ParkSettleRadius) return true;
            _awaitingPark = false;
        }
        if (dx == 0 && dy == 0) return true;

        if (Monitors(peerId) is not { Length: > 0 } monitors) return true;
        int nx = _rx + dx, ny = _ry + dy;
        var (clamped, monitor) = Clamp(monitors, nx, ny);

        // 상대 화면 가장자리를 밀면 그쪽에 맞닿은 PC(내 PC 포함)로 넘어간다.
        if (!_edgeLocked && _remoteButtons.Count == 0)
        {
            PushDirection? dir = nx < clamped.X ? PushDirection.Left : nx > clamped.X ? PushDirection.Right
                               : ny < clamped.Y ? PushDirection.Up : ny > clamped.Y ? PushDirection.Down : null;
            if (dir is { } d && !InCornerDeadZone(monitor, clamped.X, clamped.Y, d)
                && FindTargetLocked(peerId, monitor, clamped.X, clamped.Y, d) is { } target)
            {
                ReleaseRemoteLocked(peerId);
                if (target.Id == _self)
                {
                    EndControlLocked(sendLeave: true, (target.X, target.Y));
                }
                else
                {
                    _sink.Send(peerId, new LeaveControlMessage());
                    _controlling = target.Id;
                    (_rx, _ry) = (target.X, target.Y);
                    _sink.Send(target.Id, new EnterControlMessage(_rx, _ry));
                    _sink.SetLocalCursor(_park.X, _park.Y);
                    _sink.ControllingChanged(target.Id);
                }
                return true;
            }
        }

        if (clamped != (_rx, _ry))
        {
            (_rx, _ry) = clamped;
            _sink.Send(peerId, new MouseMoveMessage(_rx, _ry));
        }
        _sink.SetLocalCursor(_park.X, _park.Y);
        return true;
    }

    private void TrySwitchAtEdgeLocked(int x, int y)
    {
        if (_edgeLocked || _localButtons.Count > 0) return;
        var (_, m) = Clamp(_localMonitors, x, y);
        // 그쪽에 내 다른 모니터가 이어지면 Windows 가 커서를 옮겨 주므로 가장자리가 아니다.
        PushDirection? dir = null;
        if (x >= m.Right - 1 && !InLocal(x + 1, y)) dir = PushDirection.Right;
        else if (x <= m.X && !InLocal(x - 1, y)) dir = PushDirection.Left;
        else if (y >= m.Bottom - 1 && !InLocal(x, y + 1)) dir = PushDirection.Down;
        else if (y <= m.Y && !InLocal(x, y - 1)) dir = PushDirection.Up;
        if (dir is not { } d || InCornerDeadZone(m, x, y, d)) return;

        if (FindTargetLocked(_self, m, x, y, d) is not { } target || target.Id == _self) return;
        BeginControlLocked(target.Id, (target.X, target.Y));
    }

    private bool InLocal(int x, int y) => _localMonitors.Any(m => m.Contains(x, y));

    /// <summary>모니터 모서리 근처(최대화 창의 닫기 버튼 등)에서는 넘어가지 않는다.</summary>
    private bool InCornerDeadZone(ScreenRect m, int x, int y, PushDirection d) => d is PushDirection.Left or PushDirection.Right
        ? y < m.Y + CornerDeadZone || y >= m.Bottom - CornerDeadZone
        : x < m.X + CornerDeadZone || x >= m.Right - CornerDeadZone;

    /// <summary>들어가는 자리: 내 PC는 가장자리에서 2픽셀, 다른 PC는 1픽셀 안쪽(돌아온 직후 곧바로 다시 넘어가지 않게).</summary>
    private PushTarget? FindTargetLocked(Guid from, ScreenRect monitor, int x, int y, PushDirection d) =>
        DeskLayout.FindTarget(_layout, from, monitor, x, y, d, AvailableLocked, id => id == _self ? 2 : 1);
    private void BeginControlLocked(Guid peer, (int X, int Y) entry)
    {
        _controlling = peer;
        (_rx, _ry) = entry;
        _awaitingPark = true;
        _sink.Send(peer, new EnterControlMessage(_rx, _ry));
        _sink.SetLocalCursor(_park.X, _park.Y);
        _sink.HideLocalCursor();
        _sink.ControllingChanged(peer);
    }

    /// <summary>상대 PC에 눌러 둔 키·버튼을 모두 뗀다. 아직 실제로 누르고 있는 키는 내 PC에서 뗄 때 삼킨다.</summary>
    private void ReleaseRemoteLocked(Guid peer)
    {
        foreach (var k in _remoteKeys.Values)
        {
            _sink.Send(peer, k with { Down = false });
            if (_physicalKeys.Contains(k.VirtualKey)) _swallowUps.Add(k.VirtualKey);
        }
        _remoteKeys.Clear();
        foreach (var b in _remoteButtons) _sink.Send(peer, new MouseButtonMessage(b, false));
        _remoteButtons.Clear();
    }

    private void EndControlLocked(bool sendLeave, (int X, int Y) localPos)
    {
        if (_controlling is not { } peer) return;
        if (sendLeave)
        {
            ReleaseRemoteLocked(peer);
            _sink.Send(peer, new LeaveControlMessage());
        }
        _controlling = null;
        _awaitingPark = false;
        _sink.SetLocalCursor(localPos.X, localPos.Y);
        _sink.ShowLocalCursor();
        _lastX = localPos.X; _lastY = localPos.Y;
        _sink.ControllingChanged(null);
    }

    // ───────────── 키보드 ─────────────

    public bool OnKey(ushort vk, ushort scanCode, bool extended, bool down, bool injected)
    {
        bool? lockChanged = null;
        bool swallow;
        lock (_gate)
        {
            if (injected) return false;
            if (down) _physicalKeys.Add(vk); else _physicalKeys.Remove(vk);
            swallow = HandleKeyLocked(vk, scanCode, extended, down, ref lockChanged);
        }
        if (lockChanged is { } locked) EdgeLockChanged?.Invoke(locked);
        return swallow;
    }

    private bool HandleKeyLocked(ushort vk, ushort scanCode, bool extended, bool down, ref bool? lockChanged)
    {
        if (!down && _swallowUps.Remove(vk)) return true;

        // 단축키
        if (down)
        {
            bool ctrl = _physicalKeys.Contains(VkLControl) || _physicalKeys.Contains(VkRControl) || _physicalKeys.Contains(VkControl);
            bool alt = _physicalKeys.Contains(VkLMenu) || _physicalKeys.Contains(VkRMenu) || _physicalKeys.Contains(VkMenu);
            if (vk == VkScrollLock)
            {
                _edgeLocked = !_edgeLocked;
                lockChanged = _edgeLocked;
                if (_controlling is not null) { _swallowUps.Add(vk); return true; }
                return false;
            }
            if (ctrl && alt && vk == VkEscape)
            {
                _swallowUps.Add(vk);
                if (_controlling is not null) EndControlLocked(sendLeave: true, _park);
                return true;
            }
            if (ctrl && alt && vk >= Vk1 && vk <= Vk4)
            {
                _swallowUps.Add(vk);
                JumpLocked(vk - Vk1);
                return true;
            }
        }

        if (_controlling is { } peer)
        {
            if (!down && _localKeys.Remove(vk)) return false; // 내 PC에서 눌렀던 키는 내 PC에서 뗀다
            var msg = new KeyMessage(vk, scanCode, extended, down);
            if (down) _remoteKeys[vk] = msg; else _remoteKeys.Remove(vk);
            _sink.Send(peer, msg);
            return true;
        }

        CheckTakeOverLocked(int.MaxValue);
        if (down) _localKeys.Add(vk); else _localKeys.Remove(vk);
        return false;
    }

    /// <summary>PC 번호 순서(2번부터): 배치에서 왼쪽부터, 같으면 위부터. 연결되지 않은 PC도 자리는 차지한다.</summary>
    public IReadOnlyList<Guid> JumpOrder()
    {
        lock (_gate) return DeskLayout.JumpOrder(_layout, _self);
    }

    private void JumpLocked(int index)
    {
        if (index == 0)
        {
            if (_controlling is not null) EndControlLocked(sendLeave: true, _park);
            return;
        }
        var order = DeskLayout.JumpOrder(_layout, _self);
        if (index - 1 >= order.Count) return;
        var target = order[index - 1];
        if (_controlling == target || !AvailableLocked(target)) return;
        if (_controlling is not null) EndControlLocked(sendLeave: true, _park);
        var first = Monitors(target)![0];
        BeginControlLocked(target, (first.X + first.Width / 2, first.Y + first.Height / 2));
    }
    // ───────────── 좌표 도우미 ─────────────

    /// <summary>점을 가장 가까운 모니터 안으로 끌어온다. 끌어온 점과 그 모니터를 돌려준다.</summary>
    public static ((int X, int Y) Point, ScreenRect Monitor) Clamp(IReadOnlyList<ScreenRect> monitors, int x, int y)
    {
        (int, int) best = (x, y);
        ScreenRect bestMonitor = monitors[0];
        long bestDist = long.MaxValue;
        foreach (var m in monitors)
        {
            int cx = Math.Clamp(x, m.X, m.Right - 1);
            int cy = Math.Clamp(y, m.Y, m.Bottom - 1);
            long d = (long)(cx - x) * (cx - x) + (long)(cy - y) * (cy - y);
            if (d < bestDist)
            {
                bestDist = d;
                best = (cx, cy);
                bestMonitor = m;
                if (d == 0) break;
            }
        }
        return (best, bestMonitor);
    }
}
