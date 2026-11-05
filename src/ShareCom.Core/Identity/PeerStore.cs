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
    /// <summary>내 기준으로 상대 PC가 놓인 방향.</summary>
    public NeighborSide Side { get; init; } = NeighborSide.Right;
}

public interface IPeerStore
{
    IReadOnlyList<PeerRecord> GetAll();
    PeerRecord? Get(Guid id);
    void Upsert(PeerRecord record);
    void Remove(Guid id);
    event Action? Changed;
}

/// <summary>메모리에만 두는 기록. 시험과 저장 계층의 바탕으로 쓴다.</summary>
public class MemoryPeerStore : IPeerStore
{
    private readonly Dictionary<Guid, PeerRecord> _peers = new();
    private readonly object _gate = new();

    public event Action? Changed;

    public MemoryPeerStore(IEnumerable<PeerRecord>? initial = null)
    {
        if (initial is null) return;
        foreach (var p in initial) _peers[p.Id] = p;
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
        a.LastPort == b.LastPort && a.Side == b.Side;
}
