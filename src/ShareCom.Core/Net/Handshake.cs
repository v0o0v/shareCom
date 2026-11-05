using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ShareCom.Core.Identity;
using ShareCom.Core.Protocol;

namespace ShareCom.Core.Net;

public enum ConnectionMode : byte
{
    /// <summary>TLS 암호화, 상대 인증서 지문 고정.</summary>
    Tls = (byte)'T',
    /// <summary>평문. 연결할 때만 인증서 키 서명으로 상대를 확인한다.</summary>
    Plain = (byte)'P',
    /// <summary>짝 맺기. 항상 TLS 이고 상대 인증서를 받아 확인 코드를 만든다.</summary>
    Pairing = (byte)'R',
}

public sealed class HandshakeException(string message) : Exception(message);

public sealed record HandshakeResult(
    Stream Stream,
    ConnectionMode Mode,
    Guid PeerId,
    string PeerName,
    int PeerListenPort,
    byte[] PeerCertificate);

/// <summary>
/// 연결 머리말: 매직 "SHCM" + 버전(1) + 모드(1). 서버는 실제로 쓸 모드를 같은 꼴로 돌려준다.
/// 둘 중 한쪽이라도 암호화를 원하면 TLS 를 쓴다.
/// </summary>
public static class Handshake
{
    private static readonly byte[] Magic = "SHCM"u8.ToArray();
    private const string TlsHost = "sharecom";

    public static async Task<HandshakeResult> ClientAsync(
        NetworkStream network, DeviceIdentity me, int myListenPort, ConnectionMode requested,
        Func<Guid, PeerRecord?> lookup, Guid? expectedPeer, CancellationToken ct)
    {
        await WriteHeaderAsync(network, requested, ct).ConfigureAwait(false);
        var chosen = await ReadHeaderAsync(network, ct).ConfigureAwait(false);
        if (requested == ConnectionMode.Pairing && chosen != ConnectionMode.Pairing)
            throw new HandshakeException("상대가 짝 맺기를 받지 않았습니다.");
        // 머리말은 보호되지 않으므로 중간에서 바꿀 수 있다. 내가 고른 것보다 약한 방식은 받지 않는다.
        // (평문을 요청했는데 상대가 암호화를 고르는 것만 허용: "한쪽이라도 켜면 암호화")
        if (chosen != requested && !(requested == ConnectionMode.Plain && chosen == ConnectionMode.Tls))
            throw new HandshakeException(requested == ConnectionMode.Tls && chosen == ConnectionMode.Plain
                ? "상대가 암호화 없이 연결하려 합니다. 중간에서 연결을 바꾼 기기가 있을 수 있어 끊습니다."
                : "상대가 요청과 다른 연결 방식을 골랐습니다.");

        Stream stream = network;
        byte[]? tlsPeerCert = null;
        if (chosen is ConnectionMode.Tls or ConnectionMode.Pairing)
        {
            var ssl = new SslStream(network, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = TlsHost,
                ClientCertificates = [me.Certificate],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => me.Certificate,
                RemoteCertificateValidationCallback = (_, _, _, _) => true, // 지문은 아래에서 직접 맞춰 본다
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EnabledSslProtocols = SslProtocols.None,
            }, ct).ConfigureAwait(false);
            tlsPeerCert = ssl.RemoteCertificate?.GetRawCertData() ?? throw new HandshakeException("상대 인증서가 없습니다.");
            stream = ssl;
        }

        var myNonce = RandomNumberGenerator.GetBytes(32);
        await MessageCodec.WriteAsync(stream, new AuthHelloMessage(me.Id, me.Name, MessageCodec.ProtocolVersion, myListenPort, myNonce), ct).ConfigureAwait(false);
        var hello = await ReadAsync<AuthHelloMessage>(stream, ct).ConfigureAwait(false);
        if (hello.Id == me.Id) throw new HandshakeException("자기 자신에게 연결했습니다.");
        if (expectedPeer is { } exp && exp != hello.Id) throw new HandshakeException("기대한 PC가 아닙니다.");

        if (chosen == ConnectionMode.Pairing)
            return new HandshakeResult(stream, chosen, hello.Id, hello.Name, hello.ListenPort, tlsPeerCert!);

        var record = lookup(hello.Id) ?? throw new HandshakeException("짝 맺지 않은 PC입니다.");
        if (chosen == ConnectionMode.Tls)
        {
            if (CertificateUtil.Thumbprint(tlsPeerCert!) != record.Thumbprint)
                throw new HandshakeException("상대 인증서가 짝 맺을 때와 다릅니다.");
        }
        else
        {
            var proof = me.Sign(ProofData("client", hello.Nonce, myNonce, me.Id, hello.Id));
            await MessageCodec.WriteAsync(stream, new AuthProofMessage(proof), ct).ConfigureAwait(false);
            var theirs = await ReadAsync<AuthProofMessage>(stream, ct).ConfigureAwait(false);
            if (!CertificateUtil.Verify(record.Certificate, ProofData("server", myNonce, hello.Nonce, hello.Id, me.Id), theirs.Signature))
                throw new HandshakeException("상대 신원 확인에 실패했습니다.");
        }
        return new HandshakeResult(stream, chosen, hello.Id, hello.Name, hello.ListenPort, record.Certificate);
    }

    public static async Task<HandshakeResult> ServerAsync(
        NetworkStream network, DeviceIdentity me, int myListenPort, bool wantEncryption,
        Func<Guid, PeerRecord?> lookup, CancellationToken ct)
    {
        var requested = await ReadHeaderAsync(network, ct).ConfigureAwait(false);
        var chosen = requested switch
        {
            ConnectionMode.Pairing => ConnectionMode.Pairing,
            ConnectionMode.Tls => ConnectionMode.Tls,
            _ => wantEncryption ? ConnectionMode.Tls : ConnectionMode.Plain,
        };
        await WriteHeaderAsync(network, chosen, ct).ConfigureAwait(false);

        Stream stream = network;
        byte[]? tlsPeerCert = null;
        if (chosen is ConnectionMode.Tls or ConnectionMode.Pairing)
        {
            var ssl = new SslStream(network, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = me.Certificate,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, _, _, _) => true, // 지문은 아래에서 직접 맞춰 본다
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EnabledSslProtocols = SslProtocols.None,
            }, ct).ConfigureAwait(false);
            tlsPeerCert = ssl.RemoteCertificate?.GetRawCertData() ?? throw new HandshakeException("상대 인증서가 없습니다.");
            stream = ssl;
        }

        var hello = await ReadAsync<AuthHelloMessage>(stream, ct).ConfigureAwait(false);
        if (hello.Id == me.Id) throw new HandshakeException("자기 자신에게서 온 연결입니다.");
        PeerRecord? record = null;
        if (chosen != ConnectionMode.Pairing)
        {
            // 모르는 PC에는 우리 신원도 알려 주지 않고 끊는다.
            record = lookup(hello.Id) ?? throw new HandshakeException("짝 맺지 않은 PC입니다.");
            if (chosen == ConnectionMode.Tls && CertificateUtil.Thumbprint(tlsPeerCert!) != record.Thumbprint)
                throw new HandshakeException("상대 인증서가 짝 맺을 때와 다릅니다.");
        }

        var myNonce = RandomNumberGenerator.GetBytes(32);
        await MessageCodec.WriteAsync(stream, new AuthHelloMessage(me.Id, me.Name, MessageCodec.ProtocolVersion, myListenPort, myNonce), ct).ConfigureAwait(false);

        if (chosen == ConnectionMode.Pairing)
            return new HandshakeResult(stream, chosen, hello.Id, hello.Name, hello.ListenPort, tlsPeerCert!);

        if (chosen == ConnectionMode.Plain)
        {
            var theirs = await ReadAsync<AuthProofMessage>(stream, ct).ConfigureAwait(false);
            if (!CertificateUtil.Verify(record!.Certificate, ProofData("client", myNonce, hello.Nonce, hello.Id, me.Id), theirs.Signature))
                throw new HandshakeException("상대 신원 확인에 실패했습니다.");
            var proof = me.Sign(ProofData("server", hello.Nonce, myNonce, me.Id, hello.Id));
            await MessageCodec.WriteAsync(stream, new AuthProofMessage(proof), ct).ConfigureAwait(false);
        }
        return new HandshakeResult(stream, chosen, hello.Id, hello.Name, hello.ListenPort, record!.Certificate);
    }

    /// <summary>
    /// 서명할 내용: 역할 + 상대가 준 nonce + 내 nonce + 내 ID + 상대 ID.
    /// 상대가 준 nonce 가 들어가므로 예전 서명을 다시 써먹을 수 없다.
    /// </summary>
    private static byte[] ProofData(string role, byte[] theirNonce, byte[] myNonce, Guid signer, Guid other)
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes("ShareCom-auth-v1|" + role + "|"));
        ms.Write(theirNonce);
        ms.Write(myNonce);
        ms.Write(signer.ToByteArray());
        ms.Write(other.ToByteArray());
        return ms.ToArray();
    }

    private static async Task WriteHeaderAsync(Stream s, ConnectionMode mode, CancellationToken ct)
    {
        var header = new byte[6];
        Magic.CopyTo(header, 0);
        header[4] = (byte)MessageCodec.ProtocolVersion;
        header[5] = (byte)mode;
        await s.WriteAsync(header, ct).ConfigureAwait(false);
    }

    private static async Task<ConnectionMode> ReadHeaderAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[6];
        await s.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic)) throw new HandshakeException("ShareCom 연결이 아닙니다.");
        if (header[4] != MessageCodec.ProtocolVersion) throw new HandshakeException($"버전이 다릅니다(상대 {header[4]}, 나 {MessageCodec.ProtocolVersion}). 두 PC의 ShareCom 을 같은 버전으로 맞춰 주세요.");
        var mode = (ConnectionMode)header[5];
        if (mode is not (ConnectionMode.Tls or ConnectionMode.Plain or ConnectionMode.Pairing))
            throw new HandshakeException("알 수 없는 연결 모드입니다.");
        return mode;
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct) where T : Message
    {
        var m = await MessageCodec.ReadAsync(stream, ct).ConfigureAwait(false)
            ?? throw new HandshakeException("상대가 연결을 끊었습니다.");
        return m as T ?? throw new HandshakeException($"예상하지 못한 메시지: {m.Type}");
    }
}
