using System.Drawing.Imaging;
using System.Text;
using ShareCom.App.Infra;
using ShareCom.Core.Clipboard;

namespace ShareCom.App.Clipboard;

/// <summary>Windows 클립보드를 읽고 쓴다. UI(STA) 스레드에서만 부른다.</summary>
internal static class ClipboardIo
{
    // 비밀번호 관리자 등이 "감시 프로그램은 가져가지 말라"고 붙이는 표시
    private static readonly string[] PrivacyMarkers =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "Clipboard Viewer Ignore",
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard",
    ];

    /// <summary>지금 클립보드 내용. 공유할 것이 없거나 공유하면 안 되는 내용이면 null.</summary>
    public static ClipboardContent? Read()
    {
        IDataObject? obj;
        try { obj = System.Windows.Forms.Clipboard.GetDataObject(); }
        catch (Exception ex) { Log.Debug($"클립보드 읽기 실패: {ex.Message}"); return null; }
        if (obj is null) return null;

        var formats = obj.GetFormats(autoConvert: false);
        if (formats.Any(f => PrivacyMarkers.Contains(f, StringComparer.OrdinalIgnoreCase)))
        {
            Log.Verbose("클립보드에 공유 제외 표시가 있어 보내지 않습니다(비밀번호 관리자 등).");
            return null;
        }

        var items = new List<ClipboardItem>();
        try
        {
            if (TryGet(obj, DataFormats.UnicodeText, out string? text) && text is not null)
                items.Add(new ClipboardItem(ClipboardFormats.Text, Encoding.UTF8.GetBytes(text)));
            if (TryGet(obj, DataFormats.Html, out string? html) && html is not null)
                items.Add(new ClipboardItem(ClipboardFormats.Html, Encoding.UTF8.GetBytes(html)));
            if (TryGet(obj, DataFormats.Rtf, out string? rtf) && rtf is not null)
                items.Add(new ClipboardItem(ClipboardFormats.Rtf, Encoding.UTF8.GetBytes(rtf)));
            var png = ReadPng(obj, formats);
            if (png is not null) items.Add(new ClipboardItem(ClipboardFormats.Png, png));
        }
        catch (Exception ex)
        {
            Log.Debug($"클립보드 형식 읽기 실패: {ex.Message}");
        }
        return items.Count == 0 ? null : new ClipboardContent(items);
    }

    private static byte[]? ReadPng(IDataObject obj, string[] formats)
    {
        if (formats.Contains("PNG") && TryGet(obj, "PNG", out MemoryStream? stream) && stream is not null)
            return stream.ToArray();
        if (!formats.Contains(DataFormats.Bitmap) && !formats.Contains(DataFormats.Dib)) return null;
        using var image = System.Windows.Forms.Clipboard.GetImage();
        if (image is null) return null;
        using var ms = new MemoryStream();
        image.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static bool TryGet<T>(IDataObject obj, string format, out T? value) where T : class
    {
        value = null;
        if (!obj.GetDataPresent(format, autoConvert: false)) return false;
        if (obj.TryGetData(format, out T? data))
        {
            value = data;
            return true;
        }
        return false;
    }

    /// <summary>받은 내용을 클립보드에 쓴다. 다른 프로그램이 클립보드를 잡고 있으면 몇 번 다시 시도한다.</summary>
    public static void Write(ClipboardContent content)
    {
        var obj = new DataObject();
        if (content.Get(ClipboardFormats.Text) is { } t)
            obj.SetData(DataFormats.UnicodeText, Encoding.UTF8.GetString(t.Data));
        if (content.Get(ClipboardFormats.Html) is { } h)
            obj.SetData(DataFormats.Html, Encoding.UTF8.GetString(h.Data));
        if (content.Get(ClipboardFormats.Rtf) is { } r)
            obj.SetData(DataFormats.Rtf, Encoding.UTF8.GetString(r.Data));
        Bitmap? bitmap = null;
        if (content.Get(ClipboardFormats.Png) is { } p)
        {
            // 투명도를 지키는 PNG 와, PNG 를 모르는 프로그램용 비트맵을 함께 넣는다.
            obj.SetData("PNG", new MemoryStream(p.Data));
            try
            {
                using var ms = new MemoryStream(p.Data);
                using var img = Image.FromStream(ms);
                bitmap = new Bitmap(img);
                obj.SetData(DataFormats.Bitmap, bitmap);
            }
            catch (Exception ex) { Log.Debug($"받은 이미지를 비트맵으로 바꾸지 못함: {ex.Message}"); }
        }
        try
        {
            System.Windows.Forms.Clipboard.SetDataObject(obj, copy: true, retryTimes: 10, retryDelay: 100);
        }
        catch (Exception ex)
        {
            Log.Error("받은 클립보드를 쓰지 못했습니다(다른 프로그램이 클립보드를 잡고 있음)", ex);
        }
        finally
        {
            bitmap?.Dispose();
        }
    }
}
