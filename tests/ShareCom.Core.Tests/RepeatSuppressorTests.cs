using ShareCom.Core.Diagnostics;

namespace ShareCom.Core.Tests;

public class RepeatSuppressorTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    [Fact]
    public void 처음_기록은_쓰고_창_안의_같은_기록은_세기만_한다()
    {
        var s = new RepeatSuppressor(Window);
        Assert.True(s.ShouldWrite("자세히", "연결 실패: 192.168.0.2:47821", T0));
        Assert.False(s.ShouldWrite("자세히", "연결 실패: 192.168.0.2:47821", T0.AddSeconds(2)));
        Assert.False(s.ShouldWrite("자세히", "연결 실패: 192.168.0.2:47821", T0.AddMinutes(9)));
    }

    [Fact]
    public void 창이_끝나기_전에는_요약이_없고_끝나면_센_횟수가_나온다()
    {
        var s = new RepeatSuppressor(Window);
        s.ShouldWrite("자세히", "연결 실패", T0);
        for (int i = 1; i <= 5; i++) s.ShouldWrite("자세히", "연결 실패", T0.AddSeconds(2 * i));

        Assert.Equal(T0 + Window, s.NextDue);
        Assert.Empty(s.TakeDue(T0.AddMinutes(9)));
        var due = Assert.Single(s.TakeDue(T0 + Window));
        Assert.Equal(new RepeatSummary("자세히", "연결 실패", 5, Window), due);
        Assert.Null(s.NextDue);
        Assert.Empty(s.TakeDue(T0.AddHours(1)));
    }

    [Fact]
    public void 창이_끝난_뒤_다시_나오면_다시_쓰고_새_창을_연다()
    {
        var s = new RepeatSuppressor(Window);
        s.ShouldWrite("정보", "끊김: PC2", T0);
        Assert.Empty(s.TakeDue(T0.AddMinutes(11))); // 묶은 것이 없으면 요약도 없다
        Assert.True(s.ShouldWrite("정보", "끊김: PC2", T0.AddMinutes(11)));
        Assert.False(s.ShouldWrite("정보", "끊김: PC2", T0.AddMinutes(12)));
    }

    [Fact]
    public void 수준이나_문구가_한_글자라도_다르면_다른_기록이다()
    {
        var s = new RepeatSuppressor(Window);
        Assert.True(s.ShouldWrite("정보", "연결 실패: 192.168.0.2:47821", T0));
        Assert.True(s.ShouldWrite("오류", "연결 실패: 192.168.0.2:47821", T0));
        Assert.True(s.ShouldWrite("정보", "연결 실패: 192.168.0.3:47821", T0));
        Assert.Null(s.NextDue);
    }

    [Fact]
    public void 요약을_꺼내기_전에_창이_끝난_기록이_다시_오면_요약을_잃지_않는다()
    {
        var s = new RepeatSuppressor(Window);
        s.ShouldWrite("자세히", "연결 실패", T0);
        s.ShouldWrite("자세히", "연결 실패", T0.AddMinutes(1));
        s.ShouldWrite("자세히", "연결 실패", T0.AddMinutes(2));

        Assert.True(s.ShouldWrite("자세히", "연결 실패", T0.AddMinutes(10.5)));
        Assert.Equal(DateTime.MinValue, s.NextDue); // 바로 꺼내야 할 요약이 있다
        var due = Assert.Single(s.TakeDue(T0.AddMinutes(10.5)));
        Assert.Equal(2, due.Count);
        Assert.False(s.ShouldWrite("자세히", "연결 실패", T0.AddMinutes(11)));
    }

    [Fact]
    public void TakeAll_은_창이_안_끝났어도_요약을_모두_꺼내고_처음으로_돌아간다()
    {
        var s = new RepeatSuppressor(Window);
        s.ShouldWrite("자세히", "가", T0);
        s.ShouldWrite("자세히", "가", T0.AddSeconds(1));
        s.ShouldWrite("자세히", "나", T0);

        var all = s.TakeAll();
        Assert.Equal([new RepeatSummary("자세히", "가", 1, Window)], all);
        Assert.Null(s.NextDue);
        Assert.True(s.ShouldWrite("자세히", "가", T0.AddSeconds(2)));
    }

    [Fact]
    public void 종류가_너무_많으면_묶지_않고_그냥_쓴다()
    {
        var s = new RepeatSuppressor(Window, maxKeys: 2);
        Assert.True(s.ShouldWrite("정보", "가", T0));
        Assert.True(s.ShouldWrite("정보", "나", T0));
        Assert.True(s.ShouldWrite("정보", "다", T0));
        Assert.True(s.ShouldWrite("정보", "다", T0)); // 추적하지 못하니 묶지도 않는다
        Assert.False(s.ShouldWrite("정보", "가", T0)); // 이미 추적하던 것은 그대로 묶는다
        // 조용히 창이 끝난 것은 치워서 새 종류를 받는다.
        Assert.True(s.ShouldWrite("정보", "라", T0.AddMinutes(11)));
        Assert.False(s.ShouldWrite("정보", "라", T0.AddMinutes(12)));
    }
}
