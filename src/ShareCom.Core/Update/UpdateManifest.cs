using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareCom.Core.Update;

/// <summary>업데이트를 믿을 수 없거나 받을 수 없을 때.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>서명으로 보호하는 업데이트 내용. 릴리즈의 exe 하나를 가리킨다.</summary>
public sealed record UpdatePayload(string Product, string Version, string Tag, string Asset, string Sha256, long Size)
{
    /// <summary>버전 문자열을 비교할 수 있는 값으로.</summary>
    [JsonIgnore]
    public Version ParsedVersion => UpdateVersion.Parse(Version);
}

/// <summary>업데이트 서명을 확인하는 공개 키. PublicKey 는 SubjectPublicKeyInfo(DER)의 Base64.</summary>
public sealed record UpdateKey(string Id, string PublicKey);

/// <summary>
/// 릴리즈에 붙이는 ShareCom.update.json.
/// { "payload": Base64(UTF-8 JSON), "keyId": "primary", "signature": Base64(ECDSA P-256 SHA-256, IEEE P1363) }
/// 서명은 payload 의 바이트 그대로에 대해 한다(JSON 을 다시 쓰면서 바뀔 일이 없게).
/// </summary>
public static class UpdateManifest
{
    public const string FileName = "ShareCom.update.json";
    public const string Product = "ShareCom";
    /// <summary>exe 가 이보다 크면 받지 않는다.</summary>
    public const long MaxExeSize = 512L * 1024 * 1024;
    /// <summary>명세 파일이 이보다 크면 받지 않는다.</summary>
    public const int MaxManifestSize = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private sealed record Envelope(string? Payload, string? KeyId, string? Signature);

    public static string Sign(UpdatePayload payload, ECDsa key, string keyId)
    {
        Validate(payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256);
        return JsonSerializer.Serialize(new Envelope(Convert.ToBase64String(bytes), keyId, Convert.ToBase64String(signature)),
            new JsonSerializerOptions(Json) { WriteIndented = true });
    }

    /// <summary>서명을 확인하고 내용을 돌려준다. 믿을 수 없으면 <see cref="UpdateException"/>.</summary>
    public static UpdatePayload Verify(string json, IReadOnlyList<UpdateKey> keys)
    {
        Envelope? env;
        try { env = JsonSerializer.Deserialize<Envelope>(json, Json); }
        catch (JsonException) { throw new UpdateException("업데이트 명세 형식이 잘못됐습니다."); }
        if (env?.Payload is null || env.KeyId is null || env.Signature is null)
            throw new UpdateException("업데이트 명세에 빠진 항목이 있습니다.");

        var key = keys.FirstOrDefault(k => k.Id == env.KeyId)
            ?? throw new UpdateException($"알 수 없는 서명 키입니다({env.KeyId}).");

        byte[] bytes, signature;
        try
        {
            bytes = Convert.FromBase64String(env.Payload);
            signature = Convert.FromBase64String(env.Signature);
        }
        catch (FormatException) { throw new UpdateException("업데이트 명세 형식이 잘못됐습니다."); }

        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey), out _);
            if (!ecdsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256))
                throw new UpdateException("업데이트 서명이 맞지 않습니다.");
        }

        UpdatePayload? payload;
        try { payload = JsonSerializer.Deserialize<UpdatePayload>(bytes, Json); }
        catch (JsonException) { throw new UpdateException("업데이트 명세 내용이 잘못됐습니다."); }
        if (payload is null) throw new UpdateException("업데이트 명세 내용이 비었습니다.");
        Validate(payload);
        return payload;
    }

    private static void Validate(UpdatePayload p)
    {
        if (p.Product != Product) throw new UpdateException("다른 프로그램의 업데이트입니다.");
        if (!UpdateVersion.TryParse(p.Version, out var v)) throw new UpdateException("업데이트 버전이 잘못됐습니다.");
        if (p.Tag != UpdateVersion.TagOf(v)) throw new UpdateException("업데이트 버전과 태그가 다릅니다.");
        if (string.IsNullOrWhiteSpace(p.Asset) || p.Asset.IndexOfAny(['/', '\\', ':']) >= 0 || p.Asset.Contains("..")
            || !p.Asset.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("업데이트 파일 이름이 잘못됐습니다.");
        if (p.Sha256.Length != 64 || !p.Sha256.All(Uri.IsHexDigit)) throw new UpdateException("업데이트 해시가 잘못됐습니다.");
        if (p.Size <= 0 || p.Size > MaxExeSize) throw new UpdateException("업데이트 파일 크기가 잘못됐습니다.");
    }

    /// <summary>받은 파일이 명세와 같은지(크기, SHA-256).</summary>
    public static void VerifyFile(string path, UpdatePayload payload)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != payload.Size) throw new UpdateException("받은 파일 크기가 명세와 다릅니다.");
        using var fs = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(fs));
        if (!hash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("받은 파일의 해시가 명세와 다릅니다.");
    }
}

/// <summary>"0.6.0" 같은 버전. 빠진 자리는 0 으로 본다.</summary>
public static class UpdateVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        if (!System.Version.TryParse(t, out var v) || v.Major < 0) return false;
        version = Normalize(v);
        return true;
    }

    public static Version Parse(string text) =>
        TryParse(text, out var v) ? v : throw new UpdateException($"버전을 읽지 못했습니다({text}).");

    public static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>릴리즈 태그 이름. 0.6.0 → v0.6.0, 0.6.0.1 → v0.6.0.1.</summary>
    public static string TagOf(Version v)
    {
        v = Normalize(v);
        return v.Revision > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>사람이 읽는 버전(0.6.0).</summary>
    public static string Display(Version v) => TagOf(v)[1..];
}
