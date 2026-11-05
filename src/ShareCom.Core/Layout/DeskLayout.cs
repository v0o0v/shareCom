using ShareCom.Core.Protocol;

namespace ShareCom.Core.Layout;

public enum PushDirection { Left, Right, Up, Down }

/// <summary>논리 평면 위의 사각형(실수).</summary>
public readonly record struct LogicalRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>밀었을 때 넘어갈 PC와 그 PC 화면(실제 픽셀)에서 커서가 들어갈 자리.</summary>
public sealed record PushTarget(Guid Id, int X, int Y);

/// <summary>
/// 화면 배치 계산. 모든 PC는 하나의 평면 위에 놓이고, 평면의 단위는 논리 픽셀(실제 픽셀 × 100 ÷ 배율)이다.
/// 그래서 4K 150% 모니터와 1080p 100% 모니터가 실제 크기에 가깝게 나란히 놓인다.
/// 한 PC 안의 모니터들은 그 PC의 주 모니터 배율 하나로 함께 줄인다(PC 안의 이동은 Windows 가 처리한다).
/// </summary>
public static class DeskLayout
{
    /// <summary>두 모니터 가장자리가 이만큼(논리 픽셀) 안에 있으면 맞닿았다고 본다.</summary>
    public const double TouchTolerance = 4;
    /// <summary>배치 편집기에서 위·아래(왼·오른) 끝 맞춤으로 끌어당기는 거리.</summary>
    public const int AlignSnap = 24;
    /// <summary>나란히 붙일 때 최소로 겹쳐야 하는 길이(마우스가 넘어갈 구간).</summary>
    public const int MinOverlap = 100;

    private static double Factor(PcScreen s) => 100.0 / s.ScalePercent;

    public static (int Width, int Height) LogicalSize(PcScreen s)
    {
        var u = ScreenRect.Union(s.Monitors);
        return ((int)Math.Round(u.Width * Factor(s)), (int)Math.Round(u.Height * Factor(s)));
    }

    public static (double X, double Y) ToGlobal(LayoutEntry e, double px, double py)
    {
        var u = ScreenRect.Union(e.Screen.Monitors);
        double f = Factor(e.Screen);
        return (e.X + (px - u.X) * f, e.Y + (py - u.Y) * f);
    }

    public static (int X, int Y) ToLocal(LayoutEntry e, double gx, double gy)
    {
        var u = ScreenRect.Union(e.Screen.Monitors);
        double f = Factor(e.Screen);
        return ((int)Math.Floor(u.X + (gx - e.X) / f), (int)Math.Floor(u.Y + (gy - e.Y) / f));
    }

    public static LogicalRect GlobalMonitor(LayoutEntry e, ScreenRect m)
    {
        var (x, y) = ToGlobal(e, m.X, m.Y);
        double f = Factor(e.Screen);
        return new LogicalRect(x, y, m.Width * f, m.Height * f);
    }

    /// <summary>
    /// PC <paramref name="from"/> 의 모니터 <paramref name="monitor"/> 위 점 (px, py) 에서 <paramref name="dir"/> 쪽으로 밀었을 때 넘어갈 곳.
    /// 그쪽 가장자리에 맞닿은 다른 PC 모니터 가운데, 세로(가로) 위치가 가장 가까운 것을 고른다.
    /// 맞닿은 구간 밖에서 밀어도 가장 가까운 구간 끝으로 넘어간다. 들어가는 자리는 가장자리에서
    /// <paramref name="inset"/> 픽셀 안쪽(돌아온 직후 같은 가장자리에서 곧바로 다시 넘어가지 않게).
    /// </summary>
    public static PushTarget? FindTarget(IReadOnlyList<LayoutEntry> entries, Guid from, ScreenRect monitor, int px, int py,
        PushDirection dir, Func<Guid, bool> available, Func<Guid, int> inset)
    {
        var fe = entries.FirstOrDefault(e => e.Id == from);
        if (fe is null) return null;
        var fm = GlobalMonitor(fe, monitor);
        var (gx, gy) = ToGlobal(fe, px + 0.5, py + 0.5);
        bool horizontal = dir is PushDirection.Left or PushDirection.Right;

        LayoutEntry? bestEntry = null;
        ScreenRect bestMonitor = default;
        double bestScore = double.MaxValue, bestAlong = 0;
        foreach (var e in entries)
        {
            if (e.Id == from || !available(e.Id)) continue;
            foreach (var m in e.Screen.Monitors)
            {
                var g = GlobalMonitor(e, m);
                double gap = dir switch
                {
                    PushDirection.Right => g.X - fm.Right,
                    PushDirection.Left => fm.X - g.Right,
                    PushDirection.Down => g.Y - fm.Bottom,
                    _ => fm.Y - g.Bottom,
                };
                if (Math.Abs(gap) > TouchTolerance) continue;
                // 맞닿은 구간(두 모니터가 겹치는 부분)
                double lo = horizontal ? Math.Max(g.Y, fm.Y) : Math.Max(g.X, fm.X);
                double hi = horizontal ? Math.Min(g.Bottom, fm.Bottom) : Math.Min(g.Right, fm.Right);
                if (hi - lo < 1) continue;
                double along = horizontal ? gy : gx;
                double clamped = Math.Clamp(along, lo, hi - 0.5);
                double score = Math.Abs(clamped - along);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestEntry = e;
                    bestMonitor = m;
                    bestAlong = clamped;
                }
            }
        }
        if (bestEntry is null) return null;

        var (lx, ly) = horizontal ? ToLocal(bestEntry, 0, bestAlong) : ToLocal(bestEntry, bestAlong, 0);
        int d = inset(bestEntry.Id);
        var m2 = bestMonitor;
        return dir switch
        {
            PushDirection.Right => new PushTarget(bestEntry.Id, m2.X + d, Math.Clamp(ly, m2.Y, m2.Bottom - 1)),
            PushDirection.Left => new PushTarget(bestEntry.Id, m2.Right - 1 - d, Math.Clamp(ly, m2.Y, m2.Bottom - 1)),
            PushDirection.Down => new PushTarget(bestEntry.Id, Math.Clamp(lx, m2.X, m2.Right - 1), m2.Y + d),
            _ => new PushTarget(bestEntry.Id, Math.Clamp(lx, m2.X, m2.Right - 1), m2.Bottom - 1 - d),
        };
    }

    /// <summary>Ctrl+Alt+숫자 순서: 1 은 내 PC, 그다음은 배치에서 왼쪽부터(같으면 위부터).</summary>
    public static IReadOnlyList<Guid> JumpOrder(IReadOnlyList<LayoutEntry> entries, Guid self) =>
        entries.Where(e => e.Id != self).OrderBy(e => e.X).ThenBy(e => e.Y).ThenBy(e => e.Id).Select(e => e.Id).ToList();

    // ───────────── 배치 편집 ─────────────

    public readonly record struct Box(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool Overlaps(Box o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    }

    public static Box BoxOf(LayoutEntry e)
    {
        var (w, h) = LogicalSize(e.Screen);
        return new Box(e.X, e.Y, w, h);
    }

    /// <summary>
    /// 끌어 놓은 자리(px, py) 근처에서, 다른 PC와 겹치지 않고 어느 한 PC의 가장자리에 맞닿는 가장 가까운 자리.
    /// 맞닿는 길이는 적어도 <see cref="MinOverlap"/>(PC가 그보다 작으면 그 크기)이고, 끝 맞춤 근처면 끝을 맞춘다.
    /// </summary>
    public static (int X, int Y) Snap(IReadOnlyList<Box> others, int width, int height, int px, int py)
    {
        if (others.Count == 0) return (px, py);
        var candidates = new List<(int X, int Y)>();
        foreach (var o in others)
        {
            int ovV = Math.Max(1, Math.Min(MinOverlap, Math.Min(height, o.Height)));
            int y = Align(Math.Clamp(py, o.Y - height + ovV, o.Bottom - ovV), o.Y, o.Bottom - height, o.Y + (o.Height - height) / 2);
            candidates.Add((o.Right, y));
            candidates.Add((o.X - width, y));

            int ovH = Math.Max(1, Math.Min(MinOverlap, Math.Min(width, o.Width)));
            int x = Align(Math.Clamp(px, o.X - width + ovH, o.Right - ovH), o.X, o.Right - width, o.X + (o.Width - width) / 2);
            candidates.Add((x, o.Bottom));
            candidates.Add((x, o.Y - height));
        }

        (int X, int Y)? best = null;
        long bestDist = long.MaxValue;
        foreach (var c in candidates)
        {
            var box = new Box(c.X, c.Y, width, height);
            if (others.Any(o => o.Overlaps(box))) continue;
            long dx = c.X - px, dy = c.Y - py;
            long dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; best = c; }
        }
        if (best is { } b) return b;
        // 둘러싸여 붙일 곳이 없으면 맨 오른쪽 끝에 붙인다.
        var right = others.MaxBy(o => o.Right);
        return (right.Right, right.Y);
    }

    private static int Align(int value, params int[] targets)
    {
        foreach (var t in targets)
            if (Math.Abs(value - t) <= AlignSnap) return t;
        return value;
    }

    /// <summary>
    /// 배치에 처음 들어가는 PC의 자리. 짝 맺을 때 정한 이웃(<paramref name="hint"/>)이 있으면 그 옆에,
    /// 없으면 맨 오른쪽 PC의 오른쪽에 세로 가운데를 맞춰 놓는다.
    /// </summary>
    public static (int X, int Y) AutoPlace(IReadOnlyList<Box> others, int width, int height, (Box Neighbor, NeighborSide NeighborIsOnMy)? hint)
    {
        if (others.Count == 0) return (0, 0);
        (int X, int Y) proposed;
        if (hint is { } h)
        {
            var n = h.Neighbor;
            int y = n.Y + (n.Height - height) / 2;
            proposed = h.NeighborIsOnMy == NeighborSide.Right ? (n.X - width, y) : (n.Right, y);
        }
        else
        {
            var right = others.MaxBy(o => o.Right);
            proposed = (right.Right, right.Y + (right.Height - height) / 2);
        }
        return Snap(others, width, height, proposed.X, proposed.Y);
    }
}
