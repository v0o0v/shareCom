using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ShareCom.Core.Update;

/// <summary>받을 수 있는 업데이트. 서명은 이미 확인했다.</summary>
public sealed record UpdateCandidate(UpdatePayload Payload, Uri ExeUrl, Uri PageUrl)
{
    public Version Version => Payload.ParsedVersion;
}

/// <summary>
/// GitHub 의 최신 정식 릴리즈를 보고, 지금보다 높은 버전이면 서명된 명세를 확인해 알려 준다.
/// 미리 보기(pre-release)와 초안은 releases/latest 에 나오지 않지만 혹시 와도 무시한다.
/// </summary>
public sealed class UpdateChecker
{
    public const string Repository = "v0o0v/shareCom";
    public static readonly Uri LatestReleaseUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public const string UserAgent = "ShareCom-Updater";
    private const int MaxReleaseJson = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly IReadOnlyList<UpdateKey> _keys;
    private readonly Uri _latestUrl;

    public UpdateChecker(HttpClient http, IReadOnlyList<UpdateKey> keys, Uri? latestUrl = null)
    {
        _http = http;
        _keys = keys;
        _latestUrl = latestUrl ?? LatestReleaseUrl;
    }

    /// <summary>업데이트 확인·받기에 쓸 HttpClient. 사용자 정보는 보내지 않는다.</summary>
    public static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    private sealed record Asset(string Name, Uri Url, long Size);

    /// <summary>지금보다 높은 정식 릴리즈가 있으면 그 업데이트, 없으면 null. 릴리즈가 잘못됐으면 <see cref="UpdateException"/>.</summary>
    public async Task<UpdateCandidate?> CheckAsync(Version current, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _latestUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // 릴리즈가 아직 없음
        if (!resp.IsSuccessStatusCode) throw new UpdateException($"GitHub 이 응답하지 않습니다({(int)resp.StatusCode}).");
        var body = await ReadLimitedAsync(resp, MaxReleaseJson, ct).ConfigureAwait(false);

        string tag;
        Uri page;
        var assets = new List<Asset>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
            tag = root.GetProperty("tag_name").GetString() ?? "";
            page = HttpsUri(root.GetProperty("html_url").GetString());
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (name is null || url is null) continue;
                assets.Add(new Asset(name, HttpsUri(url), a.GetProperty("size").GetInt64()));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new UpdateException("GitHub 릴리즈 정보를 읽지 못했습니다.");
        }

        if (!UpdateVersion.TryParse(tag, out var latest) || UpdateVersion.TagOf(latest) != tag)
            throw new UpdateException($"릴리즈 태그를 버전으로 읽지 못했습니다({tag}).");
        if (latest <= UpdateVersion.Normalize(current)) return null;

        var manifestAsset = assets.FirstOrDefault(a => a.Name == UpdateManifest.FileName)
            ?? throw new UpdateException($"{tag} 릴리즈에 업데이트 명세({UpdateManifest.FileName})가 없습니다.");
        if (manifestAsset.Size > UpdateManifest.MaxManifestSize) throw new UpdateException("업데이트 명세가 너무 큽니다.");
        string manifestJson;
        using (var mresp = await _http.GetAsync(manifestAsset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (!mresp.IsSuccessStatusCode) throw new UpdateException($"업데이트 명세를 받지 못했습니다({(int)mresp.StatusCode}).");
            manifestJson = System.Text.Encoding.UTF8.GetString(await ReadLimitedAsync(mresp, UpdateManifest.MaxManifestSize, ct).ConfigureAwait(false));
        }

        var payload = UpdateManifest.Verify(manifestJson, _keys);
        if (payload.Tag != tag) throw new UpdateException("업데이트 명세의 버전이 릴리즈와 다릅니다.");
        var exe = assets.FirstOrDefault(a => a.Name == payload.Asset)
            ?? throw new UpdateException($"{tag} 릴리즈에 {payload.Asset} 이 없습니다.");
        if (exe.Size != payload.Size) throw new UpdateException("릴리즈 파일 크기가 명세와 다릅니다.");
        return new UpdateCandidate(payload, exe.Url, page);
    }

    /// <summary>exe 를 path 에 받고 크기·해시를 확인한다. 맞지 않으면 지우고 <see cref="UpdateException"/>.</summary>
    public async Task DownloadAsync(UpdateCandidate candidate, string path, CancellationToken ct)
    {
        var part = path + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            using var resp = await _http.GetAsync(candidate.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new UpdateException($"업데이트 파일을 받지 못했습니다({(int)resp.StatusCode}).");
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > candidate.Payload.Size) throw new UpdateException("업데이트 파일이 명세보다 큽니다.");
                    sha.AppendData(buffer, 0, n);
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                }
                if (total != candidate.Payload.Size) throw new UpdateException("업데이트 파일을 끝까지 받지 못했습니다.");
                if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(candidate.Payload.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("받은 파일의 해시가 명세와 다릅니다.");
            }
            File.Move(part, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static Uri HttpsUri(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException("https 주소가 아닙니다.");
        return uri;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage resp, int limit, CancellationToken ct)
    {
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        int n;
        while ((n = await s.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > limit) throw new UpdateException("받은 응답이 너무 큽니다.");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }
}
