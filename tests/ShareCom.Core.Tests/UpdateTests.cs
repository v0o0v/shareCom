using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShareCom.Core.Update;

namespace ShareCom.Core.Tests;

public class UpdateTests : IDisposable
{
    private readonly ECDsa _primary = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _spare = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<UpdateKey> _keys;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sharecom-update-" + Guid.NewGuid().ToString("N"));

    public UpdateTests()
    {
        _keys =
        [
            new UpdateKey("primary", Convert.ToBase64String(_primary.ExportSubjectPublicKeyInfo())),
            new UpdateKey("spare", Convert.ToBase64String(_spare.ExportSubjectPublicKeyInfo())),
        ];
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _primary.Dispose();
        _spare.Dispose();
        _stranger.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly byte[] ExeBytes = Encoding.UTF8.GetBytes("가짜 실행 파일 내용 0.6.0");

    private static UpdatePayload Payload(string version = "0.6.0", byte[]? content = null)
    {
        var bytes = content ?? ExeBytes;
        return new UpdatePayload(UpdateManifest.Product, version, "v" + version, "ShareCom.exe",
            Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
    }

    // ───────────── 명세·서명 ─────────────

    [Fact]
    public void 서명한_명세는_그대로_확인된다()
    {
        var json = UpdateManifest.Sign(Payload(), _primary, "primary");
        var p = UpdateManifest.Verify(json, _keys);
        Assert.Equal(new Version(0, 6, 0, 0), p.ParsedVersion);
        Assert.Equal("ShareCom.exe", p.Asset);
    }

    [Fact]
    public void 예비_키로_서명한_명세도_믿는다()
    {
        var json = UpdateManifest.Sign(Payload(), _spare, "spare");
        Assert.Equal("v0.6.0", UpdateManifest.Verify(json, _keys).Tag);
    }

    [Fact]
    public void 모르는_키로_서명하면_거부한다()
    {
        var json = UpdateManifest.Sign(Payload(), _stranger, "primary");
        var ex = Assert.Throws<UpdateException>(() => UpdateManifest.Verify(json, _keys));
        Assert.Contains("서명이 맞지 않습니다", ex.Message);
        Assert.Throws<UpdateException>(() => UpdateManifest.Verify(UpdateManifest.Sign(Payload(), _stranger, "other"), _keys));
    }

    [Fact]
    public void 내용을_바꾸면_거부한다()
    {
        var json = UpdateManifest.Sign(Payload(), _primary, "primary");
        using var doc = JsonDocument.Parse(json);
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(doc.RootElement.GetProperty("payload").GetString()!));
        var forged = payload.Replace(Payload().Sha256, new string('A', 64));
        var tampered = json.Replace(doc.RootElement.GetProperty("payload").GetString()!, Convert.ToBase64String(Encoding.UTF8.GetBytes(forged)));
        Assert.Throws<UpdateException>(() => UpdateManifest.Verify(tampered, _keys));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"payload\":\"!!\",\"keyId\":\"primary\",\"signature\":\"AA==\"}")]
    public void 형식이_잘못된_명세는_거부한다(string json) =>
        Assert.Throws<UpdateException>(() => UpdateManifest.Verify(json, _keys));

    [Fact]
    public void 잘못된_내용은_서명하지_않는다()
    {
        Assert.Throws<UpdateException>(() => UpdateManifest.Sign(Payload() with { Tag = "v0.7.0" }, _primary, "primary"));
        Assert.Throws<UpdateException>(() => UpdateManifest.Sign(Payload() with { Asset = "..\\evil.exe" }, _primary, "primary"));
        Assert.Throws<UpdateException>(() => UpdateManifest.Sign(Payload() with { Product = "Other" }, _primary, "primary"));
        Assert.Throws<UpdateException>(() => UpdateManifest.Sign(Payload() with { Size = 0 }, _primary, "primary"));
    }

    [Fact]
    public void 버전을_읽고_비교한다()
    {
        Assert.Equal(new Version(0, 6, 0, 0), UpdateVersion.Parse("v0.6.0"));
        Assert.Equal(new Version(1, 0, 0, 0), UpdateVersion.Parse("1.0"));
        Assert.Equal("v0.6.0", UpdateVersion.TagOf(new Version(0, 6, 0, 0)));
        Assert.Equal("v0.6.0.1", UpdateVersion.TagOf(new Version(0, 6, 0, 1)));
        Assert.False(UpdateVersion.TryParse("latest", out _));
        Assert.True(UpdateVersion.Parse("0.10.0") > UpdateVersion.Parse("0.9.9"));
    }

    [Fact]
    public void 받은_파일의_크기와_해시를_확인한다()
    {
        var path = Path.Combine(_dir, "a.exe");
        File.WriteAllBytes(path, ExeBytes);
        UpdateManifest.VerifyFile(path, Payload());
        File.WriteAllBytes(path, [.. ExeBytes.Take(ExeBytes.Length - 1), (byte)'X']);
        Assert.Throws<UpdateException>(() => UpdateManifest.VerifyFile(path, Payload()));
    }

    // ───────────── 적용 조건 ─────────────

    [Fact]
    public void 더_높은_버전만_받고_건너뛴_버전은_자동으로_받지_않는다()
    {
        var cur = new Version(0, 5, 2);
        Assert.True(UpdatePolicy.ShouldOffer(new Version(0, 6, 0), cur, null, manual: false));
        Assert.False(UpdatePolicy.ShouldOffer(new Version(0, 5, 2), cur, null, manual: false));
        Assert.False(UpdatePolicy.ShouldOffer(new Version(0, 5, 1), cur, null, manual: true));
        Assert.False(UpdatePolicy.ShouldOffer(new Version(0, 6, 0), cur, "0.6.0", manual: false));
        Assert.True(UpdatePolicy.ShouldOffer(new Version(0, 6, 0), cur, "0.6.0", manual: true));
        Assert.True(UpdatePolicy.ShouldOffer(new Version(0, 6, 1), cur, "0.6.0", manual: false));
    }

    [Fact]
    public void 쉬고_있을_때만_자동으로_적용한다()
    {
        var idle = UpdatePolicy.IdleRequired + TimeSpan.FromSeconds(1);
        var ok = new UpdateConditions(false, false, false, idle, idle);
        Assert.Null(UpdatePolicy.BlockReason(ok, manual: false));
        Assert.NotNull(UpdatePolicy.BlockReason(ok with { Controlling = true }, manual: false));
        Assert.NotNull(UpdatePolicy.BlockReason(ok with { Controlled = true }, manual: false));
        Assert.NotNull(UpdatePolicy.BlockReason(ok with { Transferring = true }, manual: false));
        Assert.NotNull(UpdatePolicy.BlockReason(ok with { SinceLastInput = TimeSpan.FromMinutes(4) }, manual: false));
        Assert.NotNull(UpdatePolicy.BlockReason(ok with { SinceControlEnded = TimeSpan.FromMinutes(1) }, manual: false));
        // 직접 누른 업데이트는 쓰는 중이어도 적용하지만 파일 전송 중에는 기다린다.
        Assert.Null(UpdatePolicy.BlockReason(ok with { SinceLastInput = TimeSpan.Zero }, manual: true));
        Assert.NotNull(UpdatePolicy.BlockReason(ok with { Transferring = true }, manual: true));
    }

    // ───────────── GitHub 확인·받기 ─────────────

    private sealed class FakeHttp : HttpMessageHandler
    {
        public readonly Dictionary<string, (HttpStatusCode Code, byte[] Body)> Routes = new();
        public readonly List<string> Requested = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            Assert.Equal(UpdateChecker.UserAgent, request.Headers.UserAgent.ToString());
            var (code, body) = Routes.TryGetValue(url, out var r) ? r : (HttpStatusCode.NotFound, []);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent(body) });
        }
    }

    private const string Latest = "https://api.example.test/latest";
    private const string ManifestUrl = "https://dl.example.test/ShareCom.update.json";
    private const string ExeUrl = "https://dl.example.test/ShareCom.exe";

    private (UpdateChecker Checker, FakeHttp Http) Release(string tag, string? manifest, long? exeSize = null, bool prerelease = false)
    {
        var http = new FakeHttp();
        var assets = new List<object> { new { name = "ShareCom.exe", browser_download_url = ExeUrl, size = exeSize ?? ExeBytes.Length } };
        if (manifest is not null)
        {
            assets.Add(new { name = UpdateManifest.FileName, browser_download_url = ManifestUrl, size = Encoding.UTF8.GetByteCount(manifest) });
            http.Routes[ManifestUrl] = (HttpStatusCode.OK, Encoding.UTF8.GetBytes(manifest));
        }
        var release = new
        {
            tag_name = tag,
            html_url = "https://github.com/v0o0v/shareCom/releases/tag/" + tag,
            draft = false,
            prerelease,
            assets,
        };
        http.Routes[Latest] = (HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(release));
        http.Routes[ExeUrl] = (HttpStatusCode.OK, ExeBytes);
        return (new UpdateChecker(UpdateChecker.CreateHttpClient(http), _keys, new Uri(Latest)), http);
    }

    [Fact]
    public async Task 더_높은_릴리즈를_찾아_받는다()
    {
        var (checker, _) = Release("v0.6.0", UpdateManifest.Sign(Payload(), _primary, "primary"));
        var c = await checker.CheckAsync(new Version(0, 5, 2), CancellationToken.None);
        Assert.NotNull(c);
        Assert.Equal(new Version(0, 6, 0, 0), c.Version);
        Assert.Equal("https://github.com/v0o0v/shareCom/releases/tag/v0.6.0", c.PageUrl.ToString());

        var path = Path.Combine(_dir, "dl", "ShareCom-0.6.0.exe");
        await checker.DownloadAsync(c, path, CancellationToken.None);
        Assert.Equal(ExeBytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task 같거나_낮은_버전이면_명세를_받지도_않는다()
    {
        var (checker, http) = Release("v0.5.2", UpdateManifest.Sign(Payload("0.5.2"), _primary, "primary"));
        Assert.Null(await checker.CheckAsync(new Version(0, 5, 2), CancellationToken.None));
        Assert.DoesNotContain(ManifestUrl, http.Requested);
    }

    [Fact]
    public async Task 미리보기_릴리즈는_무시한다()
    {
        var (checker, _) = Release("v0.6.0", UpdateManifest.Sign(Payload(), _primary, "primary"), prerelease: true);
        Assert.Null(await checker.CheckAsync(new Version(0, 5, 2), CancellationToken.None));
    }

    [Fact]
    public async Task 명세가_없거나_다른_버전의_명세면_거부한다()
    {
        var (noManifest, _) = Release("v0.6.0", null);
        await Assert.ThrowsAsync<UpdateException>(() => noManifest.CheckAsync(new Version(0, 5, 2), CancellationToken.None));

        // 옛 릴리즈(0.5.9)의 서명된 명세를 새 태그에 붙여도 받지 않는다.
        var (replay, _) = Release("v0.6.0", UpdateManifest.Sign(Payload("0.5.9"), _primary, "primary"));
        await Assert.ThrowsAsync<UpdateException>(() => replay.CheckAsync(new Version(0, 5, 2), CancellationToken.None));

        var (unsigned, _) = Release("v0.6.0", UpdateManifest.Sign(Payload(), _stranger, "primary"));
        await Assert.ThrowsAsync<UpdateException>(() => unsigned.CheckAsync(new Version(0, 5, 2), CancellationToken.None));

        var (sizeMismatch, _) = Release("v0.6.0", UpdateManifest.Sign(Payload(), _primary, "primary"), exeSize: 999);
        await Assert.ThrowsAsync<UpdateException>(() => sizeMismatch.CheckAsync(new Version(0, 5, 2), CancellationToken.None));
    }

    [Fact]
    public async Task 받은_파일이_명세와_다르면_남기지_않는다()
    {
        var (checker, http) = Release("v0.6.0", UpdateManifest.Sign(Payload(), _primary, "primary"));
        var c = await checker.CheckAsync(new Version(0, 5, 2), CancellationToken.None);
        http.Routes[ExeUrl] = (HttpStatusCode.OK, [.. ExeBytes.Take(ExeBytes.Length - 1), (byte)'X']);
        var path = Path.Combine(_dir, "bad.exe");
        await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(c!, path, CancellationToken.None));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task 릴리즈가_없으면_업데이트도_없다()
    {
        var http = new FakeHttp();
        var checker = new UpdateChecker(UpdateChecker.CreateHttpClient(http), _keys, new Uri(Latest));
        Assert.Null(await checker.CheckAsync(new Version(0, 5, 2), CancellationToken.None));
    }

    // ───────────── 교체·되돌리기 ─────────────

    [Fact]
    public void 교체하고_되돌리고_정리한다()
    {
        var exe = Path.Combine(_dir, "ShareCom.exe");
        File.WriteAllText(exe, "old");
        var downloaded = Path.Combine(_dir, "dl.exe");
        File.WriteAllBytes(downloaded, ExeBytes);

        UpdateInstaller.Swap(exe, downloaded, Payload());
        Assert.Equal(ExeBytes, File.ReadAllBytes(exe));
        Assert.Equal("old", File.ReadAllText(UpdateInstaller.OldPath(exe)));
        Assert.False(File.Exists(UpdateInstaller.NewPath(exe)));

        UpdateInstaller.Rollback(exe);
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(UpdateInstaller.OldPath(exe)));

        UpdateInstaller.Swap(exe, downloaded, Payload());
        Assert.True(UpdateInstaller.Cleanup(exe));
        Assert.False(File.Exists(UpdateInstaller.OldPath(exe)));
        Assert.Equal(ExeBytes, File.ReadAllBytes(exe));
    }

    [Fact]
    public void 받은_파일이_명세와_다르면_교체하지_않는다()
    {
        var exe = Path.Combine(_dir, "ShareCom.exe");
        File.WriteAllText(exe, "old");
        var downloaded = Path.Combine(_dir, "dl.exe");
        File.WriteAllText(downloaded, "변조된 파일");

        Assert.Throws<UpdateException>(() => UpdateInstaller.Swap(exe, downloaded, Payload()));
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(UpdateInstaller.OldPath(exe)));
        Assert.False(File.Exists(UpdateInstaller.NewPath(exe)));
    }

    [Fact]
    public void 실행_중인_exe_도_이름을_바꿔_교체한다()
    {
        // 실행 중인 exe 처럼 지우기만 허용(FILE_SHARE_DELETE)하고 열어 둔다.
        var exe = Path.Combine(_dir, "ShareCom.exe");
        File.WriteAllText(exe, "old");
        var downloaded = Path.Combine(_dir, "dl.exe");
        File.WriteAllBytes(downloaded, ExeBytes);
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            UpdateInstaller.Swap(exe, downloaded, Payload());
            Assert.Equal(ExeBytes, File.ReadAllBytes(exe));
        }
        Assert.True(UpdateInstaller.Cleanup(exe));
    }
}
