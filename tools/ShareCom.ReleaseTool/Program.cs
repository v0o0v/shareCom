using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShareCom.Core.Update;

// ShareCom 업데이트 서명 도구. 콘솔 출력은 코드 페이지와 무관하게 읽히도록 ASCII 만 쓴다.
//   keygen                         primary·spare 키를 만든다(이미 있으면 거부)
//   pubkeys                        공개 키를 UpdateKeys.cs 형식으로 출력
//   sign --exe F --version V --out F [--key primary|spare]
//   verify --manifest F --exe F    앱에 들어 있는 공개 키로 확인
//   export-backup --out F          암호를 걸어 두 키를 내보낸다(사용자가 직접 실행)
//   import-backup --in F           백업에서 키를 되살린다(사용자가 직접 실행)
//   forget-spare                   백업한 뒤 이 PC의 spare 개인 키를 지운다
//   check --current V              실제 GitHub 최신 릴리즈를 앱처럼 확인하고 받아 본다

namespace ShareCom.ReleaseTool;

internal static class Program
{
    private static readonly string[] KeyIds = ["primary", "spare"];

    private static string KeyDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShareCom-release");
    private static string PrivatePath(string id) => Path.Combine(KeyDir, id + ".key");
    private static string PublicPath(string id) => Path.Combine(KeyDir, id + ".pub");

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) return Usage();
            var opts = Options(args.Skip(1).ToArray());
            return args[0] switch
            {
                "keygen" => KeyGen(),
                "pubkeys" => PubKeys(),
                "sign" => Sign(Need(opts, "exe"), Need(opts, "version"), Need(opts, "out"), opts.GetValueOrDefault("key", "primary")),
                "verify" => Verify(Need(opts, "manifest"), Need(opts, "exe")),
                "export-backup" => ExportBackup(Need(opts, "out")),
                "import-backup" => ImportBackup(Need(opts, "in")),
                "forget-spare" => ForgetSpare(),
                "check" => Check(Need(opts, "current")),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: keygen | pubkeys | sign --exe F --version V --out F [--key primary|spare] | verify --manifest F --exe F | export-backup --out F | import-backup --in F | forget-spare | check --current V");
        return 2;
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException("bad option: " + args[i]);
            d[args[i][2..]] = args[i + 1];
        }
        return d;
    }

    private static string Need(Dictionary<string, string> o, string name) =>
        o.TryGetValue(name, out var v) ? v : throw new ArgumentException("missing --" + name);

    // ───────────── 키 보관(DPAPI, 현재 사용자) ─────────────

    private static void SavePrivate(string id, ECDsa key)
    {
        Directory.CreateDirectory(KeyDir);
        var pkcs8 = key.ExportPkcs8PrivateKey();
        try
        {
            File.WriteAllBytes(PrivatePath(id), ProtectedData.Protect(pkcs8, Entropy(id), DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
        File.WriteAllText(PublicPath(id), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    private static ECDsa LoadPrivate(string id)
    {
        if (!File.Exists(PrivatePath(id)))
            throw new FileNotFoundException($"no private key '{id}' on this PC ({PrivatePath(id)}). Use import-backup.");
        var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(PrivatePath(id)), Entropy(id), DataProtectionScope.CurrentUser);
        try
        {
            var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return key;
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    private static byte[] Entropy(string id) => Encoding.UTF8.GetBytes("ShareCom-release-" + id);

    private static int KeyGen()
    {
        foreach (var id in KeyIds)
            if (File.Exists(PrivatePath(id)) || File.Exists(PublicPath(id)))
                throw new InvalidOperationException($"key '{id}' already exists in {KeyDir}. Not overwriting.");
        foreach (var id in KeyIds)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            SavePrivate(id, key);
        }
        Console.WriteLine("[OK] keys created in " + KeyDir);
        return PubKeys();
    }

    private static int PubKeys()
    {
        Console.WriteLine("    public static readonly IReadOnlyList<UpdateKey> All =");
        Console.WriteLine("    [");
        foreach (var id in KeyIds)
        {
            if (!File.Exists(PublicPath(id))) throw new FileNotFoundException("no public key " + PublicPath(id));
            Console.WriteLine($"        new(\"{id}\", \"{File.ReadAllText(PublicPath(id)).Trim()}\"),");
        }
        Console.WriteLine("    ];");
        return 0;
    }

    // ───────────── 서명·검증 ─────────────

    private static int Sign(string exe, string version, string output, string keyId)
    {
        var v = UpdateVersion.Parse(version);
        var info = new FileInfo(exe);
        if (!info.Exists) throw new FileNotFoundException("no exe " + exe);
        string sha;
        using (var fs = File.OpenRead(exe)) sha = Convert.ToHexString(SHA256.HashData(fs));
        var payload = new UpdatePayload(UpdateManifest.Product, UpdateVersion.Display(v), UpdateVersion.TagOf(v), info.Name, sha, info.Length);

        using var key = LoadPrivate(keyId);
        // 서명한 키가 앱에 들어 있는 공개 키와 같은지 먼저 본다(다르면 아무도 받지 못한다).
        var embedded = UpdateKeys.All.FirstOrDefault(k => k.Id == keyId)
            ?? throw new InvalidOperationException($"key '{keyId}' is not in UpdateKeys.cs");
        if (embedded.PublicKey != Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()))
            throw new InvalidOperationException($"key '{keyId}' on this PC does not match UpdateKeys.cs");

        var json = UpdateManifest.Sign(payload, key, keyId);
        UpdateManifest.Verify(json, UpdateKeys.All);
        File.WriteAllText(output, json, new UTF8Encoding(false));
        Console.WriteLine($"[OK] signed {payload.Tag} {payload.Asset} {payload.Size} bytes sha256={sha} key={keyId}");
        return 0;
    }

    private static int Verify(string manifest, string exe)
    {
        var payload = UpdateManifest.Verify(File.ReadAllText(manifest), UpdateKeys.All);
        UpdateManifest.VerifyFile(exe, payload);
        if (!Path.GetFileName(exe).Equals(payload.Asset, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"file name {Path.GetFileName(exe)} != {payload.Asset}");
        Console.WriteLine($"[OK] verified {payload.Tag} {payload.Asset}");
        return 0;
    }

    /// <summary>실제 GitHub 최신 릴리즈를 앱과 같은 방식으로 확인하고 받아 본다(릴리즈 뒤 점검용).</summary>
    private static int Check(string current)
    {
        using var http = UpdateChecker.CreateHttpClient();
        var checker = new UpdateChecker(http, UpdateKeys.All);
        var c = checker.CheckAsync(UpdateVersion.Parse(current), CancellationToken.None).GetAwaiter().GetResult();
        if (c is null)
        {
            Console.WriteLine($"[OK] no update newer than {current}");
            return 0;
        }
        var path = Path.Combine(Path.GetTempPath(), $"sharecom-check-{Guid.NewGuid():N}.exe");
        try
        {
            checker.DownloadAsync(c, path, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"[OK] update {c.Payload.Tag} found, signature and download verified ({c.Payload.Size} bytes)");
        }
        finally { File.Delete(path); }
        return 0;
    }

    // ───────────── 백업 ─────────────

    private sealed record BackupKey(string Id, string PublicKey, string EncryptedPkcs8);
    private sealed record Backup(string Format, string Created, List<BackupKey> Keys);
    private const string BackupFormat = "ShareCom-release-backup-v1";
    private static readonly PbeParameters Pbe = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    private static int ExportBackup(string output)
    {
        if (File.Exists(output)) throw new InvalidOperationException("file already exists: " + output);
        var password = ReadPassword("Backup password (12+ chars): ");
        if (password.Length < 12) throw new ArgumentException("password too short");
        if (ReadPassword("Repeat password: ") != password) throw new ArgumentException("passwords differ");

        var keys = new List<BackupKey>();
        foreach (var id in KeyIds)
        {
            using var key = LoadPrivate(id);
            keys.Add(new BackupKey(id, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(key.ExportEncryptedPkcs8PrivateKey(password, Pbe))));
        }
        var json = JsonSerializer.Serialize(new Backup(BackupFormat, DateTime.UtcNow.ToString("O"), keys), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json, new UTF8Encoding(false));

        // 방금 쓴 파일을 다시 읽어 같은 암호로 풀리는지 확인한다.
        foreach (var k in ReadBackup(output, password)) k.Key.Dispose();
        Console.WriteLine("[OK] backup written: " + output);
        Console.WriteLine("     Keep it on a USB drive or in a password manager, then run: forget-spare");
        return 0;
    }

    private static int ImportBackup(string input)
    {
        var password = ReadPassword("Backup password: ");
        foreach (var (id, key) in ReadBackup(input, password))
        {
            using (key)
            {
                if (File.Exists(PrivatePath(id))) { Console.WriteLine($"[SKIP] '{id}' already on this PC"); continue; }
                SavePrivate(id, key);
                Console.WriteLine($"[OK] restored '{id}'");
            }
        }
        return 0;
    }

    private static List<(string Id, ECDsa Key)> ReadBackup(string path, string password)
    {
        var b = JsonSerializer.Deserialize<Backup>(File.ReadAllText(path)) ?? throw new InvalidDataException("empty backup");
        if (b.Format != BackupFormat) throw new InvalidDataException("not a ShareCom release backup");
        var result = new List<(string, ECDsa)>();
        foreach (var k in b.Keys)
        {
            var key = ECDsa.Create();
            try { key.ImportEncryptedPkcs8PrivateKey(password, Convert.FromBase64String(k.EncryptedPkcs8), out _); }
            catch (CryptographicException) { key.Dispose(); throw new InvalidDataException("wrong password or damaged backup"); }
            if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != k.PublicKey)
            {
                key.Dispose();
                throw new InvalidDataException($"backup key '{k.Id}' does not match its public key");
            }
            result.Add((k.Id, key));
        }
        return result;
    }

    private static int ForgetSpare()
    {
        if (!File.Exists(PrivatePath("spare"))) { Console.WriteLine("[OK] spare private key is not on this PC"); return 0; }
        Console.Write("Delete the spare private key from this PC? Make sure the backup works. Type DELETE: ");
        if (Console.ReadLine()?.Trim() != "DELETE") { Console.WriteLine("[SKIP] kept"); return 1; }
        File.Delete(PrivatePath("spare"));
        Console.WriteLine("[OK] spare private key deleted (public key kept)");
        return 0;
    }

    private static string ReadPassword(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
