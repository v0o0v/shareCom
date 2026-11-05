using System.Runtime.InteropServices;
using ShareCom.Core.Files;

namespace ShareCom.App.UI;

internal static class Sizes
{
    public static string Format(long n) => n switch
    {
        < 1024 => $"{n}B",
        < 1024 * 1024 => $"{n / 1024.0:0.#}KB",
        < 1024L * 1024 * 1024 => $"{n / 1024.0 / 1024.0:0.#}MB",
        _ => $"{n / 1024.0 / 1024.0 / 1024.0:0.##}GB",
    };
}

internal static class KnownFolders
{
    private static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>다운로드\shareCom. 다운로드 폴더 위치를 옮겼어도 따라간다.</summary>
    public static string ShareComDownloads()
    {
        string root;
        if (SHGetKnownFolderPath(Downloads, 0, IntPtr.Zero, out var p) == 0)
        {
            root = Marshal.PtrToStringUni(p)!;
            Marshal.FreeCoTaskMem(p);
        }
        else
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
        return Path.Combine(root, "shareCom");
    }
}

/// <summary>다른 PC가 트레이로 보낸 파일을 받을지 묻는 창. 시간 제한은 없다.</summary>
internal sealed class IncomingFilesForm : Form
{
    public bool Accepted { get; private set; }

    public IncomingFilesForm(string peerName, FileOffer offer, string folder)
    {
        Text = "ShareCom - 파일 받기";
        Font = UiFonts.Normal;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);

        var tops = offer.Entries.Select(e => e.RelativePath.Split('\\')[0]).Distinct().ToList();
        var names = string.Join("\n", tops.Take(8).Select(n => "  · " + n));
        if (tops.Count > 8) names += $"\n  · 그 밖에 {tops.Count - 8}개";

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            Text = $"'{peerName}' PC가 파일을 보내려 합니다.\n파일 {offer.FileCount:N0}개, 모두 {Sizes.Format(offer.TotalBytes)}",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });
        layout.Controls.Add(new Label { Text = names, AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
        layout.Controls.Add(new Label { Text = $"받을 곳: {folder}", AutoSize = true, ForeColor = SystemColors.GrayText });

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var reject = new Button { Text = "거절", AutoSize = true };
        var accept = new Button { Text = "받기", AutoSize = true };
        accept.Click += (_, _) => { Accepted = true; Close(); };
        reject.Click += (_, _) => { Accepted = false; Close(); };
        buttons.Controls.Add(reject);
        buttons.Controls.Add(accept);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = accept;
        CancelButton = reject;
    }
}

/// <summary>트레이로 받은 파일의 진행률 창. [취소]를 누르면 받기를 멈춘다.</summary>
internal sealed class TransferProgressForm : Form
{
    private readonly ProgressBar _bar = new() { Width = 420, Height = 22, Maximum = 1000 };
    private readonly Label _status = new() { AutoSize = true };
    private readonly Label _current = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(420, 0) };
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public TransferProgressForm(string title)
    {
        Text = "ShareCom - " + title;
        Font = UiFonts.Normal;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(16);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(_status);
        layout.Controls.Add(_bar);
        layout.Controls.Add(_current);
        var cancel = new Button { Text = "취소", AutoSize = true, Anchor = AnchorStyles.Right };
        cancel.Click += (_, _) => { _cts.Cancel(); cancel.Enabled = false; };
        layout.Controls.Add(cancel);
        Controls.Add(layout);
        FormClosing += (_, e) =>
        {
            if (!_done && e.CloseReason == CloseReason.UserClosing) _cts.Cancel();
        };
    }

    private bool _done;
    private DateTime _started = DateTime.UtcNow;

    public void Report(FileProgress p)
    {
        if (IsDisposed) return;
        _bar.Value = p.TotalBytes == 0 ? 1000 : (int)Math.Min(1000, p.DoneBytes * 1000 / p.TotalBytes);
        var secs = (DateTime.UtcNow - _started).TotalSeconds;
        var speed = secs > 0.5 ? $" · {Sizes.Format((long)(p.DoneBytes / secs))}/초" : "";
        _status.Text = $"{Sizes.Format(p.DoneBytes)} / {Sizes.Format(p.TotalBytes)} · 파일 {p.DoneFiles}/{p.TotalFiles}{speed}";
        _current.Text = p.Current;
    }

    public void Finish()
    {
        _done = true;
        if (!IsDisposed) Close();
    }
}
