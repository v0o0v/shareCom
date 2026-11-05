using ShareCom.Core.Input;

namespace ShareCom.Core.Tests;

public class MotionStatsTests
{
    private static MotionSummary Run(params double[] times)
    {
        var s = new MotionStats();
        foreach (var t in times) s.Record(t);
        return s.TakeSummary()!;
    }

    [Fact]
    public void 고르게_오면_공백도_몰림도_없다()
    {
        var times = Enumerable.Range(0, 101).Select(i => i * 4.0).ToArray();
        var r = Run(times);
        Assert.Equal(101, r.Count);
        Assert.Equal(400, r.DurationMs, 3);
        Assert.Equal(4, r.MeanIntervalMs, 3);
        Assert.Equal(4, r.MaxGapMs, 3);
        Assert.Equal(0, r.Stalls);
        Assert.Equal(0, r.Bursts);
    }

    [Fact]
    public void 멈칫했다가_한꺼번에_오면_공백과_몰림으로_센다()
    {
        // 4ms 간격으로 오다가 60ms 비고, 그다음 6개가 거의 같은 때 도착
        var times = new List<double> { 0, 4, 8 };
        times.AddRange([68, 68.01, 68.02, 68.03, 68.04, 68.05]);
        times.AddRange([72, 76]);
        var r = Run(times.ToArray());
        Assert.Equal(1, r.Stalls);
        Assert.Equal(60, r.MaxGapMs, 3);
        Assert.Equal(1, r.Bursts);
        Assert.Equal(6, r.MaxBurst);
    }

    [Fact]
    public void 쉼은_간격_통계에서_뺀다()
    {
        var r = Run(0, 4, 8, 2008, 2012);
        Assert.Equal(1, r.Pauses);
        Assert.Equal(0, r.Stalls);
        Assert.Equal(4, r.MaxGapMs, 3);
        Assert.Equal(4, r.MeanIntervalMs, 3);
    }

    [Fact]
    public void 끝에서_몰린_묶음도_센다()
    {
        var r = Run(0, 4, 30, 30.01, 30.02, 30.03);
        Assert.Equal(1, r.Bursts);
        Assert.Equal(4, r.MaxBurst);
    }

    [Fact]
    public void 세_개까지는_몰림이_아니다()
    {
        var r = Run(0, 4, 30, 30.01, 30.02, 34);
        Assert.Equal(0, r.Bursts);
    }

    [Fact]
    public void 요약하면_비워지고_없으면_null()
    {
        var s = new MotionStats();
        Assert.Null(s.TakeSummary());
        s.Record(1); s.Record(5);
        Assert.Equal(2, s.TakeSummary()!.Count);
        Assert.Null(s.TakeSummary());
        s.Record(100);
        var r = s.TakeSummary()!;
        Assert.Equal(1, r.Count);
        Assert.Equal(0, r.DurationMs);
    }

    [Fact]
    public void 요약_한_줄에_숫자가_들어간다()
    {
        var text = Run(0, 4, 8).ToString();
        Assert.Contains("3개", text);
        Assert.Contains("평균 간격 4.0ms", text);
    }
}
