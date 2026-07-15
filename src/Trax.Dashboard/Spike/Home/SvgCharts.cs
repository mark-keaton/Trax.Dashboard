using System.Globalization;
using System.Text;
using Trax.Dashboard.Models;

namespace Trax.Dashboard.Spike.Home;

/// <summary>
/// PHASE-2 SPIKE — server-side SVG charts, proving the home page needs NO client chart library.
///
/// Renders the three chart shapes the Radzen Index page uses, straight from the existing ChartModels:
///   • grouped columns  (Executions: Completed/Failed/Cancelled over time),
///   • horizontal bars   (Avg Duration, Top Failures),
///   • multi-series line  (Throughput).
///
/// This is the same approach the DAG already uses (layout + SVG computed in C#). Axes, gridlines,
/// and marks follow the dataviz mark specs (thin marks, recessive grid, 4px rounded data-ends,
/// 2px gaps). Colors keep parity with the Radzen page (status semantics: green/red/amber) and are
/// paired with a legend so identity is never color-alone. Theme-aware: ink/grid use CSS vars, so
/// the same SVG works in light and dark with no re-render.
/// </summary>
internal static class SvgCharts
{
    // Status colors — parity with the Radzen Index page. Semantic, legend-labeled.
    public const string Completed = "#2E7D32";
    public const string Failed = "#C62828";
    public const string Cancelled = "#F9A825";

    private const int W = 520, H = 300;
    private const int PadL = 44, PadR = 16, PadT = 16, PadB = 46;
    private static readonly int PlotW = W - PadL - PadR;
    private static readonly int PlotH = H - PadT - PadB;

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Open() =>
        $"<svg viewBox=\"0 0 {W} {H}\" class=\"svgchart\" role=\"img\" preserveAspectRatio=\"xMidYMid meet\">";

    // ---- shared axis + gridlines ---------------------------------------------------------------

    private static void YGrid(StringBuilder sb, double maxVal, int ticks = 4)
    {
        for (int t = 0; t <= ticks; t++)
        {
            double frac = (double)t / ticks;
            double y = PadT + PlotH - frac * PlotH;
            double val = frac * maxVal;
            sb.Append($"<line x1=\"{PadL}\" y1=\"{F(y)}\" x2=\"{PadL + PlotW}\" y2=\"{F(y)}\" class=\"grid\"/>");
            sb.Append($"<text x=\"{PadL - 6}\" y=\"{F(y + 3)}\" class=\"axis-lbl\" text-anchor=\"end\">{FormatVal(val)}</text>");
        }
    }

    private static string FormatVal(double v) =>
        v >= 1000 ? $"{v / 1000:0.#}k" : v.ToString("0", CultureInfo.InvariantCulture);

    private static double NiceMax(double raw)
    {
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double nice = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
        return nice * mag;
    }

    // ---- grouped columns (Executions) ----------------------------------------------------------

    public static string GroupedColumns(IReadOnlyList<ExecutionTimePoint> data)
    {
        if (data.Count == 0 || data.All(d => d.Completed + d.Failed + d.Cancelled == 0))
            return Empty("No executions in this time range");

        double max = NiceMax(data.Max(d => Math.Max(d.Completed, Math.Max(d.Failed, d.Cancelled))));
        var sb = new StringBuilder(Open());
        YGrid(sb, max);

        int n = data.Count;
        double slot = (double)PlotW / n;
        double groupW = slot * 0.7;
        double barW = groupW / 3;

        for (int i = 0; i < n; i++)
        {
            double gx = PadL + i * slot + (slot - groupW) / 2;
            var d = data[i];
            Bar(sb, gx + 0 * barW, d.Completed, max, barW, Completed);
            Bar(sb, gx + 1 * barW, d.Failed, max, barW, Failed);
            Bar(sb, gx + 2 * barW, d.Cancelled, max, barW, Cancelled);

            // sparse category labels (every ~nth to avoid collision)
            if (n <= 8 || i % (n / 8 + 1) == 0)
                sb.Append($"<text x=\"{F(gx + groupW / 2)}\" y=\"{H - PadB + 16}\" class=\"axis-lbl\" text-anchor=\"middle\">{Esc(d.Label)}</text>");
        }
        Axis(sb);
        sb.Append(Legend(("Completed", Completed), ("Failed", Failed), ("Cancelled", Cancelled)));
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void Bar(StringBuilder sb, double x, double value, double max, double w, string fill)
    {
        if (value <= 0) return;
        double h = value / max * PlotH;
        double y = PadT + PlotH - h;
        // 2px gap between adjacent bars, rounded top (data-end)
        sb.Append($"<rect x=\"{F(x + 1)}\" y=\"{F(y)}\" width=\"{F(w - 2)}\" height=\"{F(h)}\" rx=\"3\" fill=\"{fill}\"><title>{F(value)}</title></rect>");
    }

    // ---- horizontal bars (Avg Duration, Top Failures) ------------------------------------------

    public static string HorizontalBars<T>(IReadOnlyList<T> data, Func<T, string> name, Func<T, double> value, string fill, string? emptyMsg = null)
    {
        if (data.Count == 0)
            return Empty(emptyMsg ?? "No data");

        double max = NiceMax(data.Max(value));
        int n = data.Count;
        int labelW = 96;
        int plotX = PadL + labelW;
        int plotW = W - plotX - PadR;
        double rowH = (double)(H - PadT - 16) / n;
        var sb = new StringBuilder(Open());

        for (int i = 0; i < n; i++)
        {
            double y = PadT + i * rowH;
            double v = value(data[i]);
            double w = max > 0 ? v / max * plotW : 0;
            sb.Append($"<text x=\"{PadL - 4 + labelW}\" y=\"{F(y + rowH / 2 + 3)}\" class=\"axis-lbl\" text-anchor=\"end\">{Esc(Trim(name(data[i]), 14))}</text>");
            sb.Append($"<rect x=\"{plotX}\" y=\"{F(y + rowH * 0.15)}\" width=\"{F(Math.Max(w, 1))}\" height=\"{F(rowH * 0.7)}\" rx=\"3\" fill=\"{fill}\"><title>{Esc(name(data[i]))}: {F(v)}</title></rect>");
            sb.Append($"<text x=\"{F(plotX + w + 5)}\" y=\"{F(y + rowH / 2 + 3)}\" class=\"axis-lbl\">{FormatVal(v)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    // ---- multi-series line (Throughput) --------------------------------------------------------

    public static string MultiLine(IReadOnlyList<ThroughputSeries> series)
    {
        if (series.Count == 0 || series.All(s => s.Points.Count == 0))
            return Empty("No completed trains in the last 7 days");

        int len = series.Max(s => s.Points.Count);
        double max = NiceMax(series.SelectMany(s => s.Points).DefaultIfEmpty(new ThroughputPoint()).Max(p => p.Count));
        var sb = new StringBuilder(Open());
        YGrid(sb, max);

        foreach (var s in series)
        {
            if (s.Points.Count == 0) continue;
            var sbPath = new StringBuilder();
            for (int i = 0; i < s.Points.Count; i++)
            {
                double x = PadL + (s.Points.Count == 1 ? PlotW / 2.0 : (double)i / (s.Points.Count - 1) * PlotW);
                double y = PadT + PlotH - (s.Points[i].Count / max * PlotH);
                sbPath.Append(i == 0 ? "M" : "L").Append(F(x)).Append(' ').Append(F(y)).Append(' ');
            }
            var color = string.IsNullOrEmpty(s.Color) ? Completed : s.Color;
            sb.Append($"<path d=\"{sbPath}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
        }

        // x labels from the longest series
        var basis = series.OrderByDescending(s => s.Points.Count).First().Points;
        for (int i = 0; i < basis.Count; i++)
            if (basis.Count <= 8 || i % (basis.Count / 8 + 1) == 0)
            {
                double x = PadL + (basis.Count == 1 ? PlotW / 2.0 : (double)i / (basis.Count - 1) * PlotW);
                sb.Append($"<text x=\"{F(x)}\" y=\"{H - PadB + 16}\" class=\"axis-lbl\" text-anchor=\"middle\">{Esc(basis[i].Label)}</text>");
            }

        Axis(sb);
        sb.Append(Legend(series.Select(s => (s.Name, string.IsNullOrEmpty(s.Color) ? Completed : s.Color)).ToArray()));
        sb.Append("</svg>");
        return sb.ToString();
    }

    // ---- shared bits ---------------------------------------------------------------------------

    private static void Axis(StringBuilder sb)
    {
        sb.Append($"<line x1=\"{PadL}\" y1=\"{PadT}\" x2=\"{PadL}\" y2=\"{PadT + PlotH}\" class=\"axis\"/>");
        sb.Append($"<line x1=\"{PadL}\" y1=\"{PadT + PlotH}\" x2=\"{PadL + PlotW}\" y2=\"{PadT + PlotH}\" class=\"axis\"/>");
    }

    private static string Legend(params (string name, string color)[] items)
    {
        var sb = new StringBuilder($"<g transform=\"translate({PadL},{H - 12})\">");
        double x = 0;
        foreach (var (name, color) in items)
        {
            sb.Append($"<rect x=\"{F(x)}\" y=\"-9\" width=\"10\" height=\"10\" rx=\"2\" fill=\"{color}\"/>");
            sb.Append($"<text x=\"{F(x + 14)}\" y=\"0\" class=\"axis-lbl\">{Esc(name)}</text>");
            x += 16 + name.Length * 6.5 + 12;
        }
        sb.Append("</g>");
        return sb.ToString();
    }

    private static string Empty(string msg) =>
        $"{Open()}<text x=\"{W / 2}\" y=\"{H / 2}\" class=\"axis-lbl\" text-anchor=\"middle\">{Esc(msg)}</text></svg>";

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
    private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
