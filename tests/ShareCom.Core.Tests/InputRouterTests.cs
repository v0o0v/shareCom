using ShareCom.Core.Input;
using ShareCom.Core.Layout;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Tests;

public sealed class FakeSink : IInputSink
{
    public List<(Guid Peer, Message Message)> Sent { get; } = new();
    public (int X, int Y) Cursor { get; private set; }
    public bool Hidden { get; private set; }
    public int Releases { get; private set; }
    public Guid? Controlling { get; private set; }

    public void Send(Guid peer, Message message) => Sent.Add((peer, message));
    public void SetLocalCursor(int x, int y) => Cursor = (x, y);
    public void HideLocalCursor() => Hidden = true;
    public void ShowLocalCursor() => Hidden = false;
    public void ReleaseInjectedInput() => Releases++;
    public void ControllingChanged(Guid? peer) => Controlling = peer;

    public List<Message> To(Guid peer) => Sent.Where(s => s.Peer == peer).Select(s => s.Message).ToList();
}

public class InputRouterTests
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Peer = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private readonly FakeSink _sink = new();
    private readonly InputRouter _router;

    private static LayoutEntry E(Guid id, int x, int y, ScreenRect monitor, int scale = 100) =>
        new(id, id.ToString()[^1..], x, y, new PcScreen([monitor], scale));

    private static readonly LayoutEntry MeEntry = E(Me, 0, 0, new(0, 0, 1920, 1080));
    // 상대(1280×1024)는 내 오른쪽에 세로 가운데를 맞춰 놓는다.
    private static readonly LayoutEntry PeerRight = E(Peer, 1920, 28, new(0, 0, 1280, 1024));

    public InputRouterTests()
    {
        _router = new InputRouter(_sink);
        _router.SetSelf(Me);
        _router.SetLocalScreens([new ScreenRect(0, 0, 1920, 1080)]);
        _router.SetLayout([MeEntry, PeerRight]);
        _router.SetPeerConnected(Peer, true);
    }

    private void EnterRight()
    {
        Assert.True(_router.OnMouseMove(1919, 540, injected: false));
        Assert.Equal(Peer, _router.Controlling);
    }

    [Fact]
    public void 오른쪽_가장자리에_닿으면_오른쪽_PC로_넘어간다()
    {
        EnterRight();
        var enter = Assert.IsType<EnterControlMessage>(_sink.To(Peer).Last());
        Assert.Equal(1, enter.X);
        Assert.Equal(512, enter.Y); // 배치 평면에서 같은 높이(540 - 28)
        Assert.True(_sink.Hidden);
        Assert.Equal((960, 540), _sink.Cursor); // 내 커서는 주 모니터 가운데에 숨긴다
        Assert.Equal(Peer, _sink.Controlling);
    }

    [Fact]
    public void 넘어간_뒤의_이동은_상대_화면_좌표로_보낸다()
    {
        EnterRight();
        var y0 = ((EnterControlMessage)_sink.To(Peer).Last()).Y;
        Assert.True(_router.OnMouseMove(960 + 30, 540 + 5, false));
        Assert.Equal(new MouseMoveMessage(31, y0 + 5), _sink.To(Peer).Last());
        // 상대 화면 밖으로는 못 나간다(아래쪽은 이웃이 없음)
        Assert.True(_router.OnMouseMove(960, 540 + 190, false));
        Assert.True(_router.OnMouseMove(960, 540 + 190, false));
        Assert.True(_router.OnMouseMove(960, 540 + 190, false));
        Assert.Equal(1023, ((MouseMoveMessage)_sink.To(Peer).Last()).Y);
    }

    [Fact]
    public void 넘어가기_전에_쌓인_큰_이동은_버린다()
    {
        EnterRight();
        int before = _sink.To(Peer).Count;
        Assert.True(_router.OnMouseMove(1919, 540, false)); // 가운데로 옮기기 전 이벤트
        Assert.Equal(before, _sink.To(Peer).Count);
    }

    [Fact]
    public void 상대_화면의_내_쪽_가장자리를_밀면_돌아온다()
    {
        EnterRight();
        Assert.True(_router.OnMouseMove(960 - 50, 540, false)); // 상대 x=1 에서 왼쪽으로 50 → 밀기
        Assert.Null(_router.Controlling);
        Assert.IsType<LeaveControlMessage>(_sink.To(Peer).Last());
        Assert.False(_sink.Hidden);
        Assert.Equal(1917, _sink.Cursor.X);
        Assert.InRange(_sink.Cursor.Y, 538, 542);
        // 돌아온 직후 같은 자리에서 바로 다시 넘어가지 않는다
        Assert.False(_router.OnMouseMove(1916, 540, false));
        Assert.Null(_router.Controlling);
    }

    [Fact]
    public void 모서리에서는_넘어가지_않는다()
    {
        Assert.False(_router.OnMouseMove(1919, 3, false));
        Assert.False(_router.OnMouseMove(1919, 1078, false));
        Assert.Null(_router.Controlling);
    }

    [Fact]
    public void 이웃이_없는_쪽이나_연결이_끊긴_이웃_쪽으로는_넘어가지_않는다()
    {
        Assert.False(_router.OnMouseMove(0, 540, false));
        _router.SetPeerConnected(Peer, false);
        Assert.False(_router.OnMouseMove(1919, 540, false));
        Assert.Null(_router.Controlling);
    }

    [Fact]
    public void 왼쪽_이웃이면_왼쪽_가장자리에서_넘어가_오른쪽_끝에_들어간다()
    {
        _router.SetLayout([MeEntry, E(Peer, -1280, 28, new(0, 0, 1280, 1024))]);
        Assert.True(_router.OnMouseMove(0, 540, false));
        var enter = Assert.IsType<EnterControlMessage>(_sink.To(Peer).Last());
        Assert.Equal(1278, enter.X);
        // 오른쪽으로 밀면 돌아온다
        Assert.True(_router.OnMouseMove(960 + 10, 540, false));
        Assert.Null(_router.Controlling);
        Assert.Equal(2, _sink.Cursor.X);
    }

    [Fact]
    public void 가장자리_잠금_중에는_넘어가지_않고_ScrollLock_으로_켜고_끈다()
    {
        bool? notified = null;
        _router.EdgeLockChanged += v => notified = v;
        Assert.False(_router.OnKey(InputRouter.VkScrollLock, 0x46, false, true, false));
        _router.OnKey(InputRouter.VkScrollLock, 0x46, false, false, false);
        Assert.True(_router.EdgeLocked);
        Assert.True(notified);
        Assert.False(_router.OnMouseMove(1919, 540, false));
        Assert.Null(_router.Controlling);

        _router.OnKey(InputRouter.VkScrollLock, 0x46, false, true, false);
        Assert.False(_router.EdgeLocked);
        EnterRight();
    }

    [Fact]
    public void 마우스_버튼을_누른_채로는_넘어가지_않는다()
    {
        Assert.False(_router.OnMouseButton(MouseButtonKind.Left, true, false));
        Assert.False(_router.OnMouseMove(1919, 540, false));
        Assert.Null(_router.Controlling);
        _router.OnMouseButton(MouseButtonKind.Left, false, false);
        EnterRight();
    }

    [Fact]
    public void 다른_프로그램이_만든_입력은_건드리지_않는다()
    {
        Assert.False(_router.OnMouseMove(1919, 540, injected: true));
        Assert.Null(_router.Controlling);
        EnterRight();
        Assert.False(_router.OnKey(0x41, 0x1E, false, true, injected: true));
    }

    [Fact]
    public void 넘어간_동안_키와_버튼과_휠은_상대로_가고_내_PC에서는_삼킨다()
    {
        EnterRight();
        Assert.True(_router.OnKey(0x41, 0x1E, false, true, false));
        Assert.Equal(new KeyMessage(0x41, 0x1E, false, true), _sink.To(Peer).Last());
        Assert.True(_router.OnMouseButton(MouseButtonKind.Right, true, false));
        Assert.Equal(new MouseButtonMessage(MouseButtonKind.Right, true), _sink.To(Peer).Last());
        Assert.True(_router.OnMouseWheel(-120, false, false));
        Assert.Equal(new MouseWheelMessage(-120, false), _sink.To(Peer).Last());
    }

    [Fact]
    public void 내_PC에서_누른_키는_넘어간_뒤에도_내_PC에서_뗀다()
    {
        Assert.False(_router.OnKey(0xA0, 0x2A, false, true, false)); // Shift 누름(내 PC)
        EnterRight();
        Assert.False(_router.OnKey(0xA0, 0x2A, false, false, false)); // 떼기는 내 PC로
        Assert.DoesNotContain(_sink.To(Peer), m => m is KeyMessage);
    }

    [Fact]
    public void 돌아올_때_상대에_눌러_둔_키를_떼고_실제로_떼는_키는_삼킨다()
    {
        EnterRight();
        _router.OnKey(0x41, 0x1E, false, true, false); // A 누른 채로
        Assert.True(_router.OnMouseMove(960 - 50, 540, false)); // 돌아옴
        var msgs = _sink.To(Peer);
        Assert.Contains(new KeyMessage(0x41, 0x1E, false, false), msgs);
        Assert.IsType<LeaveControlMessage>(msgs.Last());
        // 실제 손을 뗄 때 내 PC에는 짝 없는 떼기가 가지 않게 삼킨다
        Assert.True(_router.OnKey(0x41, 0x1E, false, false, false));
        // 그다음 누름은 평소처럼 내 PC로
        Assert.False(_router.OnKey(0x41, 0x1E, false, true, false));
    }

    [Fact]
    public void Ctrl_Alt_Esc_는_무조건_내_PC로_돌아온다()
    {
        EnterRight();
        _router.OnKey(InputRouter.VkLControl, 0x1D, false, true, false);
        _router.OnKey(InputRouter.VkLMenu, 0x38, false, true, false);
        Assert.True(_router.OnKey(InputRouter.VkEscape, 0x01, false, true, false));
        Assert.Null(_router.Controlling);
        var msgs = _sink.To(Peer);
        Assert.Contains(new KeyMessage(InputRouter.VkLControl, 0x1D, false, false), msgs);
        Assert.Contains(new KeyMessage(InputRouter.VkLMenu, 0x38, false, false), msgs);
        Assert.DoesNotContain(new KeyMessage(InputRouter.VkEscape, 0x01, false, true), msgs);
        Assert.Equal((960, 540), _sink.Cursor);
        Assert.True(_router.OnKey(InputRouter.VkEscape, 0x01, false, false, false)); // Esc 떼기도 삼킴
        Assert.True(_router.OnKey(InputRouter.VkLMenu, 0x38, false, false, false));  // 상대 몫이던 Alt 떼기 삼킴
    }

    [Fact]
    public void Ctrl_Alt_숫자로_PC를_바로_고른다()
    {
        _router.SetLayout([MeEntry, PeerRight, E(Other, -800, 240, new(0, 0, 800, 600))]);
        _router.SetPeerConnected(Other, true);
        Assert.Equal([Other, Peer], _router.JumpOrder()); // 배치에서 왼쪽부터: 왼쪽 PC가 2번, 오른쪽 PC가 3번

        _router.OnKey(InputRouter.VkLControl, 0x1D, false, true, false);
        _router.OnKey(InputRouter.VkLMenu, 0x38, false, true, false);
        Assert.True(_router.OnKey(0x33, 0x04, false, true, false)); // Ctrl+Alt+3
        Assert.Equal(Peer, _router.Controlling);
        Assert.Equal(new EnterControlMessage(640, 512), _sink.To(Peer).Last());

        Assert.True(_router.OnKey(0x32, 0x03, false, true, false)); // Ctrl+Alt+2
        Assert.Equal(Other, _router.Controlling);
        Assert.IsType<LeaveControlMessage>(_sink.To(Peer).Last());

        Assert.True(_router.OnKey(0x31, 0x02, false, true, false)); // Ctrl+Alt+1 = 내 PC
        Assert.Null(_router.Controlling);
    }

    [Fact]
    public void 조작하던_PC와_연결이_끊기면_곧바로_돌아온다()
    {
        EnterRight();
        _router.SetPeerConnected(Peer, false);
        Assert.Null(_router.Controlling);
        Assert.False(_sink.Hidden);
        Assert.Null(_sink.Controlling);
        Assert.False(_router.OnKey(0x41, 0x1E, false, true, false));
    }

    [Fact]
    public void 상대가_주인을_되찾으면_돌아온다()
    {
        EnterRight();
        _router.OnTakeOver(Peer);
        Assert.Null(_router.Controlling);
        Assert.False(_sink.Hidden);
    }

    [Fact]
    public void 조작당하는_중에_실제_마우스를_움직이면_주인을_되찾는다()
    {
        _router.BeginControlledBy(Peer);
        _router.OnMouseMove(500, 500, injected: true);
        Assert.False(_router.OnMouseMove(503, 501, injected: false)); // 조금 흔들림은 무시
        Assert.DoesNotContain(_sink.To(Peer), m => m is TakeOverMessage);
        Assert.False(_router.OnMouseMove(520, 505, injected: false));
        Assert.Contains(_sink.To(Peer), m => m is TakeOverMessage);
        Assert.Equal(1, _sink.Releases);
        Assert.Null(_router.ControlledBy);
    }

    [Fact]
    public void 조작당하는_중에_실제_키를_누르면_주인을_되찾는다()
    {
        _router.BeginControlledBy(Peer);
        Assert.False(_router.OnKey(0x41, 0x1E, false, true, false));
        Assert.Contains(_sink.To(Peer), m => m is TakeOverMessage);
    }

    [Fact]
    public void 조작당하는_중에는_가장자리로_넘어가지_않는다()
    {
        _router.BeginControlledBy(Other);
        Assert.False(_router.OnMouseMove(1919, 540, injected: true));
        Assert.Null(_router.Controlling);
    }

    [Fact]
    public void 모니터가_여럿이면_가장_바깥_모니터의_가장자리에서만_넘어간다()
    {
        _router.SetLocalScreens([new ScreenRect(0, 0, 1920, 1080), new ScreenRect(1920, 0, 1280, 720)]);
        _router.SetLayout([MeEntry, E(Peer, 3200, 0, new(0, 0, 1280, 1024))]);
        Assert.False(_router.OnMouseMove(1919, 540, false)); // 두 모니터 사이
        Assert.False(_router.OnMouseMove(1919, 900, false)); // 첫 모니터 오른쪽 아래: 맞닿은 PC가 없다
        Assert.Null(_router.Controlling);
        Assert.True(_router.OnMouseMove(3199, 360, false));
        Assert.Equal(Peer, _router.Controlling);
    }

    private void Push(int dx, int dy, int times)
    {
        for (int i = 0; i < times; i++) _router.OnMouseMove(960 + dx, 540 + dy, false);
    }

    [Fact]
    public void 조작하던_PC의_반대쪽_가장자리를_밀면_그_옆_PC로_바로_넘어간다()
    {
        // 나 | Peer(1280×1024) | Other(800×600)
        _router.SetLayout([MeEntry, PeerRight, E(Other, 3200, 240, new(0, 0, 800, 600))]);
        _router.SetPeerConnected(Other, true);
        EnterRight();
        Push(150, 0, 10);
        Assert.Equal(Other, _router.Controlling);
        Assert.IsType<LeaveControlMessage>(_sink.To(Peer).Last());
        Assert.Equal(new EnterControlMessage(1, 300), _sink.To(Other).OfType<EnterControlMessage>().Single()); // 평면 높이 540 → Other 안에서 300
        Assert.Equal(Other, _sink.Controlling);

        // 왼쪽으로 밀면 내 PC가 아니라 Peer 로 간다.
        Push(-50, 0, 4);
        Assert.Equal(Peer, _router.Controlling);
        Assert.Equal(1278, ((EnterControlMessage)_sink.To(Peer).Last()).X);
    }

    [Fact]
    public void 연결되지_않은_PC_쪽은_벽이다()
    {
        _router.SetLayout([MeEntry, PeerRight, E(Other, 3200, 240, new(0, 0, 800, 600))]);
        EnterRight();
        Push(150, 0, 12);
        Assert.Equal(Peer, _router.Controlling);
        Assert.Equal(1279, ((MouseMoveMessage)_sink.To(Peer).Last()).X);
    }

    [Fact]
    public void 아래에_놓은_PC로는_아래_가장자리에서_넘어간다()
    {
        _router.SetLayout([MeEntry, E(Other, 0, 1080, new(0, 0, 1920, 1080))]);
        _router.SetPeerConnected(Other, true);
        Assert.False(_router.OnMouseMove(5, 1079, false)); // 모서리 근처는 넘어가지 않는다
        Assert.True(_router.OnMouseMove(960, 1079, false));
        Assert.Equal(Other, _router.Controlling);
        Assert.Equal(new EnterControlMessage(960, 1), _sink.To(Other).Last());
        Push(0, -10, 1);
        Assert.Null(_router.Controlling);
        Assert.Equal((960, 1077), _sink.Cursor);
    }

    [Fact]
    public void 배율이_다른_PC는_논리_크기로_맞춰_넘어간다()
    {
        // 4K 200% 모니터는 평면에서 1920×1080 크기다.
        _router.SetLayout([MeEntry, E(Peer, 1920, 0, new(0, 0, 3840, 2160), scale: 200)]);
        Assert.True(_router.OnMouseMove(1919, 540, false));
        Assert.Equal(new EnterControlMessage(1, 1081), _sink.To(Peer).Last());
    }

    [Fact]
    public void 맞닿은_구간_밖에서_밀어도_가장_가까운_끝으로_넘어간다()
    {
        // 상대는 내 오른쪽 위 600 픽셀만 맞닿는다.
        _router.SetLayout([MeEntry, E(Peer, 1920, 0, new(0, 0, 800, 600))]);
        Assert.True(_router.OnMouseMove(1919, 900, false));
        Assert.Equal(new EnterControlMessage(1, 599), _sink.To(Peer).Last());
    }

    [Fact]
    public void 배치에_없는_PC로는_넘어가지_않는다()
    {
        _router.SetLayout([MeEntry]);
        Assert.False(_router.OnMouseMove(1919, 540, false));
        Assert.Null(_router.Controlling);
    }
}
