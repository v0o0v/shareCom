using System.Collections.Concurrent;
using System.Threading.Channels;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Files;

public interface IFileTransport
{
    bool Send(Guid peer, Message message);
    bool SendBulk(Guid peer, Message message);
    bool SendBlob(Guid peer, BlobKind kind, byte[] data);
}

public sealed record FileProgress(long DoneBytes, long TotalBytes, int DoneFiles, int TotalFiles, string Current);

/// <summary>
/// 파일 전송. 보내는 쪽은 제안(파일 목록)만 먼저 넘기고, 받는 쪽이 읽을 때 조각을 요청한다.
/// 받는 쪽이 읽은 만큼 확인(FileAck)을 보내고, 보내는 쪽은 확인받지 못한 양이 <see cref="WindowBytes"/> 를
/// 넘지 않게 보낸다. 그래서 큰 파일도 메모리에 쌓이지 않는다.
/// </summary>
public sealed class FileTransferService
{
    public const int ChunkSize = 64 * 1024;
    public const long WindowBytes = 8L * 1024 * 1024;
    public const long AckEvery = 1L * 1024 * 1024;
    public const int MaxOffers = 50;

    private readonly IFileTransport _transport;
    private readonly object _gate = new();

    // 보내는 쪽
    private readonly Dictionary<Guid, LocalOffer> _offers = new();
    private readonly Queue<Guid> _offerOrder = new();
    private readonly ConcurrentDictionary<(Guid Peer, uint Req), Outgoing> _outgoing = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _pendingSends = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<(bool, string)>> _pendingResults = new();

    // 받는 쪽
    private readonly ConcurrentDictionary<(Guid Peer, uint Req), RemoteFileStream> _incoming = new();
    private int _nextRequest;

    public FileTransferService(IFileTransport transport) => _transport = transport;

    /// <summary>받는 쪽이 이만큼 아무 확인도 안 보내면 보내기를 그만둔다(탐색기가 복사를 취소한 경우 등).</summary>
    public TimeSpan SenderIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>받는 쪽이 이만큼 아무 조각도 못 받으면 읽기를 실패로 끝낸다.</summary>
    public TimeSpan ReceiverIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>다른 PC가 트레이로 파일을 보내려 한다. 사용자에게 물은 뒤 <see cref="Respond"/> 를 부른다.</summary>
    public event Action<Guid, FileOffer>? SendRequested;
    public event Action<string>? Log;

    // ───────────── 보내는 쪽 ─────────────

    /// <summary>파일·폴더로 제안을 만들어 등록한다(최근 <see cref="MaxOffers"/> 개까지 들고 있다).</summary>
    public FileOffer RegisterOffer(IEnumerable<string> paths)
    {
        var local = LocalOffer.Build(paths);
        lock (_gate)
        {
            _offers[local.Offer.Id] = local;
            _offerOrder.Enqueue(local.Offer.Id);
            while (_offerOrder.Count > MaxOffers) _offers.Remove(_offerOrder.Dequeue());
        }
        return local.Offer;
    }

    /// <summary>
    /// 상대 PC에 파일을 보내겠다고 묻는다. 상대가 받으면 true(이후 상대가 내용을 끌어간다).
    /// 다 받았는지는 <see cref="WaitResultAsync"/> 로 기다린다.
    /// </summary>
    public async Task<(bool Accepted, FileOffer Offer)> RequestSendAsync(Guid peer, IEnumerable<string> paths, CancellationToken ct = default)
    {
        var offer = RegisterOffer(paths);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSends[offer.Id] = tcs;
        _pendingResults[offer.Id] = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!_transport.SendBlob(peer, BlobKind.FileSendRequest, offer.Encode()))
                throw new IOException("상대 PC와 연결되어 있지 않습니다.");
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return (await tcs.Task.ConfigureAwait(false), offer);
        }
        finally
        {
            _pendingSends.TryRemove(offer.Id, out _);
        }
    }

    /// <summary>상대가 다 받았는지(성공, 오류 문구).</summary>
    public async Task<(bool Success, string Error)> WaitResultAsync(Guid offerId, CancellationToken ct = default)
    {
        if (!_pendingResults.TryGetValue(offerId, out var tcs)) return (false, "알 수 없는 전송");
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try { return await tcs.Task.ConfigureAwait(false); }
        finally { _pendingResults.TryRemove(offerId, out _); }
    }

    private sealed class Outgoing
    {
        public required CancellationTokenSource Cts { get; init; }
        public long Acked;
        public readonly SemaphoreSlim AckSignal = new(0);
    }

    private void OnOpen(Guid peer, FileOpenMessage m)
    {
        string? path = null;
        lock (_gate)
        {
            if (_offers.TryGetValue(m.OfferId, out var local) && m.Index >= 0 && m.Index < local.Offer.Entries.Count
                && !local.Offer.Entries[m.Index].IsDirectory)
                path = local.FullPaths[m.Index];
        }
        if (path is null)
        {
            // 제안에 없는 파일은 절대 보내지 않는다.
            _transport.Send(peer, new FileErrorMessage(m.RequestId, "원본 PC에 그 파일 목록이 없습니다. 원본 PC에서 다시 복사해 주세요."));
            return;
        }
        var o = new Outgoing { Cts = new CancellationTokenSource() };
        if (!_outgoing.TryAdd((peer, m.RequestId), o)) return;
        _ = Task.Run(() => SendFileAsync(peer, m, path, o));
    }

    private async Task SendFileAsync(Guid peer, FileOpenMessage m, string path, Outgoing o)
    {
        var ct = o.Cts.Token;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (m.Offset > 0) fs.Seek(m.Offset, SeekOrigin.Begin);
            long sent = m.Offset;
            var buffer = new byte[ChunkSize];
            while (true)
            {
                while (sent - Interlocked.Read(ref o.Acked) > WindowBytes)
                {
                    if (!await o.AckSignal.WaitAsync(SenderIdleTimeout, ct).ConfigureAwait(false))
                        throw new TimeoutException("받는 쪽이 응답하지 않습니다.");
                }
                int n = await fs.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0)
                {
                    _transport.SendBulk(peer, new FileChunkMessage(m.RequestId, [], true));
                    break;
                }
                if (!_transport.SendBulk(peer, new FileChunkMessage(m.RequestId, buffer[..n], false)))
                    throw new IOException("연결이 끊겼습니다.");
                sent += n;
            }
        }
        catch (OperationCanceledException) { /* 받는 쪽이 취소 */ }
        catch (Exception ex)
        {
            Log?.Invoke($"파일 보내기 실패 {path}: {ex.Message}");
            _transport.Send(peer, new FileErrorMessage(m.RequestId, ex is FileNotFoundException or DirectoryNotFoundException
                ? "원본 파일이 없어졌습니다."
                : ex.Message));
        }
        finally
        {
            _outgoing.TryRemove((peer, m.RequestId), out _);
            o.Cts.Dispose();
        }
    }

    // ───────────── 받는 쪽 ─────────────

    /// <summary>상대 PC 제안의 Index 번째 파일을 읽는 스트림. 처음 읽을 때 요청을 보낸다.</summary>
    public Stream OpenRemote(Guid peer, Guid offerId, int index, long size)
    {
        uint req = unchecked((uint)Interlocked.Increment(ref _nextRequest));
        var stream = new RemoteFileStream(this, peer, req, offerId, index, size);
        _incoming[(peer, req)] = stream;
        return stream;
    }

    /// <summary>트레이로 온 보내기 요청에 답한다.</summary>
    public void Respond(Guid peer, Guid offerId, bool accept) =>
        _transport.Send(peer, new FileSendResponseMessage(offerId, accept));

    /// <summary>
    /// 제안의 파일을 모두 folder 아래에 받는다. 맨 위 항목 이름이 이미 있으면 '이름 (2)' 로 받는다.
    /// 받는 중인 파일은 .sharecom-part 로 쓰다가 다 받으면 이름을 바꾼다. 끝나면 상대에게 결과를 알린다.
    /// </summary>
    public async Task ReceiveToFolderAsync(Guid peer, FileOffer offer, string folder, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        string error = "";
        try
        {
            Directory.CreateDirectory(folder);
            var rename = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long done = 0;
            int files = 0;
            for (int i = 0; i < offer.Entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var e = offer.Entries[i];
                var parts = SafePath.Normalize(e.RelativePath).Split('\\');
                if (!rename.TryGetValue(parts[0], out var top))
                {
                    top = UniqueName(folder, parts[0], isDirectory: e.IsDirectory || parts.Length > 1);
                    rename[parts[0]] = top;
                }
                parts[0] = top;
                var target = Path.Combine([folder, .. parts]);
                if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("받는 폴더 밖으로 나가는 경로입니다.");

                if (e.IsDirectory)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var part = target + ".sharecom-part";
                progress?.Report(new FileProgress(done, offer.TotalBytes, files, offer.FileCount, e.RelativePath));
                try
                {
                    await using (var src = OpenRemote(peer, offer.Id, i, e.Size))
                    await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.Asynchronous))
                    {
                        var buffer = new byte[ChunkSize];
                        int n;
                        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            done += n;
                            progress?.Report(new FileProgress(done, offer.TotalBytes, files, offer.FileCount, e.RelativePath));
                        }
                    }
                    File.Move(part, target, overwrite: false);
                    if (e.LastWriteUtcTicks > 0)
                        try { File.SetLastWriteTimeUtc(target, new DateTime(e.LastWriteUtcTicks, DateTimeKind.Utc)); } catch { }
                }
                catch
                {
                    try { File.Delete(part); } catch { }
                    throw;
                }
                files++;
            }
            progress?.Report(new FileProgress(done, offer.TotalBytes, files, offer.FileCount, ""));
        }
        catch (OperationCanceledException) { error = "받는 쪽에서 취소했습니다."; throw; }
        catch (Exception ex) { error = ex.Message; throw; }
        finally
        {
            _transport.Send(peer, new FileSendResultMessage(offer.Id, error.Length == 0, error));
        }
    }

    /// <summary>folder 에 name 이 이미 있으면 'name (2)', 'name (3)' … 중 없는 이름.</summary>
    public static string UniqueName(string folder, string name, bool isDirectory)
    {
        bool Exists(string n) => File.Exists(Path.Combine(folder, n)) || Directory.Exists(Path.Combine(folder, n));
        if (!Exists(name)) return name;
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDirectory ? "" : Path.GetExtension(name);
        for (int k = 2; ; k++)
        {
            var candidate = $"{stem} ({k}){ext}";
            if (!Exists(candidate)) return candidate;
        }
    }

    // ───────────── 메시지 ─────────────

    /// <summary>파일 관련 메시지면 처리하고 true.</summary>
    public bool OnMessage(Guid peer, Message message)
    {
        switch (message)
        {
            case FileOpenMessage m: OnOpen(peer, m); return true;
            case FileAckMessage m:
                if (_outgoing.TryGetValue((peer, m.RequestId), out var o))
                {
                    long prev;
                    do { prev = Interlocked.Read(ref o.Acked); }
                    while (m.Consumed > prev && Interlocked.CompareExchange(ref o.Acked, m.Consumed, prev) != prev);
                    o.AckSignal.Release();
                }
                return true;
            case FileCancelMessage m:
                if (_outgoing.TryGetValue((peer, m.RequestId), out var c)) try { c.Cts.Cancel(); } catch (ObjectDisposedException) { }
                return true;
            case FileChunkMessage m:
                if (_incoming.TryGetValue((peer, m.RequestId), out var s)) s.Deliver(m);
                return true;
            case FileErrorMessage m:
                if (_incoming.TryGetValue((peer, m.RequestId), out var s2)) s2.Fail(m.Error);
                return true;
            case FileSendResponseMessage m:
                if (_pendingSends.TryGetValue(m.OfferId, out var tcs)) tcs.TrySetResult(m.Accepted);
                return true;
            case FileSendResultMessage m:
                if (_pendingResults.TryGetValue(m.OfferId, out var r)) r.TrySetResult((m.Success, m.Error));
                return true;
            default:
                return false;
        }
    }

    public void OnBlob(Guid peer, BlobKind kind, byte[] data)
    {
        if (kind != BlobKind.FileSendRequest) return;
        FileOffer offer;
        try { offer = FileOffer.Decode(data); }
        catch (Exception ex)
        {
            Log?.Invoke($"받은 파일 목록이 잘못돼 거절합니다: {ex.Message}");
            return;
        }
        if (SendRequested is null) Respond(peer, offer.Id, false);
        else SendRequested.Invoke(peer, offer);
    }

    /// <summary>연결이 끊겼다: 그 PC와의 보내기·받기를 모두 끝낸다.</summary>
    public void OnPeerDisconnected(Guid peer)
    {
        foreach (var kv in _outgoing.Where(kv => kv.Key.Peer == peer))
            try { kv.Value.Cts.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var kv in _incoming.Where(kv => kv.Key.Peer == peer))
            kv.Value.Fail("원본 PC와 연결이 끊겼습니다.");
    }

    internal void Forget(Guid peer, uint req) => _incoming.TryRemove((peer, req), out _);
    internal bool SendControl(Guid peer, Message m) => _transport.Send(peer, m);

    /// <summary>다른 PC의 파일 하나를 앞에서부터 차례로 읽는 스트림.</summary>
    private sealed class RemoteFileStream : Stream
    {
        private readonly FileTransferService _svc;
        private readonly Guid _peer;
        private readonly uint _req;
        private readonly Guid _offerId;
        private readonly int _index;
        private readonly long _size;
        private readonly Channel<FileChunkMessage> _chunks = Channel.CreateUnbounded<FileChunkMessage>(new UnboundedChannelOptions { SingleReader = true });
        private string? _error;
        private byte[] _current = [];
        private int _currentPos;
        private long _consumed, _lastAck;
        private bool _opened, _finished, _disposed;

        public RemoteFileStream(FileTransferService svc, Guid peer, uint req, Guid offerId, int index, long size)
        {
            _svc = svc; _peer = peer; _req = req; _offerId = offerId; _index = index; _size = size;
        }

        public void Deliver(FileChunkMessage m) => _chunks.Writer.TryWrite(m);

        public void Fail(string error)
        {
            _error = error;
            _chunks.Writer.TryComplete(new IOException(error));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _size;
        public override long Position { get => _consumed; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.Length == 0 || (_finished && _currentPos >= _current.Length)) return 0;
            if (!_opened)
            {
                _opened = true;
                if (!_svc.SendControl(_peer, new FileOpenMessage(_req, _offerId, _index, 0)))
                    throw new IOException("원본 PC와 연결되어 있지 않습니다.");
            }
            while (_currentPos >= _current.Length)
            {
                FileChunkMessage chunk;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(_svc.ReceiverIdleTimeout);
                    try { chunk = await _chunks.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
                    catch (ChannelClosedException ex) { throw new IOException(_error ?? "전송이 끊겼습니다.", ex.InnerException); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    { throw new IOException("원본 PC가 응답하지 않습니다."); }
                }
                if (chunk.Last && chunk.Data.Length == 0)
                {
                    _finished = true;
                    return 0;
                }
                _current = chunk.Data;
                _currentPos = 0;
                if (chunk.Last) _finished = true;
            }
            int n = Math.Min(buffer.Length, _current.Length - _currentPos);
            _current.AsMemory(_currentPos, n).CopyTo(buffer);
            _currentPos += n;
            _consumed += n;
            if (_consumed - _lastAck >= AckEvery)
            {
                _lastAck = _consumed;
                _svc.SendControl(_peer, new FileAckMessage(_req, _consumed));
            }
            if (_finished && _currentPos >= _current.Length) _current = [];
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_opened && !_finished && _error is null) _svc.SendControl(_peer, new FileCancelMessage(_req));
                _svc.Forget(_peer, _req);
            }
            base.Dispose(disposing);
        }
    }
}
