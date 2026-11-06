using System.Collections.Concurrent;
using System.Text;

namespace ShareCom.Core.Diagnostics;

/// <summary>
/// 기록 파일 하나를 맡는다. 부르는 쪽은 시각과 문구만 큐에 넣고 바로 돌아간다(훅 스레드를 붙잡지 않게).
/// 줄 만들기와 파일 쓰기는 전용 스레드 하나가 한다. 파일은 열어 둔 채로 쓰고(실행 중에도 메모장으로 열 수 있게 공유),
/// 한 묶음을 쓸 때마다 flush 한다. 파일이 <c>maxBytes</c> 를 넘게 되면 <c>.old.txt</c> 로 돌리고 새로 시작하므로
/// 두 파일을 합쳐도 대략 2 × maxBytes 를 넘지 않는다. 같은 기록 되풀이는 <see cref="RepeatSuppressor"/> 로 묶는다.
/// </summary>
public sealed class LogFile : IDisposable
{
    public const long DefaultMaxBytes = 1024 * 1024;
    public static readonly TimeSpan DefaultRepeatWindow = TimeSpan.FromMinutes(10);
    private const int QueueCapacity = 10_000;
    private const int MaxBatch = 1_000;
    private const int SummaryMessageLength = 200;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>큐에 넣는 것. <see cref="FlushDone"/> 가 있으면 기록이 아니라 "여기까지 다 쓰라"는 표시다.</summary>
    private readonly record struct Item(string Level, string Message, DateTime Utc, ManualResetEventSlim? FlushDone);

    private readonly BlockingCollection<Item> _queue = new(QueueCapacity);
    private readonly RepeatSuppressor _repeats;
    private readonly Func<DateTime> _utcNow;
    private readonly long _maxBytes;
    private readonly Thread _thread;
    private readonly StringBuilder _text = new();
    private FileStream? _stream;
    private long _size;
    private int _dropped;

    public LogFile(string path, long maxBytes = DefaultMaxBytes, TimeSpan? repeatWindow = null, Func<DateTime>? utcNow = null)
    {
        Path = path;
        _maxBytes = maxBytes;
        _repeats = new RepeatSuppressor(repeatWindow ?? DefaultRepeatWindow);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "ShareCom 기록" };
        _thread.Start();
    }

    public string Path { get; }

    /// <summary>파일이 꽉 차면 옮겨 두는 이전 기록.</summary>
    public string OldPath => System.IO.Path.ChangeExtension(Path, ".old.txt");

    /// <summary>기록 하나를 넣는다. 큐가 꽉 찼으면 버리고 센다(나중에 버린 줄 수를 한 줄로 남긴다).</summary>
    /// <param name="utc">기록한 시각. 없으면 지금.</param>
    public void Add(string level, string message, DateTime? utc = null)
    {
        try
        {
            if (_queue.TryAdd(new Item(level, message, utc ?? _utcNow(), null))) return;
        }
        catch (InvalidOperationException) { return; } // 이미 닫았다
        Interlocked.Increment(ref _dropped);
    }

    /// <summary>
    /// 지금까지 넣은 기록과 묶어 둔 되풀이 요약을 모두 파일에 쓰고 기다린다. 프로그램을 끝내기 전에 부른다.
    /// 쓰기는 늘 전용 스레드가 하므로 겹쳐 쓰지 않는다. 시간 안에 못 쓰면 false.
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        var done = new ManualResetEventSlim();
        try
        {
            if (!_queue.TryAdd(new Item("", "", default, done), timeout)) return false;
        }
        catch (InvalidOperationException) { return false; }
        // 늦게 끝나도 쓰기 스레드가 Set 할 수 있게 여기서 Dispose 하지 않는다.
        return done.Wait(timeout);
    }

    /// <summary>남은 기록을 쓰고 파일을 닫는다.</summary>
    public void Dispose()
    {
        try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        _thread.Join(TimeSpan.FromSeconds(5));
    }

    private void WriteLoop()
    {
        var batch = new List<Item>(64);
        while (TakeBatch(batch))
        {
            _text.Clear();
            List<ManualResetEventSlim>? flushes = null;
            foreach (var item in batch)
            {
                if (item.FlushDone is { } done)
                {
                    AppendSummaries(_repeats.TakeAll(), _utcNow());
                    (flushes ??= new()).Add(done);
                    continue;
                }
                AppendSummaries(_repeats.TakeDue(item.Utc), item.Utc);
                if (_repeats.ShouldWrite(item.Level, item.Message, item.Utc))
                    AppendLine(item.Utc, item.Level, item.Message);
            }
            var now = _utcNow();
            AppendSummaries(_repeats.TakeDue(now), now);
            if (Interlocked.Exchange(ref _dropped, 0) is var dropped and > 0)
                AppendLine(now, "오류", $"기록이 한꺼번에 너무 많이 들어와 {dropped}줄을 버렸습니다.");
            Write();
            if (flushes is not null) foreach (var f in flushes) f.Set();
        }
        _text.Clear();
        AppendSummaries(_repeats.TakeAll(), _utcNow());
        Write();
        CloseStream();
    }

    /// <summary>
    /// 기록이 들어오거나 되풀이 요약을 낼 때가 될 때까지 기다린다. 닫혔고 남은 것이 없으면 false.
    /// </summary>
    private bool TakeBatch(List<Item> batch)
    {
        batch.Clear();
        int timeout = Timeout.Infinite;
        if (_repeats.NextDue is { } due)
            timeout = (int)Math.Clamp(Math.Ceiling((due - _utcNow()).TotalMilliseconds), 0, int.MaxValue);
        try
        {
            if (_queue.TryTake(out var first, timeout))
            {
                batch.Add(first);
                while (batch.Count < MaxBatch && _queue.TryTake(out var more)) batch.Add(more);
                return true;
            }
            return !_queue.IsCompleted;
        }
        catch (ObjectDisposedException) { return false; }
    }

    private void AppendLine(DateTime utc, string level, string message) =>
        _text.Append(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(level).Append("] ").AppendLine(message);

    private void AppendSummaries(List<RepeatSummary> summaries, DateTime utc)
    {
        foreach (var s in summaries)
            AppendLine(utc, s.Level, $"(같은 기록 {s.Count}번 더 - {WindowText(s.Window)} 동안 묶음) {Shorten(s.Message)}");
    }

    private static string WindowText(TimeSpan w) =>
        w.TotalMinutes >= 1 ? $"{w.TotalMinutes:0.#}분" : $"{w.TotalSeconds:0.#}초";

    /// <summary>요약에는 첫 줄 앞부분만 다시 적는다(오류의 스택 추적은 위에 이미 있다).</summary>
    private static string Shorten(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        var first = end < 0 ? message : message[..end];
        return first.Length <= SummaryMessageLength ? first : first[..SummaryMessageLength] + "…";
    }

    private void Write()
    {
        if (_text.Length == 0) return;
        try
        {
            var bytes = Utf8.GetBytes(_text.ToString());
            var stream = Open();
            if (_size > 0 && _size + bytes.Length > _maxBytes) stream = Rotate();
            if (_size == 0)
            {
                stream.Write(Utf8.Preamble);
                _size += Utf8.Preamble.Length;
            }
            stream.Write(bytes);
            stream.Flush();
            _size += bytes.Length;
        }
        catch
        {
            CloseStream(); // 기록 실패로 프로그램을 멈추지 않는다. 다음 묶음 때 다시 연다.
        }
    }

    private FileStream Open()
    {
        if (_stream is not null) return _stream;
        _stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _size = _stream.Length;
        return _stream;
    }

    private FileStream Rotate()
    {
        CloseStream();
        try { File.Move(Path, OldPath, overwrite: true); }
        catch { /* 다른 프로그램이 잡고 있으면 이번에는 그냥 이어 쓴다 */ }
        return Open();
    }

    private void CloseStream()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }
}
