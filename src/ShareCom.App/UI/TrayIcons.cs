using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ShareCom.App.UI;

/// <summary>트레이 아이콘: 모니터 두 대 모양. 상태에 따라 색이 바뀐다.</summary>
internal static class TrayIcons
{
    /// <summary>내 PC 사용 중(연결 없음): 회색</summary>
    public static readonly Icon Idle = Make(Color.FromArgb(120, 120, 120), Color.FromArgb(120, 120, 120));
    /// <summary>내 PC 사용 중, 다른 PC와 연결됨: 초록</summary>
    public static readonly Icon Connected = Make(Color.FromArgb(46, 160, 67), Color.FromArgb(46, 160, 67));
    /// <summary>다른 PC를 조작 중: 오른쪽 화면이 파랑</summary>
    public static readonly Icon Controlling = Make(Color.FromArgb(120, 120, 120), Color.FromArgb(31, 111, 235));

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon Make(Color left, Color right)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var lb = new SolidBrush(left);
            using var rb = new SolidBrush(right);
            g.FillRectangle(lb, 1, 7, 14, 11);
            g.FillRectangle(rb, 17, 7, 14, 11);
            g.FillRectangle(lb, 5, 20, 6, 3);
            g.FillRectangle(rb, 21, 20, 6, 3);
            using var arrow = new Pen(Color.White, 2.5f) { EndCap = LineCap.ArrowAnchor };
            g.DrawLine(arrow, 6, 12, 26, 12);
        }
        var handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }
}
