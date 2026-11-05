using System.Net;
using System.Net.Sockets;
using ShareCom.Core.Identity;
using ShareCom.Core.Net;

namespace ShareCom.Core.Tests;

/// <summary>머리말(모드 바이트)은 보호되지 않으므로 중간에서 바꿔치기하는 경우를 시험한다.</summary>
public class HandshakeTests
{
    /// <summary>클라이언트 머리말을 읽고, 모드만 바꿔 돌려주는 가짜 상대(중간자 흉내). 클라이언트가 더 보낸 바이트 수를 돌려준다.</summary>
    private static async Task<(int Port, Task<int> Extra)> FakeServer(byte replyMode)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var task = Task.Run(async () =>
        {
            try
            {
                using var c = await listener.AcceptTcpClientAsync();
                var s = c.GetStream();
                var header = new byte[6];
                await s.ReadExactlyAsync(header);
                header[5] = replyMode;
                await s.WriteAsync(header);
                var buf = new byte[256];
                int total = 0, n;
                try { while ((n = await s.ReadAsync(buf)) > 0) total += n; } catch (IOException) { }
                return total;
            }
            finally { listener.Stop(); }
        });
        await Task.Yield();
        return (port, task);
    }

    [Fact]
    public async Task 암호화를_요청했는데_평문_응답이_오면_아무것도_보내지_않고_끊는다()
    {
        var (port, extra) = await FakeServer((byte)'P');
        var me = DeviceIdentity.Create(Guid.NewGuid(), "나");
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var ex = await Assert.ThrowsAsync<HandshakeException>(() =>
                Handshake.ClientAsync(tcp.GetStream(), me, 1, ConnectionMode.Tls, _ => null, null, CancellationToken.None));
            Assert.Contains("암호화 없이", ex.Message);
        }
        Assert.Equal(0, await extra.WaitAsync(TimeSpan.FromSeconds(5))); // 신원 정보(AuthHello)도 보내지 않았다
    }

    [Fact]
    public async Task 일반_연결을_요청했는데_짝_맺기_응답이_오면_끊는다()
    {
        var (port, extra) = await FakeServer((byte)'R');
        var me = DeviceIdentity.Create(Guid.NewGuid(), "나");
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            await Assert.ThrowsAsync<HandshakeException>(() =>
                Handshake.ClientAsync(tcp.GetStream(), me, 1, ConnectionMode.Tls, _ => null, null, CancellationToken.None));
        }
        Assert.Equal(0, await extra.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
