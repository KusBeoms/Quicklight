using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Quicklight.Bench;

/// <summary>
/// The result as one PNG (bench\results\bench-v&lt;version&gt;.png, attached to the GitHub release): drawn with WPF,
/// which the app already uses, so no new dependency and no network. 1600 px wide at 2x.
/// </summary>
static class Image
{
    const double W = 1600, Pad = 48;
    static readonly Brush Bg = Hex("#14161b"), Card = Hex("#1d2027"), Fg = Hex("#f2f3f5"), Sub = Hex("#9aa0ab"), Accent = Hex("#5b8cff"),
        Prev = Hex("#4a4f5a"), Up = Hex("#3ecf8e"), Down = Hex("#ff6b6b"), Grid = Hex("#2a2e37"), Warn = Hex("#ffb547");
    static readonly Typeface Ui = new(new FontFamily("Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface Bold = new(new FontFamily("Malgun Gothic"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    static SolidColorBrush Hex(string h) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(h)!; b.Freeze(); return b; }

    /// <summary>One picture for one part (launcher | qutra): its categories, speed, its own score and its history.</summary>
    public static void Save(string path, RunResult run, RunResult? prev, Suite suite, JsonArray history, string part, string historyTitle)
    {
        Exception? error = null;
        var t = new Thread(() => { try { Draw(path, run, prev, suite, history, part, historyTitle); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException("결과 이미지를 만들지 못했습니다: " + error.Message, error);
    }

    static void Draw(string path, RunResult run, RunResult? prev, Suite suite, JsonArray history, string part, string historyTitle)
    {
        var cats = run.Categories.Where(kv => Report.PartOf(kv.Key, suite) == part).OrderBy(kv => Report.Order(kv.Key, suite)).ToList();
        var speed = run.Speed.Where(s => s.Name.StartsWith(part)).ToList();
        var mine = run.Items.Where(i => Report.PartOf(i.Cat, suite) == part).Select(i => i.Id).ToHashSet();
        var (allDrops, allGains) = Report.Movers(run, prev);
        var drops = allDrops.Where(d => mine.Contains(d.Id)).ToList();
        var gains = allGains.Where(d => mine.Contains(d.Id)).ToList();
        var alerts = run.Alerts.Where(a => !a.StartsWith("숨김") || part == "qutra").ToList();
        double score = run.Parts.GetValueOrDefault(part), scoreCi = run.PartsCi.GetValueOrDefault(part);
        double? prevScore = prev?.Parts.TryGetValue(part, out var ps) == true ? ps : null;
        var points = history.OfType<JsonObject>().Where(e => e["parts"]?[part] is not null).TakeLast(10)
            .Select(e => new JsonObject { ["version"] = e["version"]!.GetValue<string>(), ["overall"] = e["parts"]![part]!.GetValue<double>() }).ToList();
        string title = part == "qutra" ? "Qutra (AI) 평가" : "Quicklight 런처 평가";
        var machine = part == "qutra" ? new[] { "cpu", "gpu", "ram", "model" } : new[] { "cpu", "ram" };

        double barsH = cats.Count * 46 + 70;
        double H = Pad + 200 + barsH + 40 + Math.Max(speed.Count * 30 + 70, 300) + 40 + (drops.Count + gains.Count > 0 ? 70 : 0) + alerts.Count * 30 + 60;

        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(Bg, null, new Rect(0, 0, W, H));
            double y = Pad;

            // Header
            Txt(dc, title, Pad, y, 22, Sub);
            Txt(dc, $"v{run.Version}", Pad, y + 30, 54, Fg, true);
            Txt(dc, $"{run.Date} · suite {run.Suite} · {run.Mode}" + (prev is null ? "" : $" · 비교: v{prev.Version}"), Pad, y + 104, 18, Sub);
            Txt(dc, string.Join("  ·  ", machine.Where(run.Machine.ContainsKey).Select(k => run.Machine[k])), Pad, y + 132, 15, Sub);

            // This part's score, right aligned
            var big = Fmt($"{score:0.0}", 96, Fg, true);
            dc.DrawText(big, new Point(W - Pad - big.Width, y - 6));
            Txt(dc, "종합", W - Pad - big.Width - 70, y + 20, 22, Sub);
            if (prevScore is { } before)
            {
                var d = Report.Delta(score, before, scoreCi, prev!.PartsCi.GetValueOrDefault(part));
                var df = Fmt($"{d}  (이전 {before:0.0})", 22, d.StartsWith('▲') ? Up : d.StartsWith('▼') ? Down : Sub);
                dc.DrawText(df, new Point(W - Pad - df.Width, y + 112));
            }
            if (scoreCi > 0) { var ci = Fmt($"±{scoreCi:0.0}", 18, Sub); dc.DrawText(ci, new Point(W - Pad - ci.Width, y + 142)); }
            y += 200;

            // Category bars: this run (accent) over the previous one (grey), tiers on the right
            dc.DrawRoundedRectangle(Card, null, new Rect(Pad, y, W - 2 * Pad, barsH), 16, 16);
            Txt(dc, "항목별 점수", Pad + 24, y + 18, 20, Fg, true);
            Txt(dc, "■ 이번", W - Pad - 230, y + 22, 15, Accent);
            Txt(dc, "■ 이전", W - Pad - 140, y + 22, 15, Prev);
            double by = y + 64, labelW = 230, barX = Pad + 24 + labelW, barW = 760;
            foreach (var (id, c) in cats)
            {
                Txt(dc, c.Name, Pad + 24, by, 17, Fg);
                dc.DrawRoundedRectangle(Grid, null, new Rect(barX, by + 2, barW, 22), 6, 6);
                var p = prev?.Categories.GetValueOrDefault(id);
                if (p is not null) dc.DrawRoundedRectangle(Prev, null, new Rect(barX, by + 18, barW * p.Score / 100, 6), 3, 3);
                dc.DrawRoundedRectangle(Accent, null, new Rect(barX, by + 2, Math.Max(4, barW * c.Score / 100), 14), 5, 5);
                if (c.Ci > 0)
                {
                    double lo = barW * Math.Max(0, c.Score - c.Ci) / 100, hi = barW * Math.Min(100, c.Score + c.Ci) / 100;
                    dc.DrawLine(new Pen(Fg, 2), new Point(barX + lo, by + 9), new Point(barX + hi, by + 9));
                }
                Txt(dc, $"{c.Score:0.0}", barX + barW + 16, by - 2, 20, Fg, true);
                if (p is not null)
                {
                    var d = Report.Delta(c.Score, p.Score, c.Ci, p.Ci);
                    Txt(dc, d, barX + barW + 84, by, 17, d.StartsWith('▲') ? Up : d.StartsWith('▼') ? Down : Sub);
                }
                string tiers = string.Join("  ", new[] { "easy", "normal", "hard" }.Where(c.Tiers.ContainsKey).Select(t => $"{Report.TierName(t)} {c.Tiers[t]:0}"));
                Txt(dc, tiers + (c.Items > 0 && c.Tiers.Count > 0 ? $"   ({c.Items}개)" : ""), barX + barW + 160, by + 1, 15, Sub);
                by += 46;
            }
            y += barsH + 40;

            // Speed table (left) and history (right)
            double colW = (W - 2 * Pad - 32) / 2, boxH = Math.Max(speed.Count * 30 + 70, 300);
            dc.DrawRoundedRectangle(Card, null, new Rect(Pad, y, colW, boxH), 16, 16);
            Txt(dc, "속도", Pad + 24, y + 18, 20, Fg, true);
            Txt(dc, "p50        p95        목표        점수", Pad + 230, y + 22, 14, Sub);
            double sy = y + 60;
            foreach (var s in speed)
            {
                Txt(dc, Report.SpeedName(s.Name), Pad + 24, sy, 16, Fg);
                Txt(dc, $"{s.P50:0.#} {s.Unit}", Pad + 230, sy, 16, Fg);
                Txt(dc, $"{s.P95:0.#} {s.Unit}", Pad + 330, sy, 16, Fg);
                Txt(dc, (s.HigherIsBetter ? "≥ " : "≤ ") + s.Target.ToString(CultureInfo.InvariantCulture), Pad + 440, sy, 16, Sub);
                Txt(dc, $"{s.Score:0}", Pad + 540, sy, 16, s.Score >= 90 ? Up : s.Score >= 60 ? Fg : Down, true);
                sy += 30;
            }
            double hx = Pad + colW + 32;
            dc.DrawRoundedRectangle(Card, null, new Rect(hx, y, colW, boxH), 16, 16);
            Txt(dc, historyTitle, hx + 24, y + 18, 20, Fg, true);
            History(dc, points, new Rect(hx + 40, y + 70, colW - 80, boxH - 120));
            y += boxH + 40;

            // Biggest movers and alerts
            if (drops.Count + gains.Count > 0)
            {
                Txt(dc, "가장 많이 내린 문항: " + (drops.Count == 0 ? "없음" : string.Join(", ", drops.Select(d => $"{d.Id} {d.D:0}"))), Pad, y, 16, drops.Count > 0 ? Down : Sub);
                Txt(dc, "가장 많이 오른 문항: " + (gains.Count == 0 ? "없음" : string.Join(", ", gains.Select(d => $"{d.Id} +{d.D:0}"))), Pad, y + 28, 16, gains.Count > 0 ? Up : Sub);
                y += 70;
            }
            foreach (var a in alerts) { Txt(dc, "! " + a, Pad, y, 16, Warn); y += 30; }
            Txt(dc, "점수는 규칙으로 판정 (AI 판정 없음). ▲▼ 는 잡음 범위(95%)를 넘는 변화만, ≈ 는 범위 안. 속도는 같은 PC끼리만 비교.", Pad, H - Pad - 10, 14, Sub);
        }

        var bmp = new RenderTargetBitmap((int)(W * 2), (int)(H * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(v);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }

    static void History(DrawingContext dc, List<JsonObject> points, Rect r)
    {
        for (int g = 0; g <= 4; g++)
        {
            double gy = r.Bottom - r.Height * g / 4;
            dc.DrawLine(new Pen(Grid, 1), new Point(r.Left, gy), new Point(r.Right, gy));
            Txt(dc, (g * 25).ToString(), r.Left - 34, gy - 9, 13, Sub);
        }
        if (points.Count == 0) return;
        double X(int i) => points.Count == 1 ? r.Left + r.Width / 2 : r.Left + r.Width * i / (points.Count - 1);
        double Y(double s) => r.Bottom - r.Height * Math.Clamp(s, 0, 100) / 100;
        var pen = new Pen(Accent, 3);
        for (int i = 0; i < points.Count; i++)
        {
            double s = points[i]["overall"]!.GetValue<double>();
            var pt = new Point(X(i), Y(s));
            if (i > 0) dc.DrawLine(pen, new Point(X(i - 1), Y(points[i - 1]["overall"]!.GetValue<double>())), pt);
            dc.DrawEllipse(Accent, null, pt, 5, 5);
            Txt(dc, $"{s:0}", pt.X - 10, pt.Y - 28, 14, Fg);
            var label = Fmt("v" + points[i]["version"]!.GetValue<string>(), 13, Sub);
            dc.DrawText(label, new Point(pt.X - label.Width / 2, r.Bottom + 10));
        }
    }

    static FormattedText Fmt(string s, double size, Brush b, bool bold = false) =>
        new(s, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, bold ? Bold : Ui, size, b, 2.0);

    static void Txt(DrawingContext dc, string s, double x, double y, double size, Brush b, bool bold = false) => dc.DrawText(Fmt(s, size, b, bold), new Point(x, y));
}
