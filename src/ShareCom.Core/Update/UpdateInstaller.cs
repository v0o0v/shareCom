namespace ShareCom.Core.Update;

/// <summary>
/// 같은 경로에서 exe 를 바꾸고 되돌린다. 실행 중인 exe 는 지울 수 없지만 이름은 바꿀 수 있다.
///   ShareCom.exe(실행 중) → ShareCom.old.exe, 받은 파일 → ShareCom.new.exe → ShareCom.exe
/// </summary>
public static class UpdateInstaller
{
    public static string OldPath(string exe) => Sibling(exe, ".old.exe");
    public static string NewPath(string exe) => Sibling(exe, ".new.exe");

    private static string Sibling(string exe, string suffix) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, Path.GetFileNameWithoutExtension(exe) + suffix);

    /// <summary>
    /// 받은 파일을 exe 옆에 복사해 다시 검증한 뒤 이름을 바꾼다. 실패하면 원래대로 두고 예외를 던진다.
    /// </summary>
    public static void Swap(string exe, string downloaded, UpdatePayload payload)
    {
        var nw = NewPath(exe);
        var old = OldPath(exe);
        File.Copy(downloaded, nw, overwrite: true);
        try
        {
            UpdateManifest.VerifyFile(nw, payload);
            if (File.Exists(old)) File.Delete(old); // 지난 업데이트가 남긴 것
            File.Move(exe, old);
            try { File.Move(nw, exe); }
            catch
            {
                File.Move(old, exe);
                throw;
            }
        }
        catch
        {
            TryDelete(nw);
            throw;
        }
    }

    /// <summary>바꾼 것을 되돌린다. 새 exe 가 아직 꺼지는 중일 수 있어 잠깐 다시 시도한다.</summary>
    public static void Rollback(string exe, TimeSpan? wait = null)
    {
        var old = OldPath(exe);
        if (!File.Exists(old)) throw new FileNotFoundException("이전 실행 파일이 없어 되돌릴 수 없습니다.", old);
        Retry(() =>
        {
            if (File.Exists(exe)) File.Delete(exe);
            File.Move(old, exe);
        }, wait ?? TimeSpan.FromSeconds(10));
    }

    /// <summary>업데이트가 끝난 뒤 남은 old/new 파일을 지운다. 이전 프로세스가 꺼질 때까지 잠깐 다시 시도한다.</summary>
    public static bool Cleanup(string exe, TimeSpan? wait = null)
    {
        try
        {
            Retry(() =>
            {
                if (File.Exists(OldPath(exe))) File.Delete(OldPath(exe));
                if (File.Exists(NewPath(exe))) File.Delete(NewPath(exe));
            }, wait ?? TimeSpan.FromSeconds(15));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Retry(Action action, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && ex is not FileNotFoundException
                                       && DateTime.UtcNow < until)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
