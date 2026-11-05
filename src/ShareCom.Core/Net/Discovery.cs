using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ShareCom.Core.Net;

public sealed record DiscoveryAnnouncement(Guid Id, string Name, int TcpPort);

public sealed record DiscoveredPeer(Guid Id, string Name, IPEndPoint Endpoint, DateTime LastSeenUtc);

public interface IDiscovery : IDisposable
{
    void Start(DiscoveryAnnouncement self);
    event Action<DiscoveredPeer>? PeerSeen;
    /// <summary>바로 한 번 더 알린다(망이 바뀌었을 때).</summary>
    void AnnounceNow();
}

/// <summary>
/// UDP 브로드캐스트 검색. 2초마다 망의 모든 IPv4 브로드캐스트 주소로 자기 정보를 알리고,
/// 같은 포트로 들어오는 알림을 듣는다.
/// </summary>
public sealed class UdpDiscovery : IDiscovery
{
    private const string Magic = "SHCM-D1 ";
    private readonly int _port;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _client;
    private DiscoveryAnnouncement? _self;
    private readonly SemaphoreSlim _announceSignal = new(0);

    public event Action<DiscoveredPeer>? PeerSeen;

    public UdpDiscovery(int port = 47820, TimeSpan? interval = null)
    {
        _port = port;
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    public void Start(DiscoveryAnnouncement self)
    {
        _self = self;
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.EnableBroadcast = true;
        client.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        _client = client;
        _ = Task.Run(() => ReceiveLoop(client, _cts.Token));
        _ = Task.Run(() => AnnounceLoop(client, _cts.Token));
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    public void AnnounceNow() => _announceSignal.Release();

    private void OnNetworkChanged(object? sender, EventArgs e) => AnnounceNow();

    private async Task AnnounceLoop(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var payload = Encoding.UTF8.GetBytes(Magic + JsonSerializer.Serialize(_self));
                foreach (var addr in BroadcastAddresses())
                {
                    try { await client.SendAsync(payload, new IPEndPoint(addr, _port), ct).ConfigureAwait(false); }
                    catch (SocketException) { /* 그 망은 건너뛴다 */ }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception) { /* 다음 주기에 다시 */ }

            try { await _announceSignal.WaitAsync(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoop(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await client.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            try
            {
                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!text.StartsWith(Magic, StringComparison.Ordinal)) continue;
                var info = JsonSerializer.Deserialize<DiscoveryAnnouncement>(text[Magic.Length..]);
                if (info is null || info.Id == _self?.Id || info.TcpPort is <= 0 or > 65535) continue;
                var endpoint = new IPEndPoint(result.RemoteEndPoint.Address, info.TcpPort);
                PeerSeen?.Invoke(new DiscoveredPeer(info.Id, info.Name, endpoint, DateTime.UtcNow));
            }
            catch (Exception) { /* 깨진 알림은 무시 */ }
        }
    }

    /// <summary>켜져 있는 IPv4 망마다 브로드캐스트 주소(예: 192.168.0.255)와 255.255.255.255.</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var set = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask?.GetAddressBytes();
                if (mask is null || mask.Length != 4) continue;
                var b = new byte[4];
                for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                set.Add(new IPAddress(b));
            }
        }
        return set;
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _cts.Cancel();
        _client?.Dispose();
    }
}
