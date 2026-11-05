using ShareCom.Core.Input;
using ShareCom.Core.Layout;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Tests;

/// <summary>
/// PC 두 대를 메시지로 이어 붙여, 실제 마우스 이동(훅) → 라우터 → 메시지 → 상대 재현(주입) 흐름을 흉내 낸다.
/// 메시지는 큐에 넣었다가 동작마다 다 배달한다(실제처럼 비동기, 다시 들어오기 없음).
/// </summary>
public class TwoPcSimulationTests
{
    private sealed class Pc : IInputSink
    {
        public readonly Guid Id;
        public readonly InputRouter Router;
        public (int X, int Y) Cursor;
        public bool Hidden;
        public Pc? Other;
        private readonly Queue<(Pc To, Message M)> _bus;
        private readonly ScreenRect _screen;

        public Pc(Guid id, ScreenRect screen, Queue<(Pc, Message)> bus)
        {
            Id = id;
            _screen = screen;
            _bus = bus;
            Router = new InputRouter(this);
            Router.SetSelf(id);
            Router.SetLocalScreens([screen]);
            Cursor = (screen.Width / 2, screen.Height / 2);
        }

        private (int, int) ClampToScreen(int x, int y) =>
            (Math.Clamp(x, _screen.X, _screen.Right - 1), Math.Clamp(y, _screen.Y, _screen.Bottom - 1));

        /// <summary>이 PC에 달린 실제 마우스를 움직인다. 훅이 삼키면 커서는 그대로(또는 라우터가 옮긴 자리)다.</summary>
        public void Physical(int dx, int dy)
        {
            var (nx, ny) = ClampToScreen(Cursor.X + dx, Cursor.Y + dy);
            var before = Cursor;
            bool swallowed = Router.OnMouseMove(nx, ny, injected: false);
            if (!swallowed && Cursor == before) Cursor = (nx, ny);
        }

        private void Inject(int x, int y)
        {
            Cursor = ClampToScreen(x, y);
            Router.OnMouseMove(Cursor.X, Cursor.Y, injected: true);
        }

        /// <summary>TrayApp.OnMessage 와 같은 처리.</summary>
        public void Deliver(Guid from, Message m)
        {
            switch (m)
            {
                case EnterControlMessage e: Router.BeginControlledBy(from); Inject(e.X, e.Y); break;
                case LeaveControlMessage: Router.EndControlledBy(from); break;
                case TakeOverMessage: Router.OnTakeOver(from); break;
                case MouseMoveMessage mm when Router.ControlledBy == from: Inject(mm.X, mm.Y); break;
            }
        }

        public void Send(Guid peer, Message message) => _bus.Enqueue((Other!, message));
        public void SetLocalCursor(int x, int y) => Cursor = (x, y);
        public void HideLocalCursor() => Hidden = true;
        public void ShowLocalCursor() => Hidden = false;
        public void ReleaseInjectedInput() { }
        public void ControllingChanged(Guid? peer) { }
    }

    private readonly Queue<(Pc To, Message M)> _bus = new();
    private readonly Pc _a, _b;

    public TwoPcSimulationTests()
    {
        var idA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var idB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
        var screen = new ScreenRect(0, 0, 1920, 1080);
        _a = new Pc(idA, screen, _bus);
        _b = new Pc(idB, screen, _bus);
        _a.Other = _b;
        _b.Other = _a;
        // A 왼쪽, B 오른쪽
        LayoutEntry[] layout =
        [
            new(idA, "A", 0, 0, new PcScreen([screen], 100)),
            new(idB, "B", 1920, 0, new PcScreen([screen], 100)),
        ];
        foreach (var pc in new[] { _a, _b })
        {
            pc.Router.SetLayout(layout);
            pc.Router.SetPeerConnected(pc.Other!.Id, true);
        }
    }

    private void Pump()
    {
        while (_bus.Count > 0)
        {
            var (to, m) = _bus.Dequeue();
            to.Deliver(to.Other!.Id, m);
        }
    }

    private void MoveA(int dx, int dy, int times = 1)
    {
        for (int i = 0; i < times; i++) { _a.Physical(dx, dy); Pump(); }
    }

    private void MoveB(int dx, int dy, int times = 1)
    {
        for (int i = 0; i < times; i++) { _b.Physical(dx, dy); Pump(); }
    }

    /// <summary>A 마우스로 B에 넘어가 조금 움직이다가 왼쪽 가장자리를 밀어 A로 돌아온다.</summary>
    private void VisitBAndReturn(int returnStep = 100)
    {
        MoveA(100, 0, 20); // 오른쪽 가장자리를 넘어 B로
        Assert.Equal(_b.Id, _a.Router.Controlling);
        MoveA(-20, 0, 5);
        MoveA(0, 30, 3);
        for (int i = 0; i < 2000 && _a.Router.Controlling is not null; i++) MoveA(-returnStep, 0); // B 왼쪽 가장자리를 넘어 A로
        Assert.Null(_a.Router.Controlling);
        Assert.Null(_b.Router.ControlledBy);
    }

    [Theory]
    [InlineData(100)] // 빠르게 돌아옴: B 포인터는 안쪽에 남는다
    [InlineData(7)]   // 천천히 돌아옴: B 포인터가 가장자리(x=0)에 닿은 뒤 넘어간다
    [InlineData(1)]
    public void A에서_B로_갔다가_돌아온_뒤_B의_마우스로_B_포인터를_움직일_수_있다(int returnStep)
    {
        VisitBAndReturn(returnStep);
        var start = _b.Cursor;

        MoveB(0, 10, 3); // B 마우스를 아래로 조금(왼쪽으로 밀지 않음)

        Assert.Null(_b.Router.Controlling); // B가 A를 조작하기 시작하면 안 된다
        Assert.False(_b.Hidden);
        Assert.Equal(start.Y + 30, _b.Cursor.Y);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(7)]
    public void A에서_B로_갔다가_돌아온_뒤_B의_마우스를_오른쪽으로_움직여도_B에_남는다(int returnStep)
    {
        VisitBAndReturn(returnStep);
        MoveB(5, 0, 3);
        Assert.Null(_b.Router.Controlling);
        Assert.False(_b.Hidden);
    }

    [Fact]
    public void 돌아온_뒤_B의_마우스로_왼쪽을_계속_밀면_A로_넘어간다()
    {
        VisitBAndReturn();
        MoveB(-50, 0, 3);
        Assert.Equal(_a.Id, _b.Router.Controlling);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(7)]
    public void 다시_B로_넘어가면_A_마우스로_B_포인터가_움직인다(int returnStep)
    {
        VisitBAndReturn(returnStep);
        MoveB(0, 10, 3);
        MoveA(100, 0, 20);
        Assert.Equal(_b.Id, _a.Router.Controlling);
        var y = _b.Cursor.Y;
        MoveA(0, 20, 3);
        Assert.Equal(y + 60, _b.Cursor.Y);
    }
}
