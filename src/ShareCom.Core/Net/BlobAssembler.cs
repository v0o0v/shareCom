using ShareCom.Core.Protocol;

namespace ShareCom.Core.Net;

/// <summary>큰 데이터를 조각으로 나누고, 받은 조각을 다시 합친다.</summary>
public sealed class BlobAssembler
{
    public const int ChunkSize = 64 * 1024;
    /// <summary>한 번에 합칠 수 있는 최대 크기(.NET 배열 한계).</summary>
    public const long MaxBlobBytes = 2L * 1024 * 1024 * 1024 - 64 * 1024;

    private readonly Dictionary<uint, (BlobKind Kind, byte[] Buffer, long Received)> _pending = new();

    public static IEnumerable<BlobChunkMessage> Split(uint blobId, BlobKind kind, byte[] data)
    {
        if (data.Length == 0)
        {
            yield return new BlobChunkMessage(blobId, kind, 0, 0, []);
            yield break;
        }
        for (long offset = 0; offset < data.Length; offset += ChunkSize)
        {
            int n = (int)Math.Min(ChunkSize, data.Length - offset);
            var part = new byte[n];
            Buffer.BlockCopy(data, (int)offset, part, 0, n);
            yield return new BlobChunkMessage(blobId, kind, data.Length, offset, part);
        }
    }

    /// <summary>조각 하나를 넣는다. 다 모였으면 전체 데이터를 돌려준다.</summary>
    public (BlobKind Kind, byte[] Data)? Add(BlobChunkMessage chunk)
    {
        if (chunk.TotalLength < 0 || chunk.TotalLength > MaxBlobBytes)
            throw new InvalidDataException($"데이터가 너무 큽니다: {chunk.TotalLength}");
        if (chunk.Offset < 0 || chunk.Offset + chunk.Data.Length > chunk.TotalLength)
            throw new InvalidDataException("조각 위치가 잘못됐습니다.");

        if (!_pending.TryGetValue(chunk.BlobId, out var entry))
        {
            entry = (chunk.Kind, new byte[chunk.TotalLength], 0);
        }
        else if (entry.Buffer.Length != chunk.TotalLength || entry.Kind != chunk.Kind)
        {
            throw new InvalidDataException("같은 데이터의 조각끼리 크기가 다릅니다.");
        }

        Buffer.BlockCopy(chunk.Data, 0, entry.Buffer, (int)chunk.Offset, chunk.Data.Length);
        entry.Received += chunk.Data.Length;
        if (entry.Received >= entry.Buffer.Length)
        {
            _pending.Remove(chunk.BlobId);
            return (entry.Kind, entry.Buffer);
        }
        _pending[chunk.BlobId] = entry;
        return null;
    }
}
