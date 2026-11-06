namespace ShareCom.Core.Diagnostics;

/// <summary>묶여서 쓰지 않은 기록의 요약. 창이 끝났을 때 한 줄로 남긴다.</summary>
public readonly record struct RepeatSummary(string Level, string Message, int Count, TimeSpan Window);

/// <summary>
/// 같은 기록이 되풀이될 때 처음 한 번만 쓰게 한다.
/// 수준과 문구가 글자 그대로 같으면 같은 기록이다. 처음 쓴 뒤 <c>window</c> 동안 같은 기록은 세기만 하고,
/// 창이 끝나면 <see cref="TakeDue"/> 가 센 횟수를 요약으로 돌려준다. 창이 끝난 뒤 다시 나오면 다시 처음부터다.
/// 스레드 안전하지 않다(기록 쓰는 스레드 하나에서만 쓴다).
/// </summary>
public sealed class RepeatSuppressor
{
    private sealed class Entry
    {
        public DateTime WindowEnd;
        public int Suppressed;
    }

    private readonly TimeSpan _window;
    private readonly int _maxKeys;
    private readonly Dictionary<(string Level, string Message), Entry> _entries = new();
    private readonly List<RepeatSummary> _pending = new();
    private DateTime? _nextDue;

    public RepeatSuppressor(TimeSpan window, int maxKeys = 256)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
        _maxKeys = maxKeys;
    }

    /// <summary>요약을 내야 하는 가장 이른 시각. 묶은 기록이 없으면 null.</summary>
    public DateTime? NextDue => _pending.Count > 0 ? DateTime.MinValue : _nextDue;

    /// <summary>이 기록을 지금 써야 하면 true. false 면 세기만 했다.</summary>
    /// <remarks>창이 끝난 기록의 요약을 먼저 받으려면 이 앞에 <see cref="TakeDue"/> 를 부른다.</remarks>
    public bool ShouldWrite(string level, string message, DateTime now)
    {
        var key = (level, message);
        if (_entries.TryGetValue(key, out var e) && now < e.WindowEnd)
        {
            if (e.Suppressed++ == 0 && (_nextDue is null || e.WindowEnd < _nextDue)) _nextDue = e.WindowEnd;
            return false;
        }
        if (e is null && _entries.Count >= _maxKeys)
        {
            DropQuietExpired(now);
            if (_entries.Count >= _maxKeys) return true; // 너무 많은 종류: 묶지 않고 그냥 쓴다
        }
        // 창이 끝났는데 아직 꺼내지 않은 요약은 잃지 않게 따로 두었다가 다음 TakeDue 에서 내보낸다.
        if (e is { Suppressed: > 0 }) _pending.Add(new RepeatSummary(level, message, e.Suppressed, _window));
        _entries[key] = new Entry { WindowEnd = now + _window };
        if (e is { Suppressed: > 0 }) RecomputeNextDue();
        return true;
    }

    /// <summary>창이 끝난 기록 중 묶은 것이 있는 기록의 요약을 꺼낸다.</summary>
    public List<RepeatSummary> TakeDue(DateTime now)
    {
        var due = new List<RepeatSummary>(_pending);
        _pending.Clear();
        if (_nextDue is null || now < _nextDue) return due;
        foreach (var (key, e) in _entries.Where(kv => kv.Value.WindowEnd <= now).ToList())
        {
            if (e.Suppressed > 0) due.Add(new RepeatSummary(key.Level, key.Message, e.Suppressed, _window));
            _entries.Remove(key);
        }
        RecomputeNextDue();
        return due;
    }

    /// <summary>창이 끝나지 않았어도 묶은 기록의 요약을 모두 꺼내고 처음 상태로 돌아간다(프로그램을 끝낼 때).</summary>
    public List<RepeatSummary> TakeAll()
    {
        var all = new List<RepeatSummary>(_pending);
        all.AddRange(_entries.Where(kv => kv.Value.Suppressed > 0)
            .Select(kv => new RepeatSummary(kv.Key.Level, kv.Key.Message, kv.Value.Suppressed, _window)));
        _pending.Clear();
        _entries.Clear();
        _nextDue = null;
        return all;
    }

    private void DropQuietExpired(DateTime now)
    {
        foreach (var key in _entries.Where(kv => kv.Value.Suppressed == 0 && kv.Value.WindowEnd <= now).Select(kv => kv.Key).ToList())
            _entries.Remove(key);
    }

    private void RecomputeNextDue()
    {
        _nextDue = null;
        foreach (var e in _entries.Values)
            if (e.Suppressed > 0 && (_nextDue is null || e.WindowEnd < _nextDue)) _nextDue = e.WindowEnd;
    }
}
