using System.Text;
using ShareCom.Core.Diagnostics;

namespace ShareCom.Core.Tests;

public sealed class LogFileTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sharecom-log-" + Guid.NewGuid().ToString("N"));
    // 쓰기 스레드도 이 시각을 읽으므로 시험 중에는 바꾸지 않는다. 시각을 옮길 때는 Add 에 시각을 넘긴다.
    private readonly DateTime _now = new(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);

    public LogFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string LogPath => Path.Combine(_dir, "log.txt");

    private LogFile Open(long maxBytes = LogFile.DefaultMaxBytes) =>
        new(LogPath, maxBytes, TimeSpan.FromMinutes(10), () => _now);

    /// <summary>쓰는 중인 파일을 메모장처럼(쓰기 공유를 허락하고) 읽는다.</summary>
    private static string[] ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Flush_뒤에는_넣은_기록이_순서대로_파일에_있고_쓰는_중에도_읽을_수_있다()
    {
        using var log = Open();
        log.Add("정보", "시작: PC1");
        log.Add("오류", "설정 저장 실패");
        Assert.True(log.Flush(Wait));

        var lines = ReadShared(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("[정보] 시작: PC1", lines[0]);
        Assert.EndsWith("[오류] 설정 저장 실패", lines[1]);
        Assert.StartsWith(_now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"), lines[0]);
    }

    [Fact]
    public void 넘긴_시각이_있으면_그_시각으로_적는다()
    {
        using var log = Open();
        var early = _now.AddSeconds(-30);
        log.Add("오류", "COM 보안 설정 실패", early);
        Assert.True(log.Flush(Wait));
        Assert.StartsWith(early.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"), ReadShared(LogPath)[0]);
    }

    [Fact]
    public void 같은_기록이_되풀이되면_한_번만_쓰고_끝낼_때_요약을_남긴다()
    {
        using var log = Open();
        for (int i = 0; i < 300; i++)
            log.Add("자세히", "연결 실패: 192.168.0.2:47821 - 연결 시간 초과", _now.AddSeconds(i));
        Assert.True(log.Flush(Wait));

        var lines = ReadShared(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("[자세히] 연결 실패: 192.168.0.2:47821 - 연결 시간 초과", lines[0]);
        Assert.Contains("[자세히] (같은 기록 299번 더 - 10분 동안 묶음) 연결 실패: 192.168.0.2:47821", lines[1]);
    }

    [Fact]
    public void 창이_끝나면_요약을_먼저_쓰고_다음_기록을_쓴다()
    {
        using var log = Open();
        log.Add("자세히", "연결 실패: A", _now);
        log.Add("자세히", "연결 실패: A", _now.AddSeconds(2));
        log.Add("자세히", "연결 실패: A", _now.AddSeconds(4));
        log.Add("정보", "연결됨: PC2", _now.AddMinutes(11));
        Assert.True(log.Flush(Wait));

        var lines = ReadShared(LogPath);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("[자세히] 연결 실패: A", lines[0]);
        Assert.Contains("(같은 기록 2번 더 - 10분 동안 묶음) 연결 실패: A", lines[1]);
        Assert.EndsWith("[정보] 연결됨: PC2", lines[2]);
    }

    [Fact]
    public void 오류_요약에는_스택_추적_없이_첫_줄만_다시_적는다()
    {
        using var log = Open();
        var error = "클립보드 보내기 실패: System.IO.IOException: 잠김" + Environment.NewLine + "   at Foo.Bar()";
        log.Add("오류", error);
        log.Add("오류", error);
        Assert.True(log.Flush(Wait));

        var lines = ReadShared(LogPath);
        Assert.Equal(3, lines.Length); // 원래 기록 두 줄(스택 포함) + 요약 한 줄
        Assert.EndsWith("(같은 기록 1번 더 - 10분 동안 묶음) 클립보드 보내기 실패: System.IO.IOException: 잠김", lines[2]);
    }

    [Fact]
    public void 크기를_넘게_되면_old_로_돌리고_두_파일_합이_상한의_두_배를_넘지_않는다()
    {
        const long max = 4096;
        using var log = Open(max);
        for (int i = 0; i < 400; i++)
        {
            log.Add("정보", $"기록 {i:D4} " + new string('가', 20));
            if (i % 10 == 9) Assert.True(log.Flush(Wait)); // 여러 묶음으로 나눠 쓴다
        }
        Assert.True(log.Flush(Wait));

        var old = Path.ChangeExtension(LogPath, ".old.txt");
        Assert.True(File.Exists(old));
        Assert.True(new FileInfo(LogPath).Length <= max);
        Assert.True(new FileInfo(old).Length <= max);
        Assert.EndsWith("기록 0399 " + new string('가', 20), ReadShared(LogPath)[^1]);
        Assert.Equal(2, Directory.GetFiles(_dir, "log*").Length); // log.txt 와 log.old.txt 둘뿐
    }

    [Fact]
    public void 이미_큰_예전_파일이_있으면_처음_쓸_때_돌린다()
    {
        File.WriteAllText(LogPath, new string('x', 5000));
        using var log = Open(4096);
        log.Add("정보", "시작");
        Assert.True(log.Flush(Wait));

        Assert.Single(ReadShared(LogPath));
        Assert.Equal(5000, new FileInfo(Path.ChangeExtension(LogPath, ".old.txt")).Length);
    }

    [Fact]
    public void Dispose_하면_남은_요약까지_쓰고_파일을_닫는다()
    {
        var log = Open();
        log.Add("자세히", "가");
        log.Add("자세히", "가");
        log.Dispose();

        var lines = File.ReadAllLines(LogPath); // 닫혔으니 공유 없이도 열린다
        Assert.Equal(2, lines.Length);
        Assert.Contains("(같은 기록 1번 더", lines[1]);
        log.Add("정보", "닫은 뒤"); // 닫은 뒤 넣어도 터지지 않는다
        Assert.False(log.Flush(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void 실제_시계로도_창이_끝나면_Flush_없이_요약을_쓴다()
    {
        using var log = new LogFile(LogPath, repeatWindow: TimeSpan.FromMilliseconds(200));
        log.Add("자세히", "연결 실패: B");
        log.Add("자세히", "연결 실패: B");
        log.Add("자세히", "연결 실패: B");

        var deadline = DateTime.UtcNow + Wait;
        string[] lines = [];
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(LogPath) && (lines = ReadShared(LogPath)).Length >= 2) break;
            Thread.Sleep(50);
        }
        Assert.Equal(2, lines.Length);
        Assert.Contains("(같은 기록 2번 더 - 0.2초 동안 묶음) 연결 실패: B", lines[1]);
    }

    [Fact]
    public void 여러_스레드가_동시에_넣어도_빠짐없이_쓴다()
    {
        using var log = Open();
        Parallel.For(0, 8, t =>
        {
            for (int i = 0; i < 500; i++) log.Add("정보", $"스레드 {t} 기록 {i}");
        });
        Assert.True(log.Flush(Wait));
        Assert.Equal(4000, ReadShared(LogPath).Length);
    }
}
