namespace ShareCom.Core.Input;

/// <summary>
/// 원격 마우스 이동이 얼마나 고르게 오가는지 잰다. 도착(또는 보낸) 시각만 받아 쌓고, 요약 한 줄을 돌려준다.
/// 목록을 들고 있지 않고 그때그때 셈만 해서 훅·수신 스레드를 붙잡지 않는다.
/// </summary>
/// <remarks>
/// - 쉼: <see cref="PauseMs"/> 이상 벌어지면 사용자가 마우스를 멈춘 것으로 보고 간격 통계에서 뺀다.
/// - 공백: <see cref="StallMs"/> 이상 벌어졌지만 쉼은 아닌 간격. 포인터가 멈칫한 것으로 보인다.
/// - 몰림: <see cref="BurstGapMs"/> 보다 짧은 간격으로 <see cref="BurstMinCount"/> 개 이상 연달아 온 묶음.
///   마우스는 아무리 빨라도 이렇게 촘촘히 보고하지 않으므로, 네트워크나 스레드가 밀렸다가 한꺼번에 내놓은 것이다.
/// </remarks>
public sealed class MotionStats
{
    public const double PauseMs = 500;
    public const double StallMs = 15;
    public const double BurstGapMs = 0.25;
    public const int BurstMinCount = 4;

    private readonly object _gate = new();
    private int _count;
    private double _first, _last;
    private int _intervals;
    private double _intervalSum, _maxGap;
    private int _stalls, _pauses;
    private int _run = 1, _bursts, _maxBurst;

    public int Count { get { lock (_gate) return _count; } }

    /// <summary>이동 하나가 도착(또는 나갔다). <paramref name="timeMs"/> 는 단조 증가하는 밀리초.</summary>
    public void Record(double timeMs)
    {
        lock (_gate)
        {
            if (_count++ == 0)
            {
                _first = _last = timeMs;
                return;
            }
            double gap = timeMs - _last;
            _last = timeMs;

            if (gap < BurstGapMs) _run++;
            else CloseRunLocked();

            if (gap >= PauseMs) { _pauses++; return; }
            _intervals++;
            _intervalSum += gap;
            if (gap > _maxGap) _maxGap = gap;
            if (gap >= StallMs) _stalls++;
        }
    }

    private void CloseRunLocked()
    {
        if (_run >= BurstMinCount)
        {
            _bursts++;
            if (_run > _maxBurst) _maxBurst = _run;
        }
        _run = 1;
    }

    /// <summary>쌓은 것을 요약하고 비운다. 하나도 없으면 null.</summary>
    public MotionSummary? TakeSummary()
    {
        lock (_gate)
        {
            if (_count == 0) return null;
            CloseRunLocked();
            var s = new MotionSummary(_count, _last - _first, _intervals == 0 ? 0 : _intervalSum / _intervals,
                _maxGap, _stalls, _pauses, _bursts, _maxBurst);
            _count = _intervals = _stalls = _pauses = _bursts = _maxBurst = 0;
            _intervalSum = _maxGap = 0;
            _run = 1;
            return s;
        }
    }
}

/// <param name="Count">이동 개수</param>
/// <param name="DurationMs">처음부터 마지막까지 걸린 시간</param>
/// <param name="MeanIntervalMs">쉼을 뺀 평균 간격</param>
/// <param name="MaxGapMs">쉼을 뺀 가장 긴 간격</param>
/// <param name="Stalls">공백(멈칫) 횟수</param>
/// <param name="Pauses">쉼 횟수</param>
/// <param name="Bursts">몰림 묶음 수</param>
/// <param name="MaxBurst">가장 큰 몰림 묶음의 이동 개수</param>
public sealed record MotionSummary(int Count, double DurationMs, double MeanIntervalMs, double MaxGapMs,
    int Stalls, int Pauses, int Bursts, int MaxBurst)
{
    public override string ToString() =>
        $"{Count}개, {DurationMs / 1000:0.0}초, 평균 간격 {MeanIntervalMs:0.0}ms, 가장 긴 공백 {MaxGapMs:0}ms, " +
        $"{MotionStats.StallMs:0}ms 넘는 공백 {Stalls}번, 몰림 {Bursts}번(가장 큰 묶음 {MaxBurst}개), 쉼 {Pauses}번";
}
