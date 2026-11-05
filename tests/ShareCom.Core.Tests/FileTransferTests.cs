using ShareCom.Core.Files;
using ShareCom.Core.Net;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Tests;

public sealed class NodeFileTransport(Node node) : IFileTransport
{
    public bool Send(Guid peer, Message message) => node.Send(peer, message);
    public bool SendBulk(Guid peer, Message message) => node.SendBulk(peer, message);
    public bool SendBlob(Guid peer, BlobKind kind, byte[] data) => node.SendBlob(peer, kind, data);
}

public class FileTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sharecom-test-" + Guid.NewGuid().ToString("N")[..8]);

    public FileTransferTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static FileTransferService Wire(TestNode n, TimeSpan? idle = null)
    {
        var svc = new FileTransferService(new NodeFileTransport(n.Node)) { ReceiverIdleTimeout = idle ?? TimeSpan.FromSeconds(20) };
        n.Node.MessageReceived += (peer, m) => svc.OnMessage(peer, m);
        n.Node.BlobReceived += (peer, kind, data) => svc.OnBlob(peer, kind, data);
        n.Node.PeerDisconnected += svc.OnPeerDisconnected;
        return svc;
    }

    private string MakeTree()
    {
        var src = Path.Combine(_root, "src", "사진 폴더");
        Directory.CreateDirectory(Path.Combine(src, "하위", "빈 폴더"));
        File.WriteAllText(Path.Combine(src, "메모.txt"), "안녕하세요 ShareCom");
        File.WriteAllBytes(Path.Combine(src, "빈 파일.bin"), []);
        var big = new byte[5 * 1024 * 1024 + 123];
        new Random(3).NextBytes(big);
        File.WriteAllBytes(Path.Combine(src, "하위", "큰 파일.dat"), big);
        return src;
    }

    private static void AssertSameTree(string expected, string actual)
    {
        var e = Directory.EnumerateFileSystemEntries(expected, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(expected, p)).Order().ToList();
        var a = Directory.EnumerateFileSystemEntries(actual, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(actual, p)).Order().ToList();
        Assert.Equal(e, a);
        foreach (var rel in e.Where(r => File.Exists(Path.Combine(expected, r))))
            Assert.Equal(File.ReadAllBytes(Path.Combine(expected, rel)), File.ReadAllBytes(Path.Combine(actual, rel)));
    }

    [Fact]
    public async Task 붙여넣기_방식_제안의_파일을_읽을_때_받아온다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = NodeTests.Make(hub, "A");
        await using var b = NodeTests.Make(hub, "B");
        await NodeTests.PairAndConnect(a, b);
        var fa = Wire(a);
        var fb = Wire(b);

        var src = MakeTree();
        var offer = fa.RegisterOffer([src]);
        // 클립보드로 넘어가는 것처럼 인코딩했다가 푼다
        var received = FileOffer.Decode(offer.Encode());
        Assert.Equal("사진 폴더", received.Entries[0].RelativePath);
        Assert.True(received.Entries[0].IsDirectory);
        Assert.Contains(received.Entries, e => e.RelativePath == @"사진 폴더\하위\빈 폴더" && e.IsDirectory);
        Assert.Equal(3, received.FileCount);

        for (int i = 0; i < received.Entries.Count; i++)
        {
            var e = received.Entries[i];
            if (e.IsDirectory) continue;
            await using var s = fb.OpenRemote(a.Identity.Id, received.Id, i, e.Size);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            Assert.Equal(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(src)!, e.RelativePath)), ms.ToArray());
        }
    }

    [Fact]
    public async Task 큰_파일도_흐름_제어를_지키며_끝까지_받는다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = NodeTests.Make(hub, "A");
        await using var b = NodeTests.Make(hub, "B");
        await NodeTests.PairAndConnect(a, b);
        var fa = Wire(a);
        var fb = Wire(b);

        var path = Path.Combine(_root, "big.bin");
        var data = new byte[40 * 1024 * 1024];
        new Random(4).NextBytes(data);
        File.WriteAllBytes(path, data);
        var offer = fa.RegisterOffer([path]);

        await using var s = fb.OpenRemote(a.Identity.Id, offer.Id, 0, data.Length);
        var buf = new byte[100];
        Assert.Equal(100, await s.ReadAsync(buf));
        await Task.Delay(1500); // 읽기를 멈춘 동안 보내는 쪽은 창(8MB)만큼만 보내고 기다린다
        using var ms = new MemoryStream();
        ms.Write(buf);
        await s.CopyToAsync(ms);
        Assert.Equal(data, ms.ToArray());
    }

    [Fact]
    public async Task 제안에_없는_파일은_보내지_않는다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = NodeTests.Make(hub, "A");
        await using var b = NodeTests.Make(hub, "B");
        await NodeTests.PairAndConnect(a, b);
        Wire(a);
        var fb = Wire(b);
        await using var s = fb.OpenRemote(a.Identity.Id, Guid.NewGuid(), 0, 10);
        var ex = await Assert.ThrowsAsync<IOException>(async () => await s.ReadAsync(new byte[10]));
        Assert.Contains("다시 복사", ex.Message);
    }

    [Fact]
    public async Task 트레이_보내기_받으면_폴더에_저장하고_이름이_겹치면_2를_붙인다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = NodeTests.Make(hub, "A");
        await using var b = NodeTests.Make(hub, "B");
        await NodeTests.PairAndConnect(a, b);
        var fa = Wire(a);
        var fb = Wire(b);

        var src = MakeTree();
        var single = Path.Combine(_root, "src", "보고서.txt");
        File.WriteAllText(single, "내용");
        var dest = Path.Combine(_root, "받은 파일");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "보고서.txt"), "이미 있던 파일");

        Task? receiving = null;
        var lastProgress = new List<FileProgress>();
        fb.SendRequested += (peer, offer) =>
        {
            fb.Respond(peer, offer.Id, true);
            receiving = fb.ReceiveToFolderAsync(peer, offer, dest, new Progress<FileProgress>(p => lastProgress.Add(p)), default);
        };

        var (accepted, sent) = await fa.RequestSendAsync(b.Identity.Id, [src, single]);
        Assert.True(accepted);
        var result = await fa.WaitResultAsync(sent.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Success, result.Error);
        await receiving!;

        AssertSameTree(src, Path.Combine(dest, "사진 폴더"));
        Assert.Equal("이미 있던 파일", File.ReadAllText(Path.Combine(dest, "보고서.txt")));
        Assert.Equal("내용", File.ReadAllText(Path.Combine(dest, "보고서 (2).txt")));
        Assert.Empty(Directory.EnumerateFiles(dest, "*.sharecom-part", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 트레이_보내기를_거절하면_보내는_쪽이_안다()
    {
        var hub = new FakeDiscoveryHub();
        await using var a = NodeTests.Make(hub, "A");
        await using var b = NodeTests.Make(hub, "B");
        await NodeTests.PairAndConnect(a, b);
        var fa = Wire(a);
        var fb = Wire(b);
        fb.SendRequested += (peer, offer) => fb.Respond(peer, offer.Id, false);
        var path = Path.Combine(_root, "x.txt");
        File.WriteAllText(path, "x");
        var (accepted, _) = await fa.RequestSendAsync(b.Identity.Id, [path]).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(accepted);
    }

    [Fact]
    public async Task 받는_중에_원본_PC가_끊기면_읽기가_실패한다()
    {
        var hub = new FakeDiscoveryHub();
        var a = NodeTests.Make(hub, "A");
        await using var b = NodeTests.Make(hub, "B");
        await NodeTests.PairAndConnect(a, b);
        var fa = Wire(a);
        var fb = Wire(b);
        var path = Path.Combine(_root, "big.bin");
        File.WriteAllBytes(path, new byte[30 * 1024 * 1024]);
        var offer = fa.RegisterOffer([path]);

        await using var s = fb.OpenRemote(a.Identity.Id, offer.Id, 0, 30 * 1024 * 1024);
        Assert.True(await s.ReadAsync(new byte[10]) > 0);
        await a.DisposeAsync();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            var buf = new byte[64 * 1024];
            while (await s.ReadAsync(buf) > 0) { }
        });
    }

    [Theory]
    [InlineData(@"..\Windows\system32")]
    [InlineData(@"a\..\..\b")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\rooted")]
    [InlineData(@"a\\b")]
    [InlineData(@"CON")]
    [InlineData(@"nul.txt")]
    [InlineData(@"끝이 점.")]
    [InlineData(@"a:b")]
    public void 안전하지_않은_경로는_거절한다(string path)
    {
        Assert.Throws<InvalidDataException>(() => SafePath.Normalize(path));
    }

    [Fact]
    public void 안전한_경로는_역슬래시로_맞춘다()
    {
        Assert.Equal(@"폴더\하위\파일.txt", SafePath.Normalize("폴더/하위/파일.txt"));
        Assert.Equal("console.log", SafePath.Normalize("console.log"));
    }

    [Fact]
    public void 이미_있는_이름은_번호를_붙인다()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "");
        File.WriteAllText(Path.Combine(_root, "a (2).txt"), "");
        Directory.CreateDirectory(Path.Combine(_root, "폴더"));
        Assert.Equal("a (3).txt", FileTransferService.UniqueName(_root, "a.txt", false));
        Assert.Equal("폴더 (2)", FileTransferService.UniqueName(_root, "폴더", true));
        Assert.Equal("new.txt", FileTransferService.UniqueName(_root, "new.txt", false));
    }
}
