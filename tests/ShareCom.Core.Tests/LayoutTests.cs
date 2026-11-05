using ShareCom.Core.Layout;
using ShareCom.Core.Net;
using ShareCom.Core.Protocol;
using Box = ShareCom.Core.Layout.DeskLayout.Box;

namespace ShareCom.Core.Tests;

public class DeskLayoutTests
{
    [Fact]
    public void 논리_크기는_실제_픽셀을_배율로_나눈_값이다()
    {
        Assert.Equal((1920, 1080), DeskLayout.LogicalSize(new PcScreen([new(0, 0, 3840, 2160)], 200)));
        Assert.Equal((2816, 864), DeskLayout.LogicalSize(new PcScreen([new(0, 0, 1920, 1080), new(-1600, 0, 800, 600)], 125)));
    }

    [Fact]
    public void 다른_PC_옆_가까이_놓으면_그_가장자리에_딱_붙고_끝을_맞춘다()
    {
        var others = new[] { new Box(0, 0, 1920, 1080) };
        Assert.Equal((1920, 0), DeskLayout.Snap(others, 1280, 1024, 1950, 15));   // 오른쪽, 위끝 맞춤
        Assert.Equal((-1280, 56), DeskLayout.Snap(others, 1280, 1024, -1300, 60)); // 왼쪽, 아래끝 맞춤(1080-1024)
        Assert.Equal((200, 1080), DeskLayout.Snap(others, 1280, 1024, 200, 1200)); // 아래
        Assert.Equal((320, 1080), DeskLayout.Snap(others, 1280, 1024, 300, 1200)); // 아래, 가운데 맞춤으로 끌려감
    }

    [Fact]
    public void 겹치게_놓으면_겹치지_않는_가장_가까운_자리로_밀려난다()
    {
        var others = new[] { new Box(0, 0, 1920, 1080), new Box(1920, 0, 1280, 1024) };
        var (x, y) = DeskLayout.Snap(others, 800, 600, 1800, 100);
        var placed = new Box(x, y, 800, 600);
        Assert.DoesNotContain(others, o => o.Overlaps(placed));
        // 어느 한 PC와는 맞닿는다
        Assert.Contains(others, o => o.Right == x || o.X == x + 800 || o.Bottom == y || o.Y == y + 600);
    }

    [Fact]
    public void 멀리_떨어뜨려도_가장_가까운_면에_붙고_최소한_겹쳐_맞닿는다()
    {
        var others = new[] { new Box(0, 0, 1920, 1080) };
        var (x, y) = DeskLayout.Snap(others, 1280, 1024, 5000, 3000);
        Assert.True(x == 1920 || y == 1080);
        if (x == 1920) Assert.True(y <= 1080 - DeskLayout.MinOverlap);
        else Assert.True(x <= 1920 - DeskLayout.MinOverlap);
    }

    [Fact]
    public void 처음_들어가는_PC는_짝_맺을_때_정한_이웃_옆이나_맨_오른쪽에_놓인다()
    {
        var neighbor = new Box(0, 0, 1920, 1080);
        Assert.Equal((0, 0), DeskLayout.AutoPlace([], 1280, 1024, null));
        Assert.Equal((1920, 28), DeskLayout.AutoPlace([neighbor], 1280, 1024, null));
        // 이웃이 내 오른쪽이면 나는 그 왼쪽
        Assert.Equal((-1280, 28), DeskLayout.AutoPlace([neighbor], 1280, 1024, (neighbor, NeighborSide.Right)));
    }

    [Fact]
    public void PC_번호는_배치에서_왼쪽부터_같으면_위부터다()
    {
        Guid me = Guid.NewGuid(), a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var s = new PcScreen([new(0, 0, 100, 100)], 100);
        var order = DeskLayout.JumpOrder([new(me, "", 0, 0, s), new(a, "", 100, 0, s), new(b, "", -100, 0, s), new(c, "", 100, -100, s)], me);
        Assert.Equal([b, c, a], order);
    }
}

public sealed class FakeMessenger : IPeerMessenger
{
    public List<Guid> Peers { get; } = new();
    public List<(Guid Peer, Message Message)> Sent { get; } = new();
    public IReadOnlyList<Guid> ConnectedPeers => Peers;
    public bool Send(Guid peer, Message message) { Sent.Add((peer, message)); return true; }
    public LayoutDoc? LastTo(Guid peer) => Sent.Where(s => s.Peer == peer).Select(s => s.Message).OfType<LayoutMessage>().LastOrDefault()?.Doc;
}

public class LayoutSyncTests
{
    private static readonly Guid Me = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Peer = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly PcScreen Hd = new([new(0, 0, 1920, 1080)], 100);
    private static readonly PcScreen Small = new([new(0, 0, 1280, 1024)], 100);

    private readonly FakeMessenger _net = new();
    private readonly HashSet<Guid> _revoked = new();
    private readonly Dictionary<Guid, NeighborSide> _sides = new();
    private readonly LayoutSync _sync;
    private LayoutDoc? _changed;

    public LayoutSyncTests()
    {
        _net.Peers.Add(Peer);
        _sync = new LayoutSync(Me, _net, id => _sides.TryGetValue(id, out var s) ? s : null, _revoked.Contains);
        _sync.Changed += d => _changed = d;
    }

    [Fact]
    public void 처음에는_내_항목을_원점에_넣고_연결된_PC에_보낸다()
    {
        _sync.SetSelf("나", Hd);
        var doc = _sync.Doc;
        Assert.Equal(1, doc.Version);
        var me = Assert.Single(doc.Entries);
        Assert.Equal((Me, 0, 0), (me.Id, me.X, me.Y));
        Assert.Same(doc, _changed);
        Assert.Same(doc, _net.LastTo(Peer));
    }

    [Fact]
    public void 더_새_판을_받으면_고르고_내_항목이_없으면_이웃_옆에_넣어_다시_퍼뜨린다()
    {
        _sync.SetSelf("나", Hd);
        _sides[Peer] = NeighborSide.Right; // 짝 맺을 때 상대를 내 오른쪽에 두었다
        var theirs = new LayoutDoc(5, Peer, [new LayoutEntry(Peer, "상대", 0, 0, Small)]);
        _sync.OnMessage(Peer, new LayoutMessage(theirs));

        var doc = _sync.Doc;
        Assert.Equal(6, doc.Version);
        Assert.Equal(Me, doc.Editor);
        var me = doc.Find(Me)!;
        Assert.Equal(-1920, me.X); // 상대의 왼쪽
        Assert.Same(doc, _net.LastTo(Peer)); // 고쳤으므로 보낸 PC에도 보낸다
    }

    [Fact]
    public void 내_항목이_이미_맞으면_받은_판을_그대로_쓰고_보낸_PC에는_되돌려_보내지_않는다()
    {
        _sync.SetSelf("나", Hd);
        var other = Guid.NewGuid();
        _net.Peers.Add(other);
        int before = _net.Sent.Count(s => s.Peer == Peer);
        var theirs = new LayoutDoc(9, Peer, [new LayoutEntry(Me, "나", 0, 0, Hd), new LayoutEntry(Peer, "상대", 1920, 0, Small)]);
        _sync.OnMessage(Peer, new LayoutMessage(theirs));
        Assert.Same(theirs, _sync.Doc);
        Assert.Equal(before, _net.Sent.Count(s => s.Peer == Peer));
        Assert.Same(theirs, _net.LastTo(other)); // 다른 PC에는 전한다
    }

    [Fact]
    public void 오래된_판은_무시한다()
    {
        _sync.SetSelf("나", Hd);
        _sync.ApplyPositions(new Dictionary<Guid, (int, int)> { [Me] = (10, 10) });
        var current = _sync.Doc;
        _sync.OnMessage(Peer, new LayoutMessage(new LayoutDoc(1, Peer, [])));
        Assert.Same(current, _sync.Doc);
    }

    [Fact]
    public void 내_화면이_바뀌면_자리는_두고_화면만_고친다()
    {
        _sync.SetSelf("나", Hd);
        _sync.ApplyPositions(new Dictionary<Guid, (int, int)> { [Me] = (100, 200) });
        long v = _sync.Doc.Version;
        _sync.SetSelf("나", Small);
        var me = _sync.Doc.Find(Me)!;
        Assert.Equal((100, 200), (me.X, me.Y));
        Assert.True(me.Screen.SameAs(Small));
        Assert.Equal(v + 1, _sync.Doc.Version);
        // 같은 화면이면 판을 올리지 않는다
        _sync.SetSelf("나", Small);
        Assert.Equal(v + 1, _sync.Doc.Version);
    }

    [Fact]
    public void 편집기에서_옮긴_위치는_새_판으로_퍼진다()
    {
        _sync.SetSelf("나", Hd);
        _sync.OnMessage(Peer, new LayoutMessage(new LayoutDoc(3, Peer, [new LayoutEntry(Me, "나", 0, 0, Hd), new LayoutEntry(Peer, "상대", 1920, 0, Small)])));
        _sync.ApplyPositions(new Dictionary<Guid, (int, int)> { [Peer] = (0, 1080) });
        var doc = _sync.Doc;
        Assert.Equal(4, doc.Version);
        Assert.Equal(Me, doc.Editor);
        Assert.Equal((0, 1080), (doc.Find(Peer)!.X, doc.Find(Peer)!.Y));
        Assert.Same(doc, _net.LastTo(Peer));
    }

    [Fact]
    public void 짝을_푼_PC_항목은_지운다()
    {
        _sync.SetSelf("나", Hd);
        _sync.OnMessage(Peer, new LayoutMessage(new LayoutDoc(3, Peer, [new LayoutEntry(Me, "나", 0, 0, Hd), new LayoutEntry(Peer, "상대", 1920, 0, Small)])));
        _revoked.Add(Peer);
        _sync.Refresh();
        Assert.Single(_sync.Doc.Entries);
        Assert.Equal(4, _sync.Doc.Version);
    }

    [Fact]
    public void 두_PC가_서로_판을_주고받으면_같은_배치로_모인다()
    {
        var netA = new FakeMessenger();
        var netB = new FakeMessenger();
        var a = new LayoutSync(Me, netA, _ => null, _ => false);
        var b = new LayoutSync(Peer, netB, _ => null, _ => false);
        a.SetSelf("A", Hd);
        b.SetSelf("B", Small);
        netA.Peers.Add(Peer);
        netB.Peers.Add(Me);

        // 연결됨: 서로 판을 보낸다. 받은 메시지를 다 처리할 때까지 주고받는다.
        a.OnConnected(Peer);
        b.OnConnected(Me);
        for (int round = 0; round < 10; round++)
        {
            var toB = netA.Sent.ToList(); netA.Sent.Clear();
            var toA = netB.Sent.ToList(); netB.Sent.Clear();
            if (toB.Count == 0 && toA.Count == 0) break;
            foreach (var (_, m) in toB) b.OnMessage(Me, m);
            foreach (var (_, m) in toA) a.OnMessage(Peer, m);
        }
        Assert.Equal(a.Doc.Version, b.Doc.Version);
        Assert.Equal(2, a.Doc.Entries.Length);
        Assert.Equal(a.Doc.Entries.Select(e => (e.Id, e.X, e.Y)).OrderBy(e => e.Id), b.Doc.Entries.Select(e => (e.Id, e.X, e.Y)).OrderBy(e => e.Id));
        var boxA = DeskLayout.BoxOf(a.Doc.Find(Me)!);
        var boxB = DeskLayout.BoxOf(a.Doc.Find(Peer)!);
        Assert.False(boxA.Overlaps(boxB));
    }
}

public class LayoutRepairTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly PcScreen Hd = new([new(0, 0, 1920, 1080)], 100);

    private readonly FakeMessenger _netA = new(), _netB = new();
    private readonly HashSet<Guid> _revokedByA = new(), _revokedByB = new();
    private LayoutSync _a, _b;

    public LayoutRepairTests()
    {
        _a = new LayoutSync(A, _netA, _ => NeighborSide.Right, _revokedByA.Contains);
        _b = new LayoutSync(B, _netB, _ => NeighborSide.Left, _revokedByB.Contains);
        _a.SetSelf("A", Hd);
        _b.SetSelf("B", Hd);
        Connect();
        // 사용자가 배치 편집기에서 B 를 A 왼쪽으로 옮긴다(자동 배치는 오른쪽).
        _a.ApplyPositions(new Dictionary<Guid, (int, int)> { [A] = (0, 0), [B] = (-1920, 0) });
        Exchange();
        Assert.Equal(-1920, _b.Doc.Find(B)!.X);
        Assert.Equal(0, _b.Doc.Find(A)!.X);
    }

    private void Connect()
    {
        _netA.Peers.Add(B);
        _netB.Peers.Add(A);
        _a.OnConnected(B);
        _b.OnConnected(A);
        Exchange();
    }

    private void Disconnect()
    {
        _netA.Peers.Clear();
        _netB.Peers.Clear();
        _netA.Sent.Clear();
        _netB.Sent.Clear();
    }

    private void Exchange()
    {
        for (int round = 0; round < 10; round++)
        {
            var toB = _netA.Sent.ToList(); _netA.Sent.Clear();
            var toA = _netB.Sent.ToList(); _netB.Sent.Clear();
            if (toB.Count == 0 && toA.Count == 0) return;
            foreach (var (_, m) in toB) _b.OnMessage(A, m);
            foreach (var (_, m) in toA) _a.OnMessage(B, m);
        }
    }

    /// <summary>짝 풀기(양쪽에 퍼짐) → 끊김 → 다시 짝 맺기(양쪽 풀린 표시 지움) → 연결.</summary>
    private void UnpairAndRepair(Action? whileUnpaired = null)
    {
        _revokedByA.Add(B);
        _a.Refresh();
        _revokedByB.Add(A);
        _b.Refresh();
        Disconnect();
        Assert.Null(_a.Doc.Find(B)); // 짝을 푼 동안에는 배치에 없다
        whileUnpaired?.Invoke();
        _revokedByA.Remove(B);
        _a.Refresh();
        _revokedByB.Remove(A);
        _b.Refresh();
        Connect();
    }

    [Fact]
    public void 다시_짝을_맺으면_예전_배치_자리로_돌아간다()
    {
        UnpairAndRepair();
        Assert.Equal(-1920, _a.Doc.Find(B)!.X);
        Assert.Equal(-1920, _b.Doc.Find(B)!.X);
        Assert.Equal(0, _b.Doc.Find(A)!.X);
        Assert.Equal(_a.Doc.Version, _b.Doc.Version);
    }

    [Fact]
    public void 짝을_푼_동안_껐다_켜도_자리를_기억한다()
    {
        UnpairAndRepair(whileUnpaired: () =>
        {
            // 두 PC 모두 다시 켠다: 저장해 둔 배치와 기억한 자리로 새로 만든다.
            _a = new LayoutSync(A, _netA, _ => NeighborSide.Right, _revokedByA.Contains, _a.Doc, _a.Remembered);
            _b = new LayoutSync(B, _netB, _ => NeighborSide.Left, _revokedByB.Contains, _b.Doc, _b.Remembered);
            _a.SetSelf("A", Hd);
            _b.SetSelf("B", Hd);
        });
        Assert.Equal(-1920, _a.Doc.Find(B)!.X);
        Assert.Equal(-1920, _b.Doc.Find(B)!.X);
    }

    [Fact]
    public void 기억한_자리가_다른_PC와_겹치면_되살리지_않는다()
    {
        var c = Guid.Parse("00000000-0000-0000-0000-00000000000c");
        _revokedByA.Add(B);
        _a.Refresh();
        Disconnect();
        // 짝을 푼 동안 다른 PC C 가 그 자리에 들어왔다.
        _a.OnMessage(c, new LayoutMessage(new LayoutDoc(_a.Doc.Version + 1, c,
            [.. _a.Doc.Entries, new LayoutEntry(c, "C", -1920, 0, Hd)])));
        _revokedByA.Remove(B);
        _a.Refresh();
        Assert.Null(_a.Doc.Find(B)); // 겹치는 자리에 되살리지 않는다(B 가 연결되면 스스로 새 자리에 들어온다)
        Assert.Empty(_a.Remembered);
    }
}

public class LayoutJsonTests
{
    [Fact]
    public void 배치_판은_설정_파일_JSON으로_저장했다_그대로_읽힌다()
    {
        var doc = new LayoutDoc(7, Guid.NewGuid(),
        [
            new LayoutEntry(Guid.NewGuid(), "가", 0, 0, new PcScreen([new(0, 0, 1920, 1080), new(-1280, 0, 1280, 1024)], 125)),
            new LayoutEntry(Guid.NewGuid(), "나", 1536, -20, new PcScreen([new(0, 0, 2560, 1440)], 100)),
        ]);
        var json = System.Text.Json.JsonSerializer.Serialize(doc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var back = System.Text.Json.JsonSerializer.Deserialize<LayoutDoc>(json)!;
        Assert.Equal(doc.Version, back.Version);
        Assert.Equal(doc.Editor, back.Editor);
        Assert.Equal(doc.Entries.Length, back.Entries.Length);
        for (int i = 0; i < doc.Entries.Length; i++)
        {
            var (a, b) = (doc.Entries[i], back.Entries[i]);
            Assert.Equal((a.Id, a.Name, a.X, a.Y), (b.Id, b.Name, b.X, b.Y));
            Assert.True(a.Screen.SameAs(b.Screen));
        }
    }
}
