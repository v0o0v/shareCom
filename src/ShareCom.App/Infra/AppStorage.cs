using System.Security.Cryptography;
using System.Text.Json;
using ShareCom.Core.Identity;

namespace ShareCom.App.Infra;

internal sealed class AppSettings
{
    public Guid DeviceId { get; set; }
    public bool EncryptionEnabled { get; set; } = true;
    public bool EdgeLocked { get; set; }
    public bool ClipboardEnabled { get; set; } = true;
    public List<PeerRecord> Peers { get; set; } = new();
}

/// <summary>
/// %APPDATA%\ShareCom\ 에 설정(settings.json)과 신원(identity.bin)을 둔다.
/// 신원의 개인 키는 DPAPI(현재 사용자)로 암호화해 저장한다.
/// </summary>
internal sealed class AppStorage
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    public string Directory { get; }
    public AppSettings Settings { get; }

    public AppStorage(string? directory = null)
    {
        Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShareCom");
        System.IO.Directory.CreateDirectory(Directory);
        Settings = LoadSettings();
        if (Settings.DeviceId == Guid.Empty)
        {
            Settings.DeviceId = Guid.NewGuid();
            Save();
        }
    }

    private string SettingsPath => Path.Combine(Directory, "settings.json");
    private string IdentityPath => Path.Combine(Directory, "identity.bin");

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("설정 파일을 읽지 못해 새로 만듭니다", ex);
            try { File.Copy(SettingsPath, SettingsPath + ".broken", overwrite: true); } catch { }
        }
        return new AppSettings();
    }

    public void Save()
    {
        lock (_gate)
        {
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Settings, Json));
            File.Move(temp, SettingsPath, overwrite: true);
        }
    }

    public DeviceIdentity LoadOrCreateIdentity()
    {
        var name = Environment.MachineName;
        if (File.Exists(IdentityPath))
        {
            try
            {
                var pfx = ProtectedData.Unprotect(File.ReadAllBytes(IdentityPath), null, DataProtectionScope.CurrentUser);
                return DeviceIdentity.Import(Settings.DeviceId, name, pfx);
            }
            catch (Exception ex)
            {
                // 신원이 바뀌면 다른 PC들과 다시 짝을 맺어야 한다.
                Log.Error("신원 파일을 읽지 못해 새로 만듭니다. 다른 PC들과 다시 짝을 맺어야 합니다", ex);
            }
        }
        var identity = DeviceIdentity.Create(Settings.DeviceId, name);
        File.WriteAllBytes(IdentityPath, ProtectedData.Protect(identity.ExportPfx(), null, DataProtectionScope.CurrentUser));
        return identity;
    }
}

/// <summary>짝 맺은 PC 기록이 바뀔 때마다 settings.json 에 저장한다.</summary>
internal sealed class JsonPeerStore : MemoryPeerStore
{
    private readonly AppStorage _storage;

    public JsonPeerStore(AppStorage storage) : base(storage.Settings.Peers) => _storage = storage;

    protected override void OnChanged()
    {
        _storage.Settings.Peers = GetAll().ToList();
        try { _storage.Save(); } catch (Exception ex) { Log.Error("설정 저장 실패", ex); }
        base.OnChanged();
    }
}
