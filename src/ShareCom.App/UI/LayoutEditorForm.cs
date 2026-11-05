using System.Drawing.Drawing2D;
using ShareCom.Core.Layout;
using ShareCom.Core.Protocol;

namespace ShareCom.App.UI;

/// <summary>
/// 화면 배치 편집기. PC마다 상자(안에 모니터들)를 그리고, 끌어서 놓으면 가장 가까운 PC 가장자리에 붙인다.
/// [적용]을 누르면 위치를 모든 PC에 퍼뜨린다.
/// </summary>
internal sealed class LayoutEditorForm : Form
{
    private static readonly Color SelfColor = Color.FromArgb(52, 120, 220);
    private static readonly Color ConnectedColor = Color.FromArgb(46, 160, 100);
    private static readonly Color OfflineColor = Color.FromArgb(140, 140, 140);

    private readonly Guid _self;
    private readonly Func<Guid, bool> _isConnected;
    private readonly Action<IReadOnlyDictionary<Guid, (int X, int Y)>> _apply;
    private readonly Dictionary<Guid, (int X, int Y)> _pos = new();
    private readonly CanvasControl _canvas;
    private readonly Label _notice;
    private LayoutDoc _doc;
    private bool _dirty;

    // 화면 ↔ 평면 변환(끄는 동안에는 바꾸지 않는다)
    private double _scale = 0.2, _minX, _minY, _offX, _offY;

    private Guid? _drag;
    private (double X, double Y) _grab;

    public LayoutEditorForm(LayoutDoc doc, Guid self, Func<Guid, bool> isConnected, Action<IReadOnlyDictionary<Guid, (int X, int Y)>> apply)
    {
        _self = self;
        _isConnected = isConnected;
        _apply = apply;
        _doc = doc;

        Text = "ShareCom 화면 배치";
        Font = UiFonts.Normal;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(960, 640);
        MinimumSize = new Size(640, 420);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        var help = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = UiFonts.Normal.Height * 2 + 22,
            Padding = new Padding(12, 10, 12, 0),
            Text = "PC를 끌어서 실제 책상 위 배치처럼 놓으세요. 맞닿은 가장자리로 마우스가 넘어갑니다.\n" +
                   "상자 안 숫자는 Ctrl+Alt+숫자 단축키 번호입니다. 크기는 배율을 반영한 실제 크기 비율입니다.",
        };
        _canvas = new CanvasControl { Dock = DockStyle.Fill, BackColor = Color.FromArgb(246, 247, 249) };
        _canvas.Paint += (_, e) => Draw(e.Graphics);
        _canvas.MouseDown += OnMouseDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseUp += OnMouseUp;
        _canvas.Resize += (_, _) => { if (_drag is null) Fit(); _canvas.Invalidate(); };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12, 10, 12, 10) };
        var ok = new Button { Text = "적용", Width = 100, Height = 34, Dock = DockStyle.Right };
        var cancel = new Button { Text = "취소", Width = 100, Height = 34, Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
        _notice = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(160, 90, 0) };
        ok.Click += (_, _) => { _apply(new Dictionary<Guid, (int X, int Y)>(_pos)); Close(); };
        cancel.Click += (_, _) => Close();
        bottom.Controls.Add(_notice);
        bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        bottom.Controls.Add(ok);
        bottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        bottom.Controls.Add(cancel);
        CancelButton = cancel;

        Controls.Add(_canvas);
        Controls.Add(bottom);
        Controls.Add(help);
        Load(doc);
    }

    /// <summary>편집하는 동안 다른 PC에서 배치가 바뀌었다.</summary>
    public void ShowRemoteChange(LayoutDoc doc)
    {
        if (!_dirty)
        {
            Load(doc);
            return;
        }
        // 이미 옮긴 자리는 두고, 새로 들어오거나 빠진 PC만 반영한다.
        _doc = doc;
        foreach (var e in doc.Entries) _pos.TryAdd(e.Id, (e.X, e.Y));
        foreach (var gone in _pos.Keys.Where(id => doc.Find(id) is null).ToList()) _pos.Remove(gone);
        _notice.Text = "다른 PC에서 배치가 바뀌었습니다. [적용]을 누르면 지금 보이는 배치로 덮어씁니다.";
        if (_drag is null) Fit();
        _canvas.Invalidate();
    }

    private new void Load(LayoutDoc doc)
    {
        _doc = doc;
        _pos.Clear();
        foreach (var e in doc.Entries) _pos[e.Id] = (e.X, e.Y);
        Fit();
        _canvas.Invalidate();
    }

    private IEnumerable<LayoutEntry> Entries() =>
        _doc.Entries.Where(e => _pos.ContainsKey(e.Id)).Select(e => e with { X = _pos[e.Id].X, Y = _pos[e.Id].Y });

    private void Fit()
    {
        var boxes = Entries().Select(DeskLayout.BoxOf).ToList();
        if (boxes.Count == 0 || _canvas.Width < 50 || _canvas.Height < 50) return;
        _minX = boxes.Min(b => b.X);
        _minY = boxes.Min(b => b.Y);
        double w = boxes.Max(b => b.Right) - _minX, h = boxes.Max(b => b.Bottom) - _minY;
        const int margin = 60;
        _scale = Math.Min(0.3, Math.Min((_canvas.Width - 2 * margin) / Math.Max(1, w), (_canvas.Height - 2 * margin) / Math.Max(1, h)));
        _offX = (_canvas.Width - w * _scale) / 2;
        _offY = (_canvas.Height - h * _scale) / 2;
    }

    private RectangleF ToScreen(double x, double y, double w, double h) =>
        new((float)((x - _minX) * _scale + _offX), (float)((y - _minY) * _scale + _offY), (float)(w * _scale), (float)(h * _scale));

    private (double X, double Y) ToPlane(Point p) => ((p.X - _offX) / _scale + _minX, (p.Y - _offY) / _scale + _minY);

    private void Draw(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var entries = Entries().ToList();
        var order = DeskLayout.JumpOrder(entries, _self);
        // 끄는 PC를 맨 위에 그린다.
        foreach (var e in entries.OrderBy(e => e.Id == _drag))
        {
            var color = e.Id == _self ? SelfColor : _isConnected(e.Id) ? ConnectedColor : OfflineColor;
            var box = DeskLayout.BoxOf(e);
            var r = ToScreen(box.X, box.Y, box.Width, box.Height);
            using (var fill = new SolidBrush(Color.FromArgb(28, color))) g.FillRectangle(fill, r);
            foreach (var m in e.Screen.Monitors)
            {
                var gm = DeskLayout.GlobalMonitor(e, m);
                var mr = ToScreen(gm.X, gm.Y, gm.Width, gm.Height);
                mr.Inflate(-2, -2);
                using var mf = new SolidBrush(Color.FromArgb(e.Id == _drag ? 150 : 110, color));
                g.FillRectangle(mf, mr);
                using var mp = new Pen(Color.FromArgb(220, color), 1);
                g.DrawRectangle(mp, mr.X, mr.Y, mr.Width, mr.Height);
            }
            using (var pen = new Pen(color, e.Id == _drag ? 3 : 2)) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);

            int number = e.Id == _self ? 1 : order.ToList().IndexOf(e.Id) + 2;
            string state = e.Id == _self ? "내 PC" : _isConnected(e.Id) ? "연결됨" : "연결 안 됨";
            string label = $"{number}   {e.Name}\n{state}";
            using var textBrush = new SolidBrush(Color.FromArgb(30, 30, 30));
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(label, Font, textBrush, r, sf);
        }
    }

    private Guid? HitTest(Point p)
    {
        var (x, y) = ToPlane(p);
        foreach (var e in Entries().Reverse())
        {
            var b = DeskLayout.BoxOf(e);
            if (x >= b.X && x < b.Right && y >= b.Y && y < b.Bottom) return e.Id;
        }
        return null;
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || HitTest(e.Location) is not { } id) return;
        var (x, y) = ToPlane(e.Location);
        _drag = id;
        _grab = (x - _pos[id].X, y - _pos[id].Y);
        _canvas.Capture = true;
        _canvas.Invalidate();
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_drag is not { } id)
        {
            _canvas.Cursor = HitTest(e.Location) is null ? Cursors.Default : Cursors.SizeAll;
            return;
        }
        var (x, y) = ToPlane(e.Location);
        _pos[id] = ((int)Math.Round(x - _grab.X), (int)Math.Round(y - _grab.Y));
        _canvas.Invalidate();
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (_drag is not { } id) return;
        _drag = null;
        _canvas.Capture = false;
        var entries = Entries().ToList();
        var me = entries.First(x => x.Id == id);
        var box = DeskLayout.BoxOf(me);
        var others = entries.Where(x => x.Id != id).Select(DeskLayout.BoxOf).ToList();
        _pos[id] = DeskLayout.Snap(others, box.Width, box.Height, box.X, box.Y);
        _dirty = true;
        Fit();
        _canvas.Invalidate();
    }

    private sealed class CanvasControl : Control
    {
        public CanvasControl()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }
}
