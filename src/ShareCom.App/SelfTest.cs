using System.Drawing.Imaging;
using System.Text;
using ShareCom.App.Clipboard;
using ShareCom.Core.Clipboard;

namespace ShareCom.App;

/// <summary>
/// 진단 명령. <c>ShareCom.exe --selftest-clipboard 결과파일</c>
/// 클립보드에 시험 내용을 썼다가 다시 읽어 형식마다 같은지 본다. 끝나면 원래 클립보드 내용을 되돌린다.
/// </summary>
internal static class SelfTest
{
    /// <summary>
    /// <c>ShareCom.exe --selftest-layout 그림.png</c>: 가짜 PC 3대 배치로 배치 편집기를 띄워 그림으로 저장한다.
    /// </summary>
    public static int RenderLayoutEditor(string outputPath)
    {
        Guid me = Guid.NewGuid(), a = Guid.NewGuid(), b = Guid.NewGuid();
        var doc = new ShareCom.Core.Protocol.LayoutDoc(3, me,
        [
            new(me, "책상-본체", 0, 0, new([new(0, 0, 2560, 1440), new(2560, 180, 1920, 1080)], 125)),
            new(a, "노트북", -1536, 200, new([new(0, 0, 1920, 1200)], 125)),
            new(b, "거실-PC", 400, 1152, new([new(0, 0, 1920, 1080)], 100)),
        ]);
        using var form = new UI.LayoutEditorForm(doc, me, id => id == a, _ => { });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-4000, -4000);
        form.Show();
        Application.DoEvents();
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
        bmp.Save(outputPath, ImageFormat.Png);
        form.Close();
        return 0;
    }

    /// <summary><c>ShareCom.exe --selftest-settings 그림.png</c>: 설정 창을 그림으로 저장한다.</summary>
    public static int RenderSettings(string outputPath)
    {
        var values = new UI.SettingsValues(true, true, true, false, ShareCom.Core.Input.HotkeySettings.Default);
        var info = new UI.SettingsInfo(Environment.MachineName, "FDD14CBB08F8C2A9…", "0.5.0", Infra.SystemSetup.IsAdministrator());
        using var form = new UI.SettingsForm(values, info, () => { });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-4000, -4000);
        form.Show();
        Application.DoEvents();
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
        bmp.Save(outputPath, ImageFormat.Png);
        form.Close();
        return 0;
    }

    /// <summary>
    /// <c>ShareCom.exe --selftest-autostart 결과파일</c>: 관리자 권한에서 자동 시작을 등록 → 확인 → 해제 → 확인한다.
    /// 원래 등록돼 있었으면 끝난 뒤 다시 등록해 둔다. 결과 파일 첫 줄은 PASS 또는 FAIL.
    /// </summary>
    public static int RunAutoStart(string outputPath)
    {
        var report = new StringBuilder();
        bool ok = true;
        void Check(bool cond, string what) { report.AppendLine($"{(cond ? "[OK]" : "[FAIL]")} {what}"); ok &= cond; }
        var exe = Environment.ProcessPath!;
        Check(Infra.SystemSetup.IsAdministrator(), "관리자 권한");
        bool before = Infra.AutoStart.IsRegistered();
        report.AppendLine($"원래 등록 상태: {before}");
        var err = Infra.AutoStart.Register(exe);
        Check(err is null, $"등록 {err}");
        Check(Infra.AutoStart.IsRegistered(), "등록 뒤 조회됨");
        err = Infra.AutoStart.Unregister();
        Check(err is null, $"해제 {err}");
        Check(!Infra.AutoStart.IsRegistered(), "해제 뒤 조회 안 됨");
        if (before) Infra.AutoStart.Register(exe);
        File.WriteAllText(outputPath, (ok ? "PASS" : "FAIL") + "\n" + report);
        return ok ? 0 : 1;
    }

    public static int RunClipboard(string outputPath)
    {
        var report = new StringBuilder();
        int failures = 0;
        var backup = Backup();
        try
        {
            var png = MakePng();
            var html = "Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:0000000183\r\nStartFragment:0000000141\r\nEndFragment:0000000147\r\n<html><body><!--StartFragment--><b>한글</b><!--EndFragment--></body></html>";
            var sent = new ClipboardContent([
                new ClipboardItem(ClipboardFormats.Text, Encoding.UTF8.GetBytes("ShareCom 시험 글자 😀")),
                new ClipboardItem(ClipboardFormats.Html, Encoding.UTF8.GetBytes(html)),
                new ClipboardItem(ClipboardFormats.Rtf, Encoding.UTF8.GetBytes(@"{\rtf1\ansi {\b bold} text}")),
                new ClipboardItem(ClipboardFormats.Png, png),
            ]);
            ClipboardIo.Write(sent);
            var back = ClipboardIo.Read();
            if (back is null)
            {
                report.AppendLine("FAIL read returned null");
                failures++;
            }
            else
            {
                foreach (var item in sent.Items)
                {
                    var got = back.Get(item.Format);
                    bool ok = got is not null && (item.Format == ClipboardFormats.Png
                        ? SamePixels(item.Data, got.Data)
                        : TrimNull(got.Data).SequenceEqual(item.Data));
                    if (!ok) failures++;
                    report.AppendLine($"{(ok ? "OK  " : "FAIL")} {item.Format} sent={item.Data.Length} got={got?.Data.Length.ToString() ?? "none"}");
                }
                // 다시 읽어도 같은 해시가 나와야 되돌림이 생기지 않는다.
                var again = ClipboardIo.Read();
                bool stable = again?.Hash == back.Hash;
                if (!stable) failures++;
                report.AppendLine($"{(stable ? "OK  " : "FAIL")} stable-hash");
            }
        }
        catch (Exception ex)
        {
            failures++;
            report.AppendLine("FAIL exception " + ex);
        }
        finally
        {
            Restore(backup);
        }
        report.AppendLine(failures == 0 ? "RESULT PASS" : $"RESULT FAIL {failures}");
        File.WriteAllText(outputPath, report.ToString(), Encoding.UTF8);
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// 진단 명령. <c>ShareCom.exe --selftest-files 원본폴더 초</c>
    /// 원본 폴더 안의 항목을 가상 파일로 클립보드에 올리고 정한 시간 동안 기다린다(그동안 탐색기에서 붙여넣어 본다).
    /// 원본 폴더 옆에 ready.txt 를 만들어 준비됐음을 알린다. 끝나면 원래 클립보드 내용을 되돌린다.
    /// </summary>
    public static int ServeFiles(string sourceDir, int seconds)
    {
        ShareCom.App.Infra.Log.Init(Path.GetDirectoryName(Path.GetFullPath(sourceDir))!);
        var backup = Backup();
        var local = ShareCom.Core.Files.LocalOffer.Build(Directory.EnumerateFileSystemEntries(sourceDir));
        var obj = new VirtualFileDataObject(local.Offer, i => new ForwardOnlyStream(File.OpenRead(local.FullPaths[i])), "시험");
        OleClipboard.Set(obj);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourceDir))!, "ready.txt"), local.Offer.Entries.Count.ToString());
        using var timer = new System.Windows.Forms.Timer { Interval = seconds * 1000 };
        timer.Tick += (_, _) => Application.ExitThread();
        timer.Start();
        Application.Run();
        OleClipboard.ReleaseIfOwned();
        Restore(backup);
        ShareCom.App.Infra.Log.Flush();
        return 0;
    }

    /// <summary>원격 스트림처럼 앞으로만 읽히는 스트림(시험용).</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private static byte[] TrimNull(byte[] data)
    {
        int n = data.Length;
        while (n > 0 && data[n - 1] == 0) n--;
        return data[..n];
    }

    private static byte[] MakePng()
    {
        using var bmp = new Bitmap(3, 2, PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, Color.FromArgb(255, 255, 0, 0));
        bmp.SetPixel(1, 0, Color.FromArgb(128, 0, 255, 0));
        bmp.SetPixel(2, 0, Color.FromArgb(0, 0, 0, 255));
        bmp.SetPixel(0, 1, Color.White);
        bmp.SetPixel(1, 1, Color.Black);
        bmp.SetPixel(2, 1, Color.FromArgb(255, 10, 20, 30));
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static bool SamePixels(byte[] a, byte[] b)
    {
        using var ia = new Bitmap(new MemoryStream(a));
        using var ib = new Bitmap(new MemoryStream(b));
        if (ia.Size != ib.Size) return false;
        for (int y = 0; y < ia.Height; y++)
            for (int x = 0; x < ia.Width; x++)
                if (ia.GetPixel(x, y).ToArgb() != ib.GetPixel(x, y).ToArgb()) return false;
        return true;
    }

    private static DataObject? Backup()
    {
        try
        {
            var src = System.Windows.Forms.Clipboard.GetDataObject();
            if (src is null) return null;
            var copy = new DataObject();
            foreach (var f in src.GetFormats(autoConvert: false))
            {
                try
                {
                    if (src.TryGetData(f, out object? value) && value is not null) copy.SetData(f, value);
                }
                catch { /* 옮길 수 없는 형식은 건너뛴다 */ }
            }
            return copy;
        }
        catch { return null; }
    }

    private static void Restore(DataObject? backup)
    {
        try
        {
            if (backup is null) System.Windows.Forms.Clipboard.Clear();
            else System.Windows.Forms.Clipboard.SetDataObject(backup, copy: true, retryTimes: 5, retryDelay: 100);
        }
        catch { }
    }
}
