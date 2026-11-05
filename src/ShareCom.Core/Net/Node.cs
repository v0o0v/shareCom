using System.Net;
using System.Net.Sockets;
using ShareCom.Core.Identity;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Net;

public sealed class NodeOptions
{
    /// <summary>0 이면 빈 포트를 아무거나 쓴다(시험용).</summary>
    public int TcpPort { get; init; } = 47821;
    public bool EncryptionEnabled { get; set; } = true;
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>ID 가 큰 쪽은 이만큼 연결이 없을 때만 먼저 건다(양쪽이 동시에 거는 일을 줄인다).</summary>
    public TimeSpan SecondaryDialDelay { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>들어온 짝 맺기 요청이 거절·실패로 끝나면 그 주소의 요청을 이만큼 무시한다.</summary>
    public TimeSpan PairingCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DiscoveryExpiry { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed record PairingPrompt(Guid PeerId, string PeerName, string Code, bool IsInitiator);

public enum PairingOutcome { Paired, RejectedByMe, RejectedByPeer, Failed }

public sealed record PairingResult(PairingOutcome Outcome, Guid? PeerId, string? PeerName, string? Error = null);

/// <summary>연결된 PC들에게 메시지를 보내는 일만 필요한 계층(배치 동기화 등)이 쓴다.</summary>
public interface IPeerMessenger
{
    IReadOnlyList<Guid> ConnectedPeers { get; }
    bool Send(Guid peer, Message message);
}

/// <summary>
/// 이 PC의 통신 담당. 연결을 받고, 짝 맺은 PC에 계속 다시 연결을 시도하고,
/// 짝 맺기를 처리한다. 위 계층은 PC를 ID 로만 다룬다.
/// </summary>
public sealed class Node : IAsyncDisposable, IPeerMessenger
{
    private readonly DeviceIdentity _me;
    private readonly IPeerStore _store;
    private readonly IDiscovery _discovery;
    private readonly NodeOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PeerConnection> _connections = new();
    private readonly HashSet<Guid> _dialing = new();
    private readonly Dictionary<Guid, DateTime> _lastConnectedUtc = new();
    private readonly Dictionary<Guid, DiscoveredPeer> _discovered = new();
    // 들어오는 짝 맺기 요청 제한: 한 번에 하나, 거절·실패한 주소는 잠시 무시
    private bool _incomingPairing;
    private readonly Dictionary<IPAddress, DateTime> _pairingBlockedUntil = new();
    private readonly SemaphoreSlim _reconnectSignal = new(0);
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private TcpListener? _listener;

    public Node(DeviceIdentity me, IPeerStore store, IDiscovery discovery, NodeOptions? options = null)
    {
        _me = me;
        _store = store;
        _discovery = discovery;
        _options = options ?? new NodeOptions();
    }

    public DeviceIdentity Identity => _me;
    public int ListeningPort { get; private set; }

    public bool EncryptionEnabled
    {
        get => _options.EncryptionEnabled;
        set => _options.EncryptionEnabled = value;
    }

    /// <summary>짝 맺기 확인 창을 띄우고 사용자가 [짝 맺기]를 눌렀는지 돌려준다. 토큰이 취소되면 창을 닫는다.</summary>
    public Func<PairingPrompt, CancellationToken, Task<bool>>? PairingConfirmer { get; set; }

    public event Action<Guid>? PeerConnected;
    public event Action<Guid>? PeerDisconnected;
    public event Action<Guid, Message>? MessageReceived;
    /// <summary>큰 데이터가 다 모였다(보낸 PC, 종류, 내용).</summary>
    public event Action<Guid, BlobKind, byte[]>? BlobReceived;
    public event Action? DiscoveredChanged;
    public event Action<PairingResult>? PairingCompleted;
    /// <summary>진단용 기록.</summary>
    public event Action<string>? Log;

    public void Start()
    {
        var listener = new TcpListener(IPAddress.Any, _options.TcpPort);
        listener.Start();
        _listener = listener;
        ListeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
        _ = Task.Run(ReconnectLoop);
        _discovery.PeerSeen += OnPeerSeen;
        _discovery.Start(new DiscoveryAnnouncement(_me.Id, _me.Name, ListeningPort));
    }

    public IReadOnlyList<Guid> ConnectedPeers { get { lock (_gate) return _connections.Keys.ToList(); } }

    public bool IsConnected(Guid peer) { lock (_gate) return _connections.ContainsKey(peer); }

    public bool IsEncrypted(Guid peer) { lock (_gate) return _connections.TryGetValue(peer, out var c) && c.Encrypted; }

    public bool Send(Guid peer, Message message)
    {
        PeerConnection? c;
        lock (_gate) _connections.TryGetValue(peer, out c);
        return c?.Send(message) ?? false;
    }

    /// <summary>늦게 가도 되는 메시지(파일 조각 등)를 대량 줄로 보낸다.</summary>
    public bool SendBulk(Guid peer, Message message)
    {
        PeerConnection? c;
        lock (_gate) _connections.TryGetValue(peer, out c);
        return c?.SendBulk(message) ?? false;
    }

    /// <summary>
    /// 큰 데이터를 64KB 조각으로 나눠 대량 줄로 보낸다. 마우스·키보드 메시지가 늘 먼저 간다.
    /// 받는 쪽에서는 다 모이면 <see cref="BlobReceived"/> 가 한 번 일어난다.
    /// </summary>
    public bool SendBlob(Guid peer, BlobKind kind, byte[] data)
    {
        PeerConnection? c;
        lock (_gate) _connections.TryGetValue(peer, out c);
        if (c is null) return false;
        var id = unchecked((uint)Interlocked.Increment(ref _nextBlobId));
        foreach (var chunk in BlobAssembler.Split(id, kind, data))
            if (!c.SendBulk(chunk)) return false;
        return true;
    }

    private int _nextBlobId;

    /// <summary>최근에 검색된 PC들(짝 맺은 PC 포함).</summary>
    public IReadOnlyList<DiscoveredPeer> Discovered
    {
        get
        {
            var cutoff = DateTime.UtcNow - _options.DiscoveryExpiry;
            lock (_gate) return _discovered.Values.Where(d => d.LastSeenUtc >= cutoff).OrderBy(d => d.Name).ToList();
        }
    }

    /// <summary>짝을 풀고 연결을 끊는다.</summary>
    public void Unpair(Guid peer)
    {
        _store.Remove(peer);
        PeerConnection? c;
        lock (_gate) _connections.TryGetValue(peer, out c);
        c?.Close();
    }

    /// <summary>지금 연결을 끊는다(다시 연결은 계속 시도한다).</summary>
    public void Disconnect(Guid peer)
    {
        PeerConnection? c;
        lock (_gate) _connections.TryGetValue(peer, out c);
        c?.Close();
    }

    // ───────────── 받기 ─────────────

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleIncoming(tcp));
        }
    }

    private async Task HandleIncoming(TcpClient tcp)
    {
        tcp.NoDelay = true;
        HandshakeResult h;
        try
        {
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            hsCts.CancelAfter(_options.HandshakeTimeout);
            h = await Handshake.ServerAsync(tcp.GetStream(), _me, ListeningPort, _options.EncryptionEnabled, _store.Get, hsCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"들어온 연결 거절: {tcp.Client.RemoteEndPoint} - {ex.Message}");
            tcp.Dispose();
            return;
        }

        if (h.Mode == ConnectionMode.Pairing)
        {
            var from = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            if (!TryBeginIncomingPairing(from, out var why))
            {
                Log?.Invoke($"짝 맺기 요청 무시: {from} ({h.PeerName}) - {why}");
                h.Stream.Dispose();
                tcp.Dispose();
                return;
            }
            PairingResult result;
            try
            {
                result = await RunPairingAsync(tcp, h, initiator: false).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _incomingPairing = false;
            }
            if (result.Outcome != PairingOutcome.Paired)
                lock (_gate) _pairingBlockedUntil[from] = DateTime.UtcNow + _options.PairingCooldown;
            PairingCompleted?.Invoke(result);
            return;
        }
        Register(tcp, h, initiatedByMe: false);
    }

    /// <summary>
    /// 같은 망의 누구나 짝 맺기 확인 창을 띄울 수 있으므로, 창이 쌓이거나 계속 뜨지 않게 막는다.
    /// 한 번에 하나만 받고, 거절·실패·시간 초과로 끝난 주소는 <see cref="NodeOptions.PairingCooldown"/> 동안 무시한다.
    /// </summary>
    private bool TryBeginIncomingPairing(IPAddress from, out string why)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var old in _pairingBlockedUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                _pairingBlockedUntil.Remove(old);
            if (_incomingPairing) { why = "다른 짝 맺기 요청을 처리하는 중"; return false; }
            if (_pairingBlockedUntil.TryGetValue(from, out var until))
            {
                why = $"방금 거절·실패한 주소라 {(int)Math.Ceiling((until - now).TotalSeconds)}초 동안 받지 않음";
                return false;
            }
            _incomingPairing = true;
            why = "";
            return true;
        }
    }

    // ───────────── 걸기 ─────────────

    private async Task ReconnectLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { DialMissingPeers(); }
            catch (Exception ex) { Log?.Invoke($"재연결 루프 오류: {ex.Message}"); }
            try { await _reconnectSignal.WaitAsync(_options.ReconnectInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void DialMissingPeers()
    {
        var now = DateTime.UtcNow;
        foreach (var peer in _store.GetAll())
        {
            IPEndPoint? endpoint;
            lock (_gate)
            {
                if (_connections.ContainsKey(peer.Id) || _dialing.Contains(peer.Id)) continue;
                bool primary = _me.Id.CompareTo(peer.Id) < 0;
                if (!primary)
                {
                    var since = _lastConnectedUtc.TryGetValue(peer.Id, out var t) ? t : _startedUtc;
                    if (now - since < _options.SecondaryDialDelay) continue;
                }
                endpoint = PickEndpoint(peer, now);
                if (endpoint is null) continue;
                _dialing.Add(peer.Id);
            }
            _ = Task.Run(() => DialAsync(peer.Id, endpoint));
        }
    }

    private IPEndPoint? PickEndpoint(PeerRecord peer, DateTime now)
    {
        if (_discovered.TryGetValue(peer.Id, out var d) && now - d.LastSeenUtc < _options.DiscoveryExpiry)
            return d.Endpoint;
        if (peer.LastAddress is { } a && IPAddress.TryParse(a, out var ip) && peer.LastPort > 0)
            return new IPEndPoint(ip, peer.LastPort);
        return null;
    }

    private async Task DialAsync(Guid peerId, IPEndPoint endpoint)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            hsCts.CancelAfter(_options.ConnectTimeout);
            await tcp.ConnectAsync(endpoint, hsCts.Token).ConfigureAwait(false);
            hsCts.CancelAfter(_options.HandshakeTimeout);
            var mode = _options.EncryptionEnabled ? ConnectionMode.Tls : ConnectionMode.Plain;
            var h = await Handshake.ClientAsync(tcp.GetStream(), _me, ListeningPort, mode, _store.Get, peerId, hsCts.Token).ConfigureAwait(false);
            Register(tcp, h, initiatedByMe: true);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"연결 실패: {endpoint} - {ex.Message}");
            tcp.Dispose();
        }
        finally
        {
            lock (_gate) _dialing.Remove(peerId);
        }
    }

    // ───────────── 연결 등록 ─────────────

    private void Register(TcpClient tcp, HandshakeResult h, bool initiatedByMe)
    {
        var conn = new PeerConnection(tcp, h, initiatedByMe, _me.Id, _options.PingInterval, _options.PeerTimeout);
        PeerConnection? replaced = null;
        lock (_gate)
        {
            if (_connections.TryGetValue(h.PeerId, out var existing) && !existing.IsClosed)
            {
                if (!Prefer(conn, existing))
                {
                    conn.Close();
                    return;
                }
                replaced = existing;
            }
            _connections[h.PeerId] = conn;
            _lastConnectedUtc[h.PeerId] = DateTime.UtcNow;
        }

        conn.MessageReceived += (c, m) =>
        {
            if (m is not BlobChunkMessage chunk)
            {
                MessageReceived?.Invoke(c.PeerId, m);
                return;
            }
            (BlobKind Kind, byte[] Data)? done;
            try { done = c.Assembler.Add(chunk); }
            catch (Exception ex)
            {
                Log?.Invoke($"잘못된 데이터 조각을 받아 연결을 끊습니다: {ex.Message}");
                c.Close();
                return;
            }
            if (done is { } d) BlobReceived?.Invoke(c.PeerId, d.Kind, d.Data);
        };
        conn.Closed += OnConnectionClosed;
        conn.Start();
        replaced?.Close(); // 이미 목록에서 빠졌으므로 끊김 알림은 나가지 않는다

        RememberAddress(h, conn);
        Log?.Invoke($"연결됨: {h.PeerName} ({conn.RemoteEndPoint}, {(conn.Encrypted ? "암호화" : "평문")})");
        PeerConnected?.Invoke(h.PeerId);
    }

    /// <summary>
    /// 같은 PC와 연결이 둘 생겼을 때 새 연결을 남길지. 같은 쪽이 다시 건 연결이면 새것이(옛것은 죽었을 가능성이 크다),
    /// 양쪽이 동시에 건 경우면 ID 가 작은 쪽이 건 연결이 남는다. 양쪽 PC가 같은 규칙으로 같은 연결을 고른다.
    /// </summary>
    private static bool Prefer(PeerConnection candidate, PeerConnection existing) =>
        candidate.InitiatorId == existing.InitiatorId || candidate.InitiatorId.CompareTo(existing.InitiatorId) < 0;

    private void RememberAddress(HandshakeResult h, PeerConnection conn)
    {
        var record = _store.Get(h.PeerId);
        if (record is null || conn.RemoteEndPoint is null) return;
        var address = conn.RemoteEndPoint.Address.IsIPv4MappedToIPv6
            ? conn.RemoteEndPoint.Address.MapToIPv4().ToString()
            : conn.RemoteEndPoint.Address.ToString();
        _store.Upsert(record with
        {
            Name = h.PeerName,
            LastAddress = address,
            LastPort = h.PeerListenPort > 0 ? h.PeerListenPort : record.LastPort,
        });
    }

    private void OnConnectionClosed(PeerConnection conn)
    {
        bool removed;
        lock (_gate)
        {
            removed = _connections.TryGetValue(conn.PeerId, out var current) && ReferenceEquals(current, conn);
            if (removed)
            {
                _connections.Remove(conn.PeerId);
                _lastConnectedUtc[conn.PeerId] = DateTime.UtcNow;
            }
        }
        if (!removed) return;
        Log?.Invoke($"끊김: {conn.PeerName}");
        PeerDisconnected?.Invoke(conn.PeerId);
        if (!_cts.IsCancellationRequested) _reconnectSignal.Release();
    }

    // ───────────── 검색 ─────────────

    private void OnPeerSeen(DiscoveredPeer peer)
    {
        bool isNew, wake;
        lock (_gate)
        {
            isNew = !_discovered.TryGetValue(peer.Id, out var old) || old.Name != peer.Name || !old.Endpoint.Equals(peer.Endpoint)
                    || DateTime.UtcNow - old.LastSeenUtc > _options.DiscoveryExpiry;
            _discovered[peer.Id] = peer;
            wake = !_connections.ContainsKey(peer.Id) && !_dialing.Contains(peer.Id);
        }
        if (isNew) DiscoveredChanged?.Invoke();
        if (wake && _store.Get(peer.Id) is not null) _reconnectSignal.Release();
    }

    // ───────────── 짝 맺기 ─────────────

    /// <summary>상대 PC에 짝 맺기를 요청한다. 두 화면에 확인 코드가 뜨고 양쪽이 모두 수락하면 짝이 맺어진다.</summary>
    public async Task<PairingResult> PairAsync(IPEndPoint endpoint, CancellationToken ct = default)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        HandshakeResult h;
        try
        {
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
            hsCts.CancelAfter(_options.ConnectTimeout);
            await tcp.ConnectAsync(endpoint, hsCts.Token).ConfigureAwait(false);
            hsCts.CancelAfter(_options.HandshakeTimeout);
            h = await Handshake.ClientAsync(tcp.GetStream(), _me, ListeningPort, ConnectionMode.Pairing, _store.Get, null, hsCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            var failed = new PairingResult(PairingOutcome.Failed, null, null, ex is OperationCanceledException ? "연결 시간이 지났습니다." : ex.Message);
            PairingCompleted?.Invoke(failed);
            return failed;
        }
        var result = await RunPairingAsync(tcp, h, initiator: true, ct).ConfigureAwait(false);
        PairingCompleted?.Invoke(result);
        return result;
    }

    private async Task<PairingResult> RunPairingAsync(TcpClient tcp, HandshakeResult h, bool initiator, CancellationToken outer = default)
    {
        using var _tcp = tcp;
        using var _stream = h.Stream;
        var confirmer = PairingConfirmer;
        var code = CertificateUtil.PairingCode(_me.Thumbprint, CertificateUtil.Thumbprint(h.PeerCertificate));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, outer);
        cts.CancelAfter(_options.PairingTimeout);

        try
        {
            var remoteTask = Handshake.ReadAsync<PairDecisionMessage>(h.Stream, cts.Token);
            var localTask = confirmer is null
                ? Task.FromResult(false)
                : confirmer(new PairingPrompt(h.PeerId, h.PeerName, code, initiator), cts.Token);

            bool local, remote;
            var first = await Task.WhenAny(remoteTask, localTask).ConfigureAwait(false);
            if (first == remoteTask && !await SafeResult(remoteTask))
            {
                // 상대가 먼저 거절했으면 내 확인 창을 닫는다.
                await cts.CancelAsync().ConfigureAwait(false);
                try { await localTask.ConfigureAwait(false); } catch { }
                return new PairingResult(PairingOutcome.RejectedByPeer, h.PeerId, h.PeerName);
            }

            local = await localTask.ConfigureAwait(false);
            await MessageCodec.WriteAsync(h.Stream, new PairDecisionMessage(local), cts.Token).ConfigureAwait(false);
            if (!local) return new PairingResult(PairingOutcome.RejectedByMe, h.PeerId, h.PeerName);

            remote = (await remoteTask.ConfigureAwait(false)).Accepted;
            if (!remote) return new PairingResult(PairingOutcome.RejectedByPeer, h.PeerId, h.PeerName);

            var address = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (address is { IsIPv4MappedToIPv6: true }) address = address.MapToIPv4();
            var old = _store.Get(h.PeerId);
            _store.Upsert(new PeerRecord
            {
                Id = h.PeerId,
                Name = h.PeerName,
                Certificate = h.PeerCertificate,
                Thumbprint = CertificateUtil.Thumbprint(h.PeerCertificate),
                LastAddress = address?.ToString(),
                LastPort = h.PeerListenPort,
                // 처음 짝 맺을 때: 요청한 쪽은 상대를 오른쪽에, 받은 쪽은 상대를 왼쪽에 둔다.
                Side = old?.Side ?? (initiator ? NeighborSide.Right : NeighborSide.Left),
                TrustedAtUtcTicks = DateTime.UtcNow.Ticks,
            });
            _store.ClearRevoked(h.PeerId);
            // 오래된 연결이 있으면 끊고 새 인증서로 다시 붙게 한다.
            Disconnect(h.PeerId);
            _reconnectSignal.Release();
            return new PairingResult(PairingOutcome.Paired, h.PeerId, h.PeerName);
        }
        catch (OperationCanceledException)
        {
            return new PairingResult(PairingOutcome.Failed, h.PeerId, h.PeerName, "시간이 지나 짝 맺기를 취소했습니다.");
        }
        catch (Exception ex)
        {
            return new PairingResult(PairingOutcome.Failed, h.PeerId, h.PeerName, ex.Message);
        }

        static async Task<bool> SafeResult(Task<PairDecisionMessage> t)
        {
            try { return (await t.ConfigureAwait(false)).Accepted; }
            catch { return false; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _discovery.PeerSeen -= OnPeerSeen;
        _discovery.Dispose();
        try { _listener?.Stop(); } catch { }
        List<PeerConnection> all;
        lock (_gate) { all = _connections.Values.ToList(); _connections.Clear(); }
        foreach (var c in all) c.Close();
    }
}
