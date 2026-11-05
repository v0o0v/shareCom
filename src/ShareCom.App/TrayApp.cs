using System.Diagnostics;
using Microsoft.Win32;
using ShareCom.App.Infra;
using ShareCom.App.Input;
using ShareCom.App.Native;
using ShareCom.App.UI;
using ShareCom.Core.Identity;
using ShareCom.Core.Input;
using ShareCom.Core.Net;
using ShareCom.Core.Protocol;
using Message = ShareCom.Core.Protocol.Message;

namespace ShareCom.App;

/// <summary>트레이에 상주하며 통신(Node)·입력 라우팅(InputRouter)·입력 훅·입력 재현을 잇는다.</summary>
internal sealed class TrayApp : ApplicationContext, IInputSink
{
    private readonly AppStorage _storage;
    private readonly JsonPeerStore _store;
    private readonly DeviceIdentity _identity;
    private readonly Node _node;
    private readonly InputRouter _router;
    private readonly InputInjector _injector = new();
    private readonly InputHooks _hooks;
    private readonly NotifyIcon _tray;
    private readonly Control _ui;
    private Guid? _controlling;

    public TrayApp()
    {
        _ui = new Control();
        _ui.CreateControl(); // UI 스레드로 넘기는 BeginInvoke 용

        _storage = new AppStorage();
        _identity = _storage.LoadOrCreateIdentity();
        _store = new JsonPeerStore(_storage);
        Log.Info($"시작: {_identity.Name} ({_identity.Id}), 지문 {_identity.Thumbprint[..16]}…, 관리자={SystemSetup.IsAdministrator()}");

        _router = new InputRouter(this) { EdgeLocked = _storage.Settings.EdgeLocked };
        _router.EdgeLockChanged += locked =>
        {
            _storage.Settings.EdgeLocked = locked;
            SaveSettings();
            Notify(locked ? "가장자리 넘기기를 잠갔습니다. (Scroll Lock 으로 풀기)" : "가장자리 넘기기 잠금을 풀었습니다.");
        };
        _router.SetLocalScreens(LocalMonitors());
        SyncPeersToRouter();
        _store.Changed += SyncPeersToRouter;

        _node = new Node(_identity, _store, new UdpDiscovery(), new NodeOptions { EncryptionEnabled = _storage.Settings.EncryptionEnabled });
        _node.Log += Log.Info;
        _node.PeerConnected += OnPeerConnected;
        _node.PeerDisconnected += OnPeerDisconnected;
        _node.MessageReceived += OnMessage;
        _node.PairingConfirmer = ConfirmPairingAsync;
        _node.PairingCompleted += OnPairingCompleted;

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.Idle,
            Text = "ShareCom",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip { Font = UiFonts.Normal },
        };
        _tray.ContextMenuStrip.Opening += (_, e) => { BuildMenu(_tray.ContextMenuStrip); e.Cancel = false; };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowContextMenu();
        };

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _hooks = new InputHooks(_router);
        _hooks.Start();
        try
        {
            _node.Start();
        }
        catch (Exception ex)
        {
            Log.Error("통신 시작 실패", ex);
            MessageBox.Show($"통신을 시작하지 못했습니다. 다른 프로그램이 포트 47821 을 쓰고 있을 수 있습니다.\n\n{ex.Message}",
                "ShareCom", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Task.Run(SystemSetup.EnsureFirewallRule);
        UpdateTrayState();
        Notify(_store.GetAll().Count == 0
            ? "실행 중입니다. 트레이 아이콘을 눌러 다른 PC와 짝을 맺으세요."
            : "실행 중입니다. 짝 맺은 PC에 연결합니다.");
    }

    // ───────────── IInputSink ─────────────

    void IInputSink.Send(Guid peer, Message message) => _node.Send(peer, message);
    void IInputSink.SetLocalCursor(int x, int y) => Win32.SetCursorPos(x, y);
    void IInputSink.HideLocalCursor() => CursorHider.Hide();
    void IInputSink.ShowLocalCursor() => CursorHider.Show();
    void IInputSink.ReleaseInjectedInput() => _injector.ReleaseAll();

    void IInputSink.ControllingChanged(Guid? peer)
    {
        _controlling = peer;
        _ui.BeginInvoke(UpdateTrayState);
    }

    // ───────────── 통신 이벤트 ─────────────

    private void OnPeerConnected(Guid peer)
    {
        _router.SetPeerConnected(peer, true);
        _node.Send(peer, new ScreenInfoMessage(LocalMonitors()));
        var name = _store.Get(peer)?.Name ?? "PC";
        Notify($"'{name}' 와 연결됐습니다{(_node.IsEncrypted(peer) ? "" : " (암호화 꺼짐)")}.");
        _ui.BeginInvoke(UpdateTrayState);
    }

    private void OnPeerDisconnected(Guid peer)
    {
        _router.SetPeerConnected(peer, false);
        var name = _store.Get(peer)?.Name;
        if (name is not null) Notify($"'{name}' 와 연결이 끊겼습니다. 다시 연결을 시도합니다.");
        _ui.BeginInvoke(UpdateTrayState);
    }

    private void OnMessage(Guid peer, Message message)
    {
        switch (message)
        {
            case ScreenInfoMessage s:
                _router.SetPeerScreens(peer, s.Monitors);
                break;
            case LayoutSideMessage l when _store.Get(peer) is { } rec:
                _store.Upsert(rec with { Side = l.SenderIsOnYour });
                break;
            case EnterControlMessage e:
                _router.BeginControlledBy(peer);
                _injector.MoveTo(e.X, e.Y);
                break;
            case LeaveControlMessage:
                _router.EndControlledBy(peer);
                _injector.ReleaseAll();
                break;
            case TakeOverMessage:
                _router.OnTakeOver(peer);
                break;
            case MouseMoveMessage m when _router.ControlledBy == peer:
                _injector.MoveTo(m.X, m.Y);
                break;
            case MouseButtonMessage b when _router.ControlledBy == peer:
                _injector.Button(b.Button, b.Down);
                break;
            case MouseWheelMessage w when _router.ControlledBy == peer:
                _injector.Wheel(w.Delta, w.Horizontal);
                break;
            case KeyMessage k when _router.ControlledBy == peer || !k.Down:
                // 떼기는 조작이 끝난 뒤에 도착해도 재현한다(눌린 채 남지 않게).
                _injector.Key(k);
                break;
        }
    }

    private Task<bool> ConfirmPairingAsync(PairingPrompt prompt, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.BeginInvoke(() =>
        {
            var form = new PairingForm(prompt);
            form.FormClosed += (_, _) => tcs.TrySetResult(form.Accepted);
            ct.Register(() => _ui.BeginInvoke(() => { if (!form.IsDisposed) form.Close(); }));
            form.Show();
            form.Activate();
        });
        return tcs.Task;
    }

    private void OnPairingCompleted(PairingResult r)
    {
        Log.Info($"짝 맺기 결과: {r.Outcome} {r.PeerName} {r.Error}");
        Notify(r.Outcome switch
        {
            PairingOutcome.Paired => $"'{r.PeerName}' 와 짝을 맺었습니다. 곧 연결됩니다.",
            PairingOutcome.RejectedByPeer => $"'{r.PeerName}' 쪽에서 짝 맺기를 취소했습니다.",
            PairingOutcome.RejectedByMe => "짝 맺기를 취소했습니다.",
            _ => $"짝 맺기에 실패했습니다: {r.Error}",
        });
    }

    // ───────────── 시스템 이벤트 ─────────────

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        var monitors = LocalMonitors();
        _router.SetLocalScreens(monitors);
        foreach (var peer in _node.ConnectedPeers) _node.Send(peer, new ScreenInfoMessage(monitors));
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteConnect)
        {
            Log.Info($"화면 잠금/세션 전환({e.Reason}): 내 PC로 돌아옵니다.");
            _router.ReturnHome();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _router.ReturnHome();
        if (e.Mode == PowerModes.Resume)
        {
            Log.Info("절전에서 깨어남: 연결을 다시 확인합니다.");
            foreach (var peer in _node.ConnectedPeers) _node.Disconnect(peer); // 죽었을 연결을 바로 새로 건다
        }
    }

    // ───────────── 트레이 메뉴 ─────────────

    private void ShowContextMenu()
    {
        // NotifyIcon 의 감춰진 ShowContextMenu 를 불러 왼쪽 클릭에도 메뉴를 띄운다.
        typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(_tray, null);
    }

    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem($"ShareCom · 내 PC: {_identity.Name}") { Enabled = false });
        var nowText = _controlling is { } c ? $"지금: '{_store.Get(c)?.Name}' 조작 중" : "지금: 내 PC 사용 중";
        menu.Items.Add(new ToolStripMenuItem(nowText) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("짝 맺은 PC") { Enabled = false });
        var peers = _store.GetAll().OrderBy(p => p.Name).ToList();
        if (peers.Count == 0) menu.Items.Add(new ToolStripMenuItem("   (아직 없음)") { Enabled = false });
        foreach (var p in peers)
        {
            var state = _node.IsConnected(p.Id) ? (_node.IsEncrypted(p.Id) ? "연결됨" : "연결됨, 암호화 꺼짐") : "연결 안 됨";
            var item = new ToolStripMenuItem($"   {p.Name} — {SideText(p.Side)}, {state}");
            var left = new ToolStripMenuItem("내 왼쪽에 두기") { Checked = p.Side == NeighborSide.Left };
            var right = new ToolStripMenuItem("내 오른쪽에 두기") { Checked = p.Side == NeighborSide.Right };
            left.Click += (_, _) => SetSide(p.Id, NeighborSide.Left);
            right.Click += (_, _) => SetSide(p.Id, NeighborSide.Right);
            var unpair = new ToolStripMenuItem("짝 풀기…");
            unpair.Click += (_, _) => Unpair(p);
            item.DropDownItems.AddRange([left, right, new ToolStripSeparator(), unpair]);
            menu.Items.Add(item);
        }

        var pairMenu = new ToolStripMenuItem("새 PC와 짝 맺기");
        var candidates = _node.Discovered.Where(d => _store.Get(d.Id) is null).ToList();
        if (candidates.Count == 0)
            pairMenu.DropDownItems.Add(new ToolStripMenuItem("(같은 와이파이에서 찾는 중… 상대 PC에서도 ShareCom 을 켜세요)") { Enabled = false });
        foreach (var d in candidates)
        {
            var item = new ToolStripMenuItem($"{d.Name} ({d.Endpoint.Address})");
            item.Click += (_, _) => StartPairing(d.Endpoint);
            pairMenu.DropDownItems.Add(item);
        }
        pairMenu.DropDownItems.Add(new ToolStripSeparator());
        var byAddress = new ToolStripMenuItem("IP 주소로 직접 짝 맺기…");
        byAddress.Click += (_, _) =>
        {
            using var f = new ConnectByAddressForm(_node.ListeningPort);
            if (f.ShowDialog() == DialogResult.OK && f.Endpoint is { } ep) StartPairing(ep);
        };
        pairMenu.DropDownItems.Add(byAddress);
        menu.Items.Add(pairMenu);
        menu.Items.Add(new ToolStripSeparator());

        var edge = new ToolStripMenuItem("가장자리 넘기기 잠금 (Scroll Lock)") { Checked = _router.EdgeLocked };
        edge.Click += (_, _) =>
        {
            _router.EdgeLocked = !_router.EdgeLocked;
            _storage.Settings.EdgeLocked = _router.EdgeLocked;
            SaveSettings();
        };
        menu.Items.Add(edge);

        var encryption = new ToolStripMenuItem("암호화") { Checked = _node.EncryptionEnabled };
        encryption.Click += (_, _) => ToggleEncryption();
        menu.Items.Add(encryption);

        var help = new ToolStripMenuItem("단축키 보기");
        help.Click += (_, _) => MessageBox.Show(
            "Scroll Lock  : 가장자리 넘기기 잠금 켜기/끄기\n" +
            "Ctrl+Alt+1   : 내 PC로\n" +
            "Ctrl+Alt+2~4 : 짝 맺은 PC로 바로 이동 (왼쪽 PC부터 차례로 번호)\n" +
            "Ctrl+Alt+Esc : 긴급 복귀 (무조건 내 PC로)\n\n" +
            "다른 PC를 조작하는 중에 그 PC의 마우스·키보드를 쓰면 그 PC가 주인을 되찾습니다.\n" +
            "Ctrl+Alt+Del 과 UAC 창·잠금 화면은 해당 PC에서 직접 조작해야 합니다.",
            "ShareCom 단축키", MessageBoxButtons.OK, MessageBoxIcon.Information);
        menu.Items.Add(help);

        var logs = new ToolStripMenuItem("기록 폴더 열기");
        logs.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_storage.Directory}\"") { UseShellExecute = true });
        menu.Items.Add(logs);

        menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("종료");
        exit.Click += (_, _) => ExitThread();
        menu.Items.Add(exit);
    }

    private static string SideText(NeighborSide side) => side == NeighborSide.Left ? "왼쪽" : "오른쪽";

    private void StartPairing(System.Net.IPEndPoint endpoint)
    {
        Notify($"{endpoint.Address} 에 짝 맺기를 요청합니다. 두 PC 화면에 코드가 뜹니다.");
        _ = Task.Run(() => _node.PairAsync(endpoint));
    }

    private void SetSide(Guid peer, NeighborSide side)
    {
        if (_store.Get(peer) is not { } rec) return;
        _store.Upsert(rec with { Side = side });
        _node.Send(peer, new LayoutSideMessage(side.Opposite()));
    }

    private void Unpair(PeerRecord p)
    {
        var answer = MessageBox.Show($"'{p.Name}' 와 짝을 풀까요?\n다시 쓰려면 짝 맺기를 새로 해야 합니다.",
            "ShareCom", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.OK) _node.Unpair(p.Id);
    }

    private void ToggleEncryption()
    {
        bool next = !_node.EncryptionEnabled;
        if (!next)
        {
            var answer = MessageBox.Show(
                "암호화를 끄면 같은 와이파이의 다른 기기가 키 입력(비밀번호 포함)을 엿볼 수 있습니다.\n" +
                "상대 PC도 암호화를 꺼야 실제로 평문으로 연결됩니다. 끌까요?",
                "ShareCom", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer != DialogResult.OK) return;
        }
        _node.EncryptionEnabled = next;
        _storage.Settings.EncryptionEnabled = next;
        SaveSettings();
        foreach (var peer in _node.ConnectedPeers) _node.Disconnect(peer); // 새 설정으로 다시 연결
    }

    private void SyncPeersToRouter()
    {
        var all = _store.GetAll();
        foreach (var p in all) _router.SetPeer(p.Id, p.Name, p.Side);
        foreach (var known in _knownPeers.Except(all.Select(p => p.Id)).ToList()) _router.RemovePeer(known);
        _knownPeers = all.Select(p => p.Id).ToHashSet();
    }

    private HashSet<Guid> _knownPeers = new();

    private void UpdateTrayState()
    {
        if (_controlling is { } c)
        {
            _tray.Icon = TrayIcons.Controlling;
            _tray.Text = Trim($"ShareCom - '{_store.Get(c)?.Name}' 조작 중");
        }
        else
        {
            var connected = _node.ConnectedPeers.Count;
            _tray.Icon = connected > 0 ? TrayIcons.Connected : TrayIcons.Idle;
            _tray.Text = Trim(connected > 0 ? $"ShareCom - {connected}대 연결됨" : "ShareCom - 연결된 PC 없음");
        }
    }

    private static string Trim(string s) => s.Length <= 63 ? s : s[..63];

    private void Notify(string text)
    {
        Log.Info(text);
        _ui.BeginInvoke(() => _tray.ShowBalloonTip(3000, "ShareCom", text, ToolTipIcon.None));
    }

    private void SaveSettings()
    {
        try { _storage.Save(); } catch (Exception ex) { Log.Error("설정 저장 실패", ex); }
    }

    private static ScreenRect[] LocalMonitors() =>
        Screen.AllScreens.OrderByDescending(s => s.Primary)
              .Select(s => new ScreenRect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height))
              .ToArray();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _router.ReturnHome();
            _hooks.Dispose();
            _injector.ReleaseAll();
            CursorHider.RestoreNow();
            try { _node.DisposeAsync().AsTask().Wait(2000); } catch { }
            _tray.Visible = false;
            _tray.Dispose();
            _ui.Dispose();
            Log.Info("종료");
        }
        base.Dispose(disposing);
    }
}
