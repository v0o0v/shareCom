using System.Collections.Concurrent;
using ShareCom.Core.Diagnostics;

namespace ShareCom.App.Infra;

/// <summary>
/// %APPDATA%\ShareCom\log.txt 에 남기는 기록. 쓰기는 <see cref="LogFile"/> 의 전용 스레드가 한다.
/// 기본으로는 오류와 큰 사건(<see cref="Info"/>, <see cref="Error"/>)만 남기고,
/// 설정에서 [자세한 기록]을 켰을 때만 <see cref="Verbose"/>·<see cref="Debug"/> 를 남긴다.
/// </summary>
internal static class Log
{
    private const int MaxDebugLines = 200;
    private const int MaxEarlyLines = 100;
    private static readonly ConcurrentQueue<(string Level, string Message, DateTime Utc)> Early = new();
    private static LogFile? _file;
    private static volatile bool _verbose;
    private static int _debugCount;

    public static string? FilePath => _file?.Path;

    /// <summary>자세한 기록을 남길지. 설정 창에서 바꾸면 바로 적용한다.</summary>
    public static bool VerboseEnabled
    {
        get => _verbose;
        set => _verbose = value;
    }

    public static void Init(string directory)
    {
        if (_file is not null) return;
        Directory.CreateDirectory(directory);
        var file = new LogFile(Path.Combine(directory, "log.txt"));
        _file = file;
        // Init 전에 남긴 기록(예: COM 보안 설정 실패)은 그때 시각 그대로 넘긴다.
        while (Early.TryDequeue(out var e)) file.Add(e.Level, e.Message, e.Utc);
    }

    /// <summary>큰 사건. 늘 남긴다.</summary>
    public static void Info(string message) => Add("정보", message);

    /// <summary>오류. 늘 남긴다.</summary>
    public static void Error(string message, Exception? ex = null) =>
        Add("오류", ex is null ? message : $"{message}: {ex}");

    /// <summary>자주 생기는 일상 기록(연결 재시도, 클립보드·파일 오감 등). 자세한 기록을 켰을 때만 남긴다.</summary>
    public static void Verbose(string message)
    {
        if (_verbose) Add("자세히", message);
    }

    /// <summary>진단 기록. 자세한 기록을 켰을 때만, 그것도 실행마다 200줄까지만 남긴다.</summary>
    public static void Debug(string message)
    {
        if (_verbose && Interlocked.Increment(ref _debugCount) <= MaxDebugLines) Add("진단", message);
    }

    /// <summary>프로그램이 끝나기 전에 남은 기록과 되풀이 요약을 쓴다.</summary>
    public static void Flush() => _file?.Flush(TimeSpan.FromSeconds(2));

    private static void Add(string level, string message)
    {
        if (_file is { } file)
        {
            file.Add(level, message);
            return;
        }
        if (Early.Count < MaxEarlyLines) Early.Enqueue((level, message, DateTime.UtcNow));
    }
}
