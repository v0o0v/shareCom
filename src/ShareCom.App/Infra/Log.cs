using System.Collections.Concurrent;
using System.Text;

namespace ShareCom.App.Infra;

/// <summary>%APPDATA%\ShareCom\log.txt 에 남기는 간단한 기록. 쓰기는 별도 스레드에서 한다(훅 스레드를 붙잡지 않게).</summary>
internal static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly BlockingCollection<string> Lines = new(10_000);
    private static string? _path;
    private static int _debugCount;

    public static string? FilePath => _path;

    public static void Init(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "log.txt");
        var t = new Thread(WriteLoop) { IsBackground = true, Name = "ShareCom 기록" };
        t.Start();
    }

    public static void Info(string message) => Add("정보", message);

    public static void Error(string message, Exception? ex = null) =>
        Add("오류", ex is null ? message : $"{message}: {ex}");

    /// <summary>자주 생길 수 있는 진단 기록. 넘치지 않게 실행마다 200줄까지만 남긴다.</summary>
    public static void Debug(string message)
    {
        if (Interlocked.Increment(ref _debugCount) <= 200) Add("진단", message);
    }

    private static void Add(string level, string message) =>
        Lines.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");

    /// <summary>프로그램이 끝나기 전에 남은 기록을 쓴다.</summary>
    public static void Flush()
    {
        if (_path is null) return;
        var sb = new StringBuilder();
        while (Lines.TryTake(out var line)) sb.AppendLine(line);
        if (sb.Length > 0) try { File.AppendAllText(_path, sb.ToString(), Encoding.UTF8); } catch { }
    }

    private static void WriteLoop()
    {
        foreach (var first in Lines.GetConsumingEnumerable())
        {
            var sb = new StringBuilder(first).AppendLine();
            while (Lines.TryTake(out var more)) sb.AppendLine(more);
            try
            {
                var info = new FileInfo(_path!);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(_path!, Path.ChangeExtension(_path!, ".old.txt"), overwrite: true);
                File.AppendAllText(_path!, sb.ToString(), Encoding.UTF8);
            }
            catch { /* 기록 실패로 프로그램을 멈추지 않는다 */ }
        }
    }
}
