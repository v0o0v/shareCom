using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Net;

/// <summary>
/// 짝 맺은 PC 하나와의 연결. 읽기·쓰기·생존 신호를 각각의 루프로 돌린다.
/// 받은 메시지는 받은 순서대로 한 번에 하나씩 <see cref="MessageReceived"/> 로 넘긴다.
/// </summary>
public sealed class PeerConnection
{
    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly Channel<Message> _outbox = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _pingInterval;
    private readonly TimeSpan _timeout;
    private long _lastReceivedTimestamp = Stopwatch.GetTimestamp();
    private int _closed;

    public Guid PeerId { get; }
    public string PeerName { get; }
    public int PeerListenPort { get; }
    public bool Encrypted { get; }
    public bool InitiatedByMe { get; }
    /// <summary>이 연결을 건 쪽의 ID. 양쪽이 동시에 연결했을 때 어느 연결을 남길지 정하는 데 쓴다.</summary>
    public Guid InitiatorId { get; }
    public IPEndPoint? RemoteEndPoint { get; }

    public event Action<PeerConnection, Message>? MessageReceived;
    public event Action<PeerConnection>? Closed;

    internal PeerConnection(TcpClient tcp, HandshakeResult h, bool initiatedByMe, Guid myId, TimeSpan pingInterval, TimeSpan timeout)
    {
        _tcp = tcp;
        _stream = h.Stream;
        PeerId = h.PeerId;
        PeerName = h.PeerName;
        PeerListenPort = h.PeerListenPort;
        Encrypted = h.Mode != ConnectionMode.Plain;
        InitiatedByMe = initiatedByMe;
        InitiatorId = initiatedByMe ? myId : h.PeerId;
        RemoteEndPoint = tcp.Client.RemoteEndPoint as IPEndPoint;
        _pingInterval = pingInterval;
        _timeout = timeout;
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public void Start()
    {
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WriteLoop);
        _ = Task.Run(PingLoop);
    }

    public bool Send(Message message) => !IsClosed && _outbox.Writer.TryWrite(message);

    private async Task ReadLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var m = await MessageCodec.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (m is null) break;
                Interlocked.Exchange(ref _lastReceivedTimestamp, Stopwatch.GetTimestamp());
                switch (m)
                {
                    case PingMessage p: Send(new PongMessage(p.Ticks)); break;
                    case PongMessage: break;
                    default:
                        try { MessageReceived?.Invoke(this, m); }
                        catch (Exception) { /* 처리기 오류로 연결을 끊지 않는다 */ }
                        break;
                }
            }
        }
        catch (Exception) { /* 끊김 */ }
        Close();
    }

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var m in _outbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                await MessageCodec.WriteAsync(_stream, m, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* 끊김 */ }
        Close();
    }

    private async Task PingLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_pingInterval, _cts.Token).ConfigureAwait(false);
                var silent = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceivedTimestamp));
                if (silent > _timeout) break; // 절전·와이파이 끊김으로 조용히 죽은 연결
                Send(new PingMessage(DateTime.UtcNow.Ticks));
            }
        }
        catch (OperationCanceledException) { return; }
        Close();
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _outbox.Writer.TryComplete();
        _cts.Cancel();
        try { _stream.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
        try { Closed?.Invoke(this); } catch { }
    }
}
