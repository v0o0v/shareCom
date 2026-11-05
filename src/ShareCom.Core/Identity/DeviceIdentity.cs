using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ShareCom.Core.Identity;

/// <summary>이 PC의 바뀌지 않는 신원: 고유 ID, 이름, ECDSA P-256 자체 서명 인증서.</summary>
public sealed class DeviceIdentity : IDisposable
{
    public Guid Id { get; }
    public string Name { get; }
    public X509Certificate2 Certificate { get; }
    public string Thumbprint { get; }

    private DeviceIdentity(Guid id, string name, X509Certificate2 certificate)
    {
        Id = id;
        Name = name;
        Certificate = certificate;
        Thumbprint = CertificateUtil.Thumbprint(certificate);
    }

    public static DeviceIdentity Create(Guid id, string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN=ShareCom {id:D}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], critical: false));
        using var temp = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(50));
        // SChannel 은 메모리에만 있는 키를 못 쓰므로 PFX 로 내보냈다가 다시 읽는다.
        return Import(id, name, temp.Export(X509ContentType.Pfx));
    }

    public static DeviceIdentity Import(Guid id, string name, byte[] pfx)
    {
        var cert = X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
        return new DeviceIdentity(id, name, cert);
    }

    public byte[] ExportPfx() => Certificate.Export(X509ContentType.Pfx);

    /// <summary>평문 모드 신원 증명용 서명.</summary>
    public byte[] Sign(byte[] data)
    {
        using var key = Certificate.GetECDsaPrivateKey() ?? throw new InvalidOperationException("개인 키가 없습니다.");
        return key.SignData(data, HashAlgorithmName.SHA256);
    }

    public void Dispose() => Certificate.Dispose();
}

public static class CertificateUtil
{
    public static string Thumbprint(X509Certificate2 cert) => cert.GetCertHashString(HashAlgorithmName.SHA256);

    public static string Thumbprint(byte[] rawCertificate)
    {
        using var cert = X509CertificateLoader.LoadCertificate(rawCertificate);
        return Thumbprint(cert);
    }

    public static bool Verify(byte[] rawCertificate, byte[] data, byte[] signature)
    {
        using var cert = X509CertificateLoader.LoadCertificate(rawCertificate);
        using var key = cert.GetECDsaPublicKey();
        return key is not null && key.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// 짝 맺기 확인 코드(6자리). 두 인증서 지문을 정렬해 해시하므로 양쪽에서 같은 값이 나온다.
    /// 중간에 다른 기기가 끼어들면 한쪽이 보는 인증서가 달라져 코드가 달라진다.
    /// </summary>
    public static string PairingCode(string thumbprintA, string thumbprintB)
    {
        var ordered = string.CompareOrdinal(thumbprintA, thumbprintB) <= 0
            ? thumbprintA + "|" + thumbprintB
            : thumbprintB + "|" + thumbprintA;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("ShareCom-pair-v1|" + ordered));
        uint value = BitConverter.ToUInt32(hash, 0);
        return (value % 1_000_000).ToString("D6");
    }
}
