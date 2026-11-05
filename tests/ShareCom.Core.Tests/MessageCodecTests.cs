using ShareCom.Core.Identity;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Tests;

public class MessageCodecTests
{
    private static T RoundTrip<T>(T message) where T : Message
    {
        var bytes = MessageCodec.Encode(message);
        Assert.Equal(bytes.Length - 4, BitConverter.ToInt32(bytes, 0));
        return Assert.IsType<T>(MessageCodec.Decode(bytes.AsSpan(4)));
    }

    [Fact]
    public void 단순_메시지는_그대로_되돌아온다()
    {
        Message[] all =
        [
            new PairDecisionMessage(true),
            new PingMessage(123456789),
            new PongMessage(-5),
            new LayoutSideMessage(NeighborSide.Left),
            new EnterControlMessage(-1920, 300),
            new LeaveControlMessage(),
            new MouseMoveMessage(5, -7),
            new MouseButtonMessage(MouseButtonKind.X2, true),
            new MouseWheelMessage(-120, true),
            new KeyMessage(0x41, 0x1E, false, true),
            new KeyMessage(0x25, 0x4B, true, false),
            new TakeOverMessage(),
        ];
        foreach (var m in all)
        {
            var bytes = MessageCodec.Encode(m);
            Assert.Equal(m, MessageCodec.Decode(bytes.AsSpan(4)));
        }
    }

    [Fact]
    public void 바이트_배열이_든_메시지도_되돌아온다()
    {
        var id = Guid.NewGuid();
        var hello = RoundTrip(new AuthHelloMessage(id, "작업실 PC", 1, 47821, [1, 2, 3]));
        Assert.Equal(id, hello.Id);
        Assert.Equal("작업실 PC", hello.Name);
        Assert.Equal(47821, hello.ListenPort);
        Assert.Equal(new byte[] { 1, 2, 3 }, hello.Nonce);

        var proof = RoundTrip(new AuthProofMessage([9, 8, 7, 6]));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, proof.Signature);

        var screens = RoundTrip(new ScreenInfoMessage([new(0, 0, 1920, 1080), new(-1280, 100, 1280, 1024)]));
        Assert.Equal([new(0, 0, 1920, 1080), new(-1280, 100, 1280, 1024)], screens.Monitors);
    }

    [Fact]
    public async Task 스트림으로_여러_메시지를_이어서_읽는다()
    {
        var ms = new MemoryStream();
        await MessageCodec.WriteAsync(ms, new MouseMoveMessage(1, 2), default);
        await MessageCodec.WriteAsync(ms, new KeyMessage(1, 2, true, true), default);
        ms.Position = 0;
        Assert.Equal(new MouseMoveMessage(1, 2), await MessageCodec.ReadAsync(ms, default));
        Assert.Equal(new KeyMessage(1, 2, true, true), await MessageCodec.ReadAsync(ms, default));
        Assert.Null(await MessageCodec.ReadAsync(ms, default));
    }

    [Fact]
    public void 짝_맺기_코드는_양쪽에서_같고_6자리다()
    {
        using var a = DeviceIdentity.Create(Guid.NewGuid(), "A");
        using var b = DeviceIdentity.Create(Guid.NewGuid(), "B");
        var ab = CertificateUtil.PairingCode(a.Thumbprint, b.Thumbprint);
        var ba = CertificateUtil.PairingCode(b.Thumbprint, a.Thumbprint);
        Assert.Equal(ab, ba);
        Assert.Matches("^[0-9]{6}$", ab);

        using var c = DeviceIdentity.Create(Guid.NewGuid(), "C");
        Assert.NotEqual(ab, CertificateUtil.PairingCode(a.Thumbprint, c.Thumbprint));
    }

    [Fact]
    public void 신원은_내보냈다_다시_읽어도_같다()
    {
        var id = Guid.NewGuid();
        using var a = DeviceIdentity.Create(id, "A");
        using var again = DeviceIdentity.Import(id, "A", a.ExportPfx());
        Assert.Equal(a.Thumbprint, again.Thumbprint);
        var sig = again.Sign([1, 2, 3]);
        Assert.True(CertificateUtil.Verify(a.Certificate.RawData, [1, 2, 3], sig));
        Assert.False(CertificateUtil.Verify(a.Certificate.RawData, [1, 2, 4], sig));
    }
}
