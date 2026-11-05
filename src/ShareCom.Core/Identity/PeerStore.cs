using ShareCom.Core.Protocol;

namespace ShareCom.Core.Identity;

/// <summary>짝 맺은 PC 하나의 기록.</summary>
public sealed record PeerRecord
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    /// <summary>상대 인증서 원본(DER). 평문 모드 서명 확인에 쓴다.</summary>
    public required byte[] Certificate { get; init; }
    /// <summary>상대 인증서 SHA-256 지문. 암호화 모드에서 이 값과 맞춰 본다.</summary>
    public required string Thumbprint { get; init; }
    /// <summary>마지막으로 연결된 주소. 자동 검색이 막힌 망에서 다시 붙을 때 쓴다.</summary>
    public string? LastAddress { get; init; }
    public int LastPort { get; init; }
    /// <summary>처음 짝 맺을 때 정한 방향. 배치에 아직 내 자리가 없을 때 이 PC 옆에 나를 놓는 데 쓴다.</summary>
    public NeighborSide Side { get; init; } = NeighborSide.Right;
    /// <summary>이 PC를 믿기 시작한 시각(UTC ticks, 직접 짝 맺기 또는 자동 소개). 짝 풀기와 어느 쪽이 나중인지 가린다.</summary>
    public long TrustedAtUtcTicks { get; init; }
}

public interface IPeerStore
{
    IReadOnlyList<PeerRecord> GetAll();
    PeerRecord? Get(Guid id);
    void Upsert(PeerRecord record);
    void Remove(Guid id);
    /// <summary>짝을 푼 PC와 그 시각(UTC ticks). 자동 소개로 다시 들어오지 않게 기억한다.</summary>
    IReadOnlyDictionary<Guid, long> Revoked { get; }
    void SetRevoked(Guid id, long atUtcTicks);
    void ClearRevoked(Guid id);
    event Action? Changed;
}

/// <summary>메모리에만 두는 기록. 시험과 저장 계층의 바탕으로 쓴다.</summary>
public class MemoryPeerStore : IPeerStore
{
    private readonly Dictionary<Guid, PeerRecord> _peers = new();
    private readonly Dictionary<Guid, long> _revoked = new();
    private readonly object _gate = new();

    public event Action? Changed;

    public MemoryPeerStore(IEnumerable<PeerRecord>? initial = null, IReadOnlyDictionary<Guid, long>? revoked = null)
    {
        foreach (var p in initial ?? []) _peers[p.Id] = p;
        foreach (var (id, at) in revoked ?? new Dictionary<Guid, long>()) _revoked[id] = at;
    }

    public IReadOnlyDictionary<Guid, long> Revoked { get { lock (_gate) return new Dictionary<Guid, long>(_revoked); } }

    public void SetRevoked(Guid id, long atUtcTicks)
    {
        lock (_gate)
        {
            if (_revoked.TryGetValue(id, out var old) && old == atUtcTicks) return;
            _revoked[id] = atUtcTicks;
        }
        OnChanged();
    }

    public void ClearRevoked(Guid id)
    {
        bool removed;
        lock (_gate) removed = _revoked.Remove(id);
        if (removed) OnChanged();
    }

    public IReadOnlyList<PeerRecord> GetAll() { lock (_gate) return _peers.Values.ToList(); }

    public PeerRecord? Get(Guid id) { lock (_gate) return _peers.GetValueOrDefault(id); }

    public void Upsert(PeerRecord record)
    {
        lock (_gate)
        {
            if (_peers.TryGetValue(record.Id, out var old) && RecordEquals(old, record)) return;
            _peers[record.Id] = record;
        }
        OnChanged();
    }

    public void Remove(Guid id)
    {
        bool removed;
        lock (_gate) removed = _peers.Remove(id);
        if (removed) OnChanged();
    }

    protected virtual void OnChanged() => Changed?.Invoke();

    private static bool RecordEquals(PeerRecord a, PeerRecord b) =>
        a.Name == b.Name && a.Thumbprint == b.Thumbprint && a.LastAddress == b.LastAddress &&
        a.LastPort == b.LastPort && a.Side == b.Side && a.TrustedAtUtcTicks == b.TrustedAtUtcTicks &&
        a.Certificate.AsSpan().SequenceEqual(b.Certificate);
}
