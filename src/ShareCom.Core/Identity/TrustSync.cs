using ShareCom.Core.Net;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Identity;

/// <summary>
/// 자동 소개. 짝인 PC끼리 "내가 믿는 PC들"과 "짝을 푼 PC들"을 주고받아,
/// 새 PC가 아무 PC 하나와만 짝을 맺어도 나머지 PC들과 서로 짝이 되게 한다.
/// 소개는 이미 인증된 연결로만 오므로, 내가 믿는 PC가 믿는 PC를 나도 믿는 셈이다.
/// 같은 PC에 대해 소개와 짝 풀기가 엇갈리면 시각이 늦은 쪽이 이긴다(짝을 푼 뒤 다시 직접 짝 맺으면 다시 믿는다).
/// </summary>
public sealed class TrustSync
{
    private readonly Node _node;
    private readonly IPeerStore _store;
    private readonly Func<long> _clock;
    private readonly Guid _self;
    private readonly object _gate = new();

    public TrustSync(Node node, IPeerStore store, Func<long>? clock = null)
    {
        _node = node;
        _store = store;
        _clock = clock ?? (() => DateTime.UtcNow.Ticks);
        _self = node.Identity.Id;
        node.PeerConnected += OnConnected;
        node.MessageReceived += OnMessage;
        node.PairingCompleted += OnPaired;
    }

    /// <summary>진단용 기록.</summary>
    public event Action<string>? Log;

    /// <summary>짝을 풀고, 연결된 모든 PC에도 짝 풀기를 알린다.</summary>
    public void Unpair(Guid peer)
    {
        var now = _clock();
        lock (_gate)
        {
            _store.SetRevoked(peer, now);
            _node.Unpair(peer);
        }
        Broadcast([Revoke(peer, now)], except: null);
    }

    private void OnConnected(Guid peer)
    {
        var entries = new List<TrustEntry>();
        lock (_gate)
        {
            // 짝 풀기를 먼저 보내 상대가 옛 소개로 되살리지 않게 한다.
            foreach (var (id, at) in _store.Revoked) if (id != peer) entries.Add(Revoke(id, at));
            foreach (var r in _store.GetAll()) if (r.Id != peer) entries.Add(Trust(r));
        }
        if (entries.Count > 0) _node.Send(peer, new TrustMessage(entries.ToArray()));
    }

    private void OnPaired(PairingResult result)
    {
        if (result.Outcome != PairingOutcome.Paired || result.PeerId is not { } id) return;
        var record = _store.Get(id);
        if (record is null) return;
        Broadcast([Trust(record)], except: id);
    }

    private void OnMessage(Guid from, Message message)
    {
        if (message is not TrustMessage m) return;
        var changed = new List<TrustEntry>();
        lock (_gate)
        {
            if (_store.Get(from) is null) return; // 이미 짝이 풀린 PC가 보낸 것
            foreach (var e in m.Entries)
                if (Apply(from, e)) changed.Add(e);
        }
        if (changed.Count > 0) Broadcast(changed, except: from);
    }

    /// <summary>한 줄을 반영한다. 내 상태가 바뀌었으면(다른 PC에도 알릴 것이면) true.</summary>
    private bool Apply(Guid from, TrustEntry e)
    {
        if (e.Id == _self || e.Id == from || e.Id == Guid.Empty) return false;
        var existing = _store.Get(e.Id);
        var revoked = _store.Revoked.TryGetValue(e.Id, out var revokedAt) ? revokedAt : (long?)null;

        if (e.Trusted)
        {
            if (e.Certificate.Length == 0) return false;
            if (revoked is { } r && r >= e.AtUtcTicks) return false;
            string thumbprint;
            try { thumbprint = CertificateUtil.Thumbprint(e.Certificate); }
            catch { return false; }

            if (existing is not null)
            {
                if (existing.TrustedAtUtcTicks >= e.AtUtcTicks) return false;
                _store.Upsert(existing with
                {
                    Certificate = e.Certificate,
                    Thumbprint = thumbprint,
                    TrustedAtUtcTicks = e.AtUtcTicks,
                });
                return true;
            }
            _store.Upsert(new PeerRecord
            {
                Id = e.Id,
                Name = e.Name,
                Certificate = e.Certificate,
                Thumbprint = thumbprint,
                LastAddress = e.Address,
                LastPort = e.Port,
                TrustedAtUtcTicks = e.AtUtcTicks,
            });
            _store.ClearRevoked(e.Id);
            Log?.Invoke($"자동 소개로 짝이 되었습니다: {e.Name}");
            return true;
        }

        // 짝 풀기
        if (existing is not null)
        {
            if (existing.TrustedAtUtcTicks >= e.AtUtcTicks) return false;
            _store.SetRevoked(e.Id, e.AtUtcTicks);
            _node.Unpair(e.Id);
            Log?.Invoke($"다른 PC에서 짝을 풀어 이 PC에서도 풀었습니다: {existing.Name}");
            return true;
        }
        if (revoked is { } old && old >= e.AtUtcTicks) return false;
        _store.SetRevoked(e.Id, e.AtUtcTicks);
        return true;
    }

    private void Broadcast(IReadOnlyCollection<TrustEntry> entries, Guid? except)
    {
        var message = new TrustMessage(entries.ToArray());
        foreach (var peer in _node.ConnectedPeers)
            if (peer != except) _node.Send(peer, message);
    }

    private static TrustEntry Trust(PeerRecord r) =>
        new(r.Id, r.Name, r.Certificate, r.LastAddress, r.LastPort, r.TrustedAtUtcTicks, Trusted: true);

    private static TrustEntry Revoke(Guid id, long at) => new(id, "", [], null, 0, at, Trusted: false);
}
