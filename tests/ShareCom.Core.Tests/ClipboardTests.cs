using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ShareCom.Core.Clipboard;
using ShareCom.Core.Net;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Tests;

public class ClipboardTests
{
    private sealed class FakeTransport : IClipboardTransport
    {
        public List<Guid> Peers { get; } = new();
        public List<(Guid Peer, byte[] Payload)> Sent { get; } = new();
        public List<ClipboardContent> Written { get; } = new();
        public IReadOnlyList<Guid> ConnectedPeers => Peers;
        public void SendToPeer(Guid peer, byte[] payload) => Sent.Add((peer, payload));
        public void WriteLocal(Guid fromPeer, ClipboardContent content) => Written.Add(content);
    }

    private static ClipboardContent Text(string s) =>
        new([new ClipboardItem(ClipboardFormats.Text, Encoding.UTF8.GetBytes(s))]);

    [Fact]
    public void 내용은_인코딩했다_되돌려도_같고_형식_순서와_관계없이_해시가_같다()
    {
        var a = new ClipboardContent([
            new ClipboardItem(ClipboardFormats.Text, Encoding.UTF8.GetBytes("안녕")),
            new ClipboardItem(ClipboardFormats.Html, Encoding.UTF8.GetBytes("<b>안녕</b>")),
        ]);
        var b = new ClipboardContent([a.Items[1], a.Items[0]]);
        Assert.Equal(a.Hash, b.Hash);
        var back = ClipboardContent.Decode(a.Encode());
        Assert.Equal(a.Hash, back.Hash);
        Assert.Equal("안녕", Encoding.UTF8.GetString(back.Get(ClipboardFormats.Text)!.Data));
        Assert.NotEqual(a.Hash, Text("안녕!").Hash);
    }

    [Fact]
    public void 복사하면_연결된_모든_PC로_보낸다()
    {
        var t = new FakeTransport();
        t.Peers.AddRange([Guid.NewGuid(), Guid.NewGuid()]);
        var sync = new ClipboardSync(t);
        sync.OnLocalChanged(Text("hello"));
        Assert.Equal(2, t.Sent.Count);
        Assert.Equal("hello", Encoding.UTF8.GetString(ClipboardContent.Decode(t.Sent[0].Payload).Items[0].Data));
    }

    [Fact]
    public void 받은_내용은_쓰고_그_내용이_다시_바뀜으로_와도_되돌려_보내지_않는다()
    {
        var t = new FakeTransport();
        var peer = Guid.NewGuid();
        t.Peers.Add(peer);
        var sync = new ClipboardSync(t);
        sync.OnRemote(peer, Text("from B").Encode());
        Assert.Single(t.Written);
        sync.OnLocalChanged(Text("from B"));
        Assert.Empty(t.Sent);
        // 그 뒤 새로 복사한 것은 보낸다
        sync.OnLocalChanged(Text("new"));
        Assert.Single(t.Sent);
    }

    [Fact]
    public void 같은_내용을_두_번_알려도_한_번만_보낸다()
    {
        var t = new FakeTransport();
        t.Peers.Add(Guid.NewGuid());
        var sync = new ClipboardSync(t);
        sync.OnLocalChanged(Text("x"));
        sync.OnLocalChanged(Text("x"));
        Assert.Single(t.Sent);
    }

    [Fact]
    public void 꺼져_있으면_보내지도_쓰지도_않는다()
    {
        var t = new FakeTransport();
        t.Peers.Add(Guid.NewGuid());
        var sync = new ClipboardSync(t) { Enabled = false };
        sync.OnLocalChanged(Text("x"));
        sync.OnRemote(t.Peers[0], Text("y").Encode());
        Assert.Empty(t.Sent);
        Assert.Empty(t.Written);
    }

    [Fact]
    public void 조각_나누기와_합치기()
    {
        var data = new byte[BlobAssembler.ChunkSize * 3 + 17];
        new Random(1).NextBytes(data);
        var chunks = BlobAssembler.Split(7, BlobKind.Clipboard, data).ToList();
        Assert.Equal(4, chunks.Count);
        var asm = new BlobAssembler();
        for (int i = 0; i < chunks.Count - 1; i++) Assert.Null(asm.Add(chunks[i]));
        var done = asm.Add(chunks[^1]);
        Assert.NotNull(done);
        Assert.Equal(data, done!.Value.Data);

        var empty = BlobAssembler.Split(8, BlobKind.Clipboard, []).ToList();
        Assert.Single(empty);
        Assert.Empty(asm.Add(empty[0])!.Value.Data);
    }

    [Fact]
    public void 잘못된_조각은_거절한다()
    {
        var asm = new BlobAssembler();
        Assert.Throws<InvalidDataException>(() => asm.Add(new BlobChunkMessage(1, BlobKind.Clipboard, 10, 8, new byte[5])));
        Assert.Throws<InvalidDataException>(() => asm.Add(new BlobChunkMessage(2, BlobKind.Clipboard, -1, 0, [])));
    }

    [Fact]
    public async Task 큰_데이터를_보내는_중에도_입력_메시지가_먼저_도착한다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = NodeTests.Make(hub, "A");
        await using var b = NodeTests.Make(hub, "B");
        await NodeTests.PairAndConnect(a, b);

        var order = new ConcurrentQueue<string>();
        byte[]? received = null;
        b.Node.BlobReceived += (_, kind, data) => { received = data; order.Enqueue("blob"); };
        b.Node.MessageReceived += (_, m) => { if (m is MouseMoveMessage) order.Enqueue("move"); };

        var big = new byte[20 * 1024 * 1024]; // 20MB: 한 메시지 한계(16MB)보다 크다
        new Random(2).NextBytes(big);
        Assert.True(a.Node.SendBlob(b.Identity.Id, BlobKind.Clipboard, big));
        Assert.True(a.Node.Send(b.Identity.Id, new MouseMoveMessage(1, 2)));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (received is null && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.NotNull(received);
        Assert.Equal(big, received);
        Assert.Equal(["move", "blob"], order.ToArray());
    }
}
