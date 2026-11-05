using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ShareCom.Core.Identity;
using ShareCom.Core.Net;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Tests;

/// <summary>한 프로세스 안의 노드들이 서로를 "검색"하게 해 주는 가짜 검색.</summary>
public sealed class FakeDiscoveryHub
{
    private readonly List<FakeDiscovery> _members = new();
    public FakeDiscovery Join() { var d = new FakeDiscovery(this); lock (_members) _members.Add(d); return d; }
    internal void Leave(FakeDiscovery d) { lock (_members) _members.Remove(d); }

    internal void Broadcast(FakeDiscovery from, DiscoveryAnnouncement a)
    {
        FakeDiscovery[] others;
        lock (_members) others = _members.Where(m => m != from).ToArray();
        foreach (var o in others) o.Receive(new DiscoveredPeer(a.Id, a.Name, new IPEndPoint(IPAddress.Loopback, a.TcpPort), DateTime.UtcNow));
    }
}

public sealed class FakeDiscovery(FakeDiscoveryHub hub) : IDiscovery
{
    private DiscoveryAnnouncement? _self;
    private Timer? _timer;
    public event Action<DiscoveredPeer>? PeerSeen;
    public bool Silent { get; set; }

    public void Start(DiscoveryAnnouncement self)
    {
        _self = self;
        _timer = new Timer(_ => AnnounceNow(), null, 0, 200);
    }

    public void AnnounceNow() { if (_self is not null && !Silent) hub.Broadcast(this, _self); }
    internal void Receive(DiscoveredPeer p) { if (!Silent) PeerSeen?.Invoke(p); }
    public void Dispose() { _timer?.Dispose(); hub.Leave(this); }
}

public sealed class TestNode : IAsyncDisposable
{
    public required Node Node { get; init; }
    public required DeviceIdentity Identity { get; init; }
    public required MemoryPeerStore Store { get; init; }
    public FakeDiscovery? Discovery { get; init; }
    public ConcurrentQueue<Message> Received { get; } = new();
    public ConcurrentQueue<PairingResult> Pairings { get; } = new();
    public bool AcceptPairing { get; set; } = true;
    public ValueTask DisposeAsync() => Node.DisposeAsync();
}

public class NodeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static NodeOptions FastOptions(bool encryption = true, int port = 0, TimeSpan? pairingCooldown = null) => new()
    {
        TcpPort = port,
        EncryptionEnabled = encryption,
        PingInterval = TimeSpan.FromMilliseconds(300),
        PeerTimeout = TimeSpan.FromSeconds(2),
        ReconnectInterval = TimeSpan.FromMilliseconds(200),
        SecondaryDialDelay = TimeSpan.FromSeconds(1),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        HandshakeTimeout = TimeSpan.FromSeconds(5),
        PairingTimeout = TimeSpan.FromSeconds(10),
        PairingCooldown = pairingCooldown ?? TimeSpan.FromMilliseconds(200),
    };

    internal static TestNode Make(FakeDiscoveryHub hub, string name, bool encryption = true,
        DeviceIdentity? identity = null, MemoryPeerStore? store = null, int port = 0, bool silent = false, TimeSpan? pairingCooldown = null)
    {
        identity ??= DeviceIdentity.Create(Guid.NewGuid(), name);
        store ??= new MemoryPeerStore();
        var discovery = hub.Join();
        discovery.Silent = silent;
        var node = new Node(identity, store, discovery, FastOptions(encryption, port, pairingCooldown));
        var t = new TestNode { Node = node, Identity = identity, Store = store, Discovery = discovery };
        node.PairingConfirmer = (_, _) => Task.FromResult(t.AcceptPairing);
        node.MessageReceived += (_, m) => t.Received.Enqueue(m);
        node.PairingCompleted += r => t.Pairings.Enqueue(r);
        node.Start();
        return t;
    }

    internal static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("기다리다 시간 초과: " + what);
            await Task.Delay(50);
        }
    }

    private static IPEndPoint Ep(TestNode n) => new(IPAddress.Loopback, n.Node.ListeningPort);

    internal static async Task PairAndConnect(TestNode a, TestNode b)
    {
        var result = await a.Node.PairAsync(Ep(b));
        Assert.Equal(PairingOutcome.Paired, result.Outcome);
        await Until(() => a.Node.IsConnected(b.Identity.Id) && b.Node.IsConnected(a.Identity.Id), "짝 맺은 뒤 연결");
    }

    [Fact]
    public async Task 짝을_맺으면_양쪽에_기록되고_암호화_연결로_메시지가_오간다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A");
        await using var b = Make(hub, "B");

        await PairAndConnect(a, b);

        var recB = Assert.Single(b.Store.GetAll());
        Assert.Equal(a.Identity.Id, recB.Id);
        Assert.Equal(a.Identity.Thumbprint, recB.Thumbprint);
        Assert.Equal(NeighborSide.Left, recB.Side);
        Assert.Equal(NeighborSide.Right, a.Store.Get(b.Identity.Id)!.Side);
        Assert.True(a.Node.IsEncrypted(b.Identity.Id));

        Assert.True(a.Node.Send(b.Identity.Id, new MouseMoveMessage(10, 20)));
        await Until(() => b.Received.Contains(new MouseMoveMessage(10, 20)), "B 가 메시지 받음");
        Assert.True(b.Node.Send(a.Identity.Id, new KeyMessage(0x41, 0x1E, false, true)));
        await Until(() => a.Received.Contains(new KeyMessage(0x41, 0x1E, false, true)), "A 가 메시지 받음");
    }

    [Fact]
    public async Task 양쪽_모두_암호화를_끄면_평문으로_붙는다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A", encryption: false);
        await using var b = Make(hub, "B", encryption: false);
        await PairAndConnect(a, b);
        Assert.False(a.Node.IsEncrypted(b.Identity.Id));
        Assert.True(a.Node.Send(b.Identity.Id, new MouseMoveMessage(1, 1)));
        await Until(() => b.Received.Contains(new MouseMoveMessage(1, 1)), "평문 메시지");
    }

    [Fact]
    public async Task 한쪽이라도_암호화를_켜면_암호화한다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A", encryption: false);
        await using var b = Make(hub, "B", encryption: true);
        await PairAndConnect(a, b);
        Assert.True(a.Node.IsEncrypted(b.Identity.Id));
        Assert.True(b.Node.IsEncrypted(a.Identity.Id));
    }

    [Fact]
    public async Task 상대가_거절하면_짝이_맺어지지_않는다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A");
        await using var b = Make(hub, "B");
        b.AcceptPairing = false;
        var result = await a.Node.PairAsync(Ep(b));
        Assert.Equal(PairingOutcome.RejectedByPeer, result.Outcome);
        Assert.Empty(a.Store.GetAll());
        Assert.Empty(b.Store.GetAll());
    }

    [Fact]
    public async Task 상대가_거절하면_내_확인_창이_닫힌다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A");
        await using var b = Make(hub, "B");
        var closed = new TaskCompletionSource();
        // A 는 사용자가 아직 고르지 않은 상태로 창을 띄워 둔다.
        a.Node.PairingConfirmer = async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { closed.TrySetResult(); }
            return false;
        };
        b.AcceptPairing = false;
        var result = await a.Node.PairAsync(Ep(b));
        Assert.Equal(PairingOutcome.RejectedByPeer, result.Outcome);
        await closed.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task 거절한_주소에서_곧바로_다시_요청하면_확인_창을_띄우지_않는다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A");
        await using var b = Make(hub, "B", pairingCooldown: TimeSpan.FromSeconds(30));
        int prompts = 0;
        b.Node.PairingConfirmer = (_, _) => { Interlocked.Increment(ref prompts); return Task.FromResult(false); };

        Assert.Equal(PairingOutcome.RejectedByPeer, (await a.Node.PairAsync(Ep(b))).Outcome);
        var again = await a.Node.PairAsync(Ep(b));
        Assert.NotEqual(PairingOutcome.Paired, again.Outcome);
        Assert.Equal(1, prompts);
        Assert.Empty(b.Store.GetAll());
    }

    [Fact]
    public async Task 짝_맺기_확인_창이_떠_있는_동안_온_다른_요청은_무시한다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A");
        await using var c = Make(hub, "C");
        await using var b = Make(hub, "B");
        int prompts = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.Node.PairingConfirmer = (_, ct) => { Interlocked.Increment(ref prompts); return release.Task.WaitAsync(ct); };

        var first = a.Node.PairAsync(Ep(b));
        await Until(() => Volatile.Read(ref prompts) == 1, "첫 요청의 확인 창");
        var second = await c.Node.PairAsync(Ep(b)); // B 는 아직 고르지 않았다
        Assert.NotEqual(PairingOutcome.Paired, second.Outcome);
        Assert.Equal(1, Volatile.Read(ref prompts));

        release.SetResult(true);
        Assert.Equal(PairingOutcome.Paired, (await first).Outcome);
        Assert.NotNull(b.Store.Get(a.Identity.Id));
        Assert.Null(b.Store.Get(c.Identity.Id));
    }

    [Fact]
    public async Task 양쪽_화면의_확인_코드가_같다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A");
        await using var b = Make(hub, "B");
        string? codeA = null, codeB = null;
        a.Node.PairingConfirmer = (p, _) => { codeA = p.Code; return Task.FromResult(true); };
        b.Node.PairingConfirmer = (p, _) => { codeB = p.Code; return Task.FromResult(true); };
        await a.Node.PairAsync(Ep(b));
        Assert.NotNull(codeA);
        Assert.Equal(codeA, codeB);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 같은_ID_라도_인증서가_다르면_거절한다(bool encryption)
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A", encryption);
        await using var b = Make(hub, "B", encryption);
        await PairAndConnect(a, b);
        await b.DisposeAsync();
        await Until(() => !a.Node.IsConnected(b.Identity.Id), "B 끊김");

        // B 의 ID 를 흉내 내지만 다른 키를 가진 가짜 PC
        var fakeIdentity = DeviceIdentity.Create(b.Identity.Id, "가짜 B");
        var fakeStore = new MemoryPeerStore(b.Store.GetAll());
        await using var fake = Make(hub, "가짜 B", encryption, fakeIdentity, fakeStore);
        await Task.Delay(2500);
        Assert.False(a.Node.IsConnected(b.Identity.Id));
        Assert.Empty(a.Received);
    }

    [Fact]
    public async Task 짝_맺지_않은_PC는_연결할_수_없다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A");
        var strangerId = DeviceIdentity.Create(Guid.NewGuid(), "낯선 PC");
        // 낯선 PC는 A 를 안다고 우기지만 A 는 낯선 PC를 모른다.
        var strangerStore = new MemoryPeerStore([new PeerRecord
        {
            Id = a.Identity.Id, Name = "A", Certificate = a.Identity.Certificate.RawData,
            Thumbprint = a.Identity.Thumbprint, LastAddress = "127.0.0.1", LastPort = a.Node.ListeningPort,
        }]);
        await using var stranger = Make(hub, "낯선 PC", identity: strangerId, store: strangerStore);
        await Task.Delay(2500);
        Assert.Empty(a.Node.ConnectedPeers);
        Assert.Empty(stranger.Node.ConnectedPeers);
    }

    [Fact]
    public async Task 상대가_꺼졌다_켜지면_포트가_바뀌어도_자동으로_다시_붙는다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A");
        var b = Make(hub, "B");
        await PairAndConnect(a, b);

        var disconnected = new TaskCompletionSource();
        a.Node.PeerDisconnected += id => { if (id == b.Identity.Id) disconnected.TrySetResult(); };
        var pfx = b.Identity.ExportPfx();
        var records = b.Store.GetAll();
        await b.DisposeAsync();
        await disconnected.Task.WaitAsync(Wait);

        // 같은 신원·기록으로 다시 켠다(새 포트).
        var reborn = DeviceIdentity.Import(b.Identity.Id, "B", pfx);
        await using var b2 = Make(hub, "B", identity: reborn, store: new MemoryPeerStore(records));
        await Until(() => a.Node.IsConnected(b2.Identity.Id) && b2.Node.IsConnected(a.Identity.Id), "다시 연결");
        Assert.True(b2.Node.Send(a.Identity.Id, new MouseMoveMessage(7, 7)));
        await Until(() => a.Received.Contains(new MouseMoveMessage(7, 7)), "재연결 뒤 메시지");
    }

    [Fact]
    public async Task 검색이_막혀도_마지막_주소로_다시_붙는다()
    {
        var hub = new FakeDiscoveryHub();
        int port = FreePort();
        await using var a = Make(hub, "A");
        var b = Make(hub, "B", port: port);
        await PairAndConnect(a, b);

        var pfx = b.Identity.ExportPfx();
        var records = b.Store.GetAll();
        await b.DisposeAsync();
        await Until(() => !a.Node.IsConnected(b.Identity.Id), "B 끊김");
        a.Discovery!.Silent = true;

        var reborn = DeviceIdentity.Import(b.Identity.Id, "B", pfx);
        await using var b2 = Make(hub, "B", identity: reborn, store: new MemoryPeerStore(records), port: port, silent: true);
        await Until(() => a.Node.IsConnected(b2.Identity.Id) && b2.Node.IsConnected(a.Identity.Id), "마지막 주소로 다시 연결");
    }

    [Fact]
    public async Task 연결이_끊기면_다시_붙는다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A");
        await using var b = Make(hub, "B");
        await PairAndConnect(a, b);
        int connects = 0;
        a.Node.PeerConnected += _ => Interlocked.Increment(ref connects);
        a.Node.Disconnect(b.Identity.Id);
        await Until(() => Volatile.Read(ref connects) >= 1 && a.Node.IsConnected(b.Identity.Id) && b.Node.IsConnected(a.Identity.Id), "끊긴 뒤 다시 연결");
        Assert.True(a.Node.Send(b.Identity.Id, new MouseMoveMessage(3, 3)));
        await Until(() => b.Received.Contains(new MouseMoveMessage(3, 3)), "다시 연결 뒤 메시지");
    }

    [Fact]
    public async Task 짝을_풀면_다시_붙지_않는다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = Make(hub, "A");
        await using var b = Make(hub, "B");
        await PairAndConnect(a, b);
        a.Node.Unpair(b.Identity.Id);
        await Until(() => !b.Node.IsConnected(a.Identity.Id), "B 쪽도 끊김");
        await Task.Delay(2000);
        Assert.False(a.Node.IsConnected(b.Identity.Id));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
