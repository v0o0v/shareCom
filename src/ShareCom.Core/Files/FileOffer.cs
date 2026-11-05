using System.Text;

namespace ShareCom.Core.Files;

/// <summary>제안에 든 파일 또는 폴더 하나. RelativePath 는 '\' 로 나눈 상대 경로다.</summary>
public sealed record FileEntry(string RelativePath, bool IsDirectory, long Size, long LastWriteUtcTicks);

/// <summary>
/// 보내는 쪽이 넘기는 파일 목록(내용은 없음). 받는 쪽은 이 목록의 Index 로 내용을 요청한다.
/// 폴더는 그 안의 폴더·파일이 모두 펼쳐져 들어가고, 폴더 항목이 그 안의 항목보다 앞에 온다.
/// </summary>
public sealed class FileOffer
{
    public Guid Id { get; }
    public IReadOnlyList<FileEntry> Entries { get; }
    public long TotalBytes { get; }
    public int FileCount { get; }

    public FileOffer(Guid id, IReadOnlyList<FileEntry> entries)
    {
        Id = id;
        Entries = entries;
        TotalBytes = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        FileCount = entries.Count(e => !e.IsDirectory);
    }

    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write((byte)1);
        w.Write(Id.ToByteArray());
        w.Write(Entries.Count);
        foreach (var e in Entries)
        {
            w.Write(e.RelativePath);
            w.Write(e.IsDirectory);
            w.Write(e.Size);
            w.Write(e.LastWriteUtcTicks);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>받은 목록을 푼다. 경로가 하나라도 안전하지 않으면 거절한다.</summary>
    public static FileOffer Decode(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        if (r.ReadByte() != 1) throw new InvalidDataException("알 수 없는 파일 목록 형식");
        var id = new Guid(r.ReadBytes(16));
        int n = r.ReadInt32();
        if (n < 0 || n > 1_000_000) throw new InvalidDataException("파일 수가 잘못됨");
        var list = new List<FileEntry>(n);
        for (int i = 0; i < n; i++)
        {
            var path = SafePath.Normalize(r.ReadString());
            bool dir = r.ReadBoolean();
            long size = r.ReadInt64();
            long ticks = r.ReadInt64();
            if (size < 0) throw new InvalidDataException("파일 크기가 잘못됨");
            list.Add(new FileEntry(path, dir, dir ? 0 : size, ticks));
        }
        return new FileOffer(id, list);
    }
}

/// <summary>다른 PC가 보낸 상대 경로를 검사한다. 받는 폴더 밖으로 나가는 경로(.., 절대 경로, 드라이브)를 막는다.</summary>
public static class SafePath
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars().Concat(['\\', '/', ':']).Distinct().ToArray();
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Normalize(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("빈 경로");
        var parts = relative.Replace('/', '\\').Split('\\');
        foreach (var p in parts)
        {
            if (p.Length == 0 || p == "." || p == "..") throw new InvalidDataException($"안전하지 않은 경로: {relative}");
            if (p.IndexOfAny(Invalid) >= 0) throw new InvalidDataException($"경로에 쓸 수 없는 글자: {relative}");
            if (p.EndsWith(' ') || p.EndsWith('.')) throw new InvalidDataException($"경로 끝이 공백이나 점: {relative}");
            var stem = p.Split('.')[0];
            if (Reserved.Contains(stem)) throw new InvalidDataException($"Windows 예약 이름: {relative}");
        }
        return string.Join('\\', parts);
    }
}

/// <summary>보내는 쪽이 들고 있는 제안: 목록과 각 항목의 실제 경로.</summary>
public sealed class LocalOffer
{
    public FileOffer Offer { get; }
    public IReadOnlyList<string> FullPaths { get; }

    private LocalOffer(FileOffer offer, IReadOnlyList<string> fullPaths)
    {
        Offer = offer;
        FullPaths = fullPaths;
    }

    /// <summary>
    /// 고른 파일·폴더로 제안을 만든다. 폴더는 안을 모두 펼친다. 읽을 수 없는 폴더와
    /// 바로 가기 링크 폴더(무한 반복 위험)는 건너뛴다.
    /// </summary>
    public static LocalOffer Build(IEnumerable<string> paths)
    {
        var entries = new List<FileEntry>();
        var full = new List<string>();
        var usedTop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw);
            var name = Path.GetFileName(path.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = path.Replace(":", "").Trim('\\'); // 드라이브 통째(C:\)
            if (!usedTop.Add(name)) continue;
            if (Directory.Exists(path)) AddDirectory(path, name, entries, full);
            else if (File.Exists(path)) AddFile(new FileInfo(path), name, entries, full);
        }
        return new LocalOffer(new FileOffer(Guid.NewGuid(), entries), full);
    }

    private static void AddFile(FileInfo f, string relative, List<FileEntry> entries, List<string> full)
    {
        entries.Add(new FileEntry(relative, false, f.Length, f.LastWriteTimeUtc.Ticks));
        full.Add(f.FullName);
    }

    private static void AddDirectory(string path, string relative, List<FileEntry> entries, List<string> full)
    {
        var info = new DirectoryInfo(path);
        entries.Add(new FileEntry(relative, true, 0, info.LastWriteTimeUtc.Ticks));
        full.Add(info.FullName);
        IEnumerable<FileSystemInfo> children;
        try { children = info.EnumerateFileSystemInfos().OrderBy(c => c is FileInfo).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }
        foreach (var c in children)
        {
            var rel = relative + "\\" + c.Name;
            if (c is DirectoryInfo d)
            {
                if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                AddDirectory(d.FullName, rel, entries, full);
            }
            else if (c is FileInfo f)
            {
                AddFile(f, rel, entries, full);
            }
        }
    }
}
