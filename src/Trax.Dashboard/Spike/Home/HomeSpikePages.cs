using System.Text;
using Trax.Dashboard.Models;

namespace Trax.Dashboard.Spike.Home;

/// <summary>Renders the home dashboard: stat tiles + KPI cards + four server-side SVG charts.</summary>
internal static class HomeSpikePages
{
    public static string Page(string prefix, HomeMetrics m) => $$"""
        <!DOCTYPE html>
        <html lang="en" x-data :data-theme="$store.theme.mode">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>Dashboard — htmx spike</title>
          <script src="https://unpkg.com/htmx.org@2.0.4"></script>
          <script defer src="https://unpkg.com/alpinejs@3.14.8/dist/cdn.min.js"></script>
          <link rel="stylesheet" href="{{prefix}}/assets/spike.css" />
        </head>
        <body>
          <script>
            document.addEventListener('alpine:init', () => {
              Alpine.store('theme', {
                mode: (document.cookie.match(/trax_theme=(\w+)/)?.[1]) || 'light',
                toggle(){ this.mode = this.mode==='dark'?'light':'dark';
                  document.cookie='trax_theme='+this.mode+';path=/;max-age=31536000'; },
              });
            });
          </script>

          <header class="topbar">
            <h1>Dashboard</h1>
            <div class="title-wrap">
              <a class="btn btn--ghost" href="{{prefix}}/dead-letters">Dead Letters →</a>
              <button class="btn btn--ghost" @click="$store.theme.toggle()"
                      x-text="$store.theme.mode === 'dark' ? '☀ Light' : '☾ Dark'"></button>
            </div>
          </header>

          <p class="lede">
            KPIs and charts, all rendered <b>server-side as inline SVG</b> — no client chart library.
            <em>Phase-2 spike — the home page.</em>
          </p>

          <!-- htmx polls this whole region every 10s; server re-renders tiles + charts. -->
          <div id="home-metrics" hx-get="{{prefix}}/home/metrics" hx-trigger="every 10s" hx-swap="innerHTML">
            {{MetricsFragment(m)}}
          </div>
        </body>
        </html>
        """;

    public static string MetricsFragment(HomeMetrics m)
    {
        var sb = new StringBuilder();

        // ── Server health tiles ──
        sb.Append("<div class=\"tilerow\">");
        Tile(sb, "CPU", $"{m.CpuPercent}%", "var(--info,#2b7fc6)");
        Tile(sb, "Working Set", $"{m.MemoryMb} MB", "var(--warn)");
        Tile(sb, "GC Heap", $"{m.GcHeapMb} MB", "var(--accent)");
        Tile(sb, "Uptime", FormatUptime(m.Uptime), "var(--ok)");
        sb.Append("</div>");

        // ── KPI cards ──
        sb.Append("<div class=\"tilerow\">");
        Tile(sb, "Executions Today", m.ExecutionsToday.ToString(), "var(--accent)");
        Tile(sb, "Success Rate", $"{m.SuccessRate}%", "var(--ok)");
        Tile(sb, "Currently Running", m.CurrentlyRunning.ToString(), "var(--info,#2b7fc6)");
        Tile(sb, "Dead Letters", m.UnresolvedDeadLetters.ToString(), "var(--err)");
        sb.Append("</div>");

        // ── Charts (2×2) ──
        sb.Append("<div class=\"chartgrid\">");
        Card(sb, "Executions", SvgCharts.GroupedColumns(m.ExecutionsOverTime));
        Card(sb, "Avg Execution Duration (7d)",
            SvgCharts.HorizontalBars(m.AvgDurations, d => d.Name, d => d.AvgMs, SvgCharts.Completed,
                "No completed trains in the last 7 days"));
        Card(sb, "Top Failing Trains (7d)",
            SvgCharts.HorizontalBars(m.TopFailures, f => f.Name, f => f.Count, SvgCharts.Failed,
                "No failures in the last 7 days"));
        Card(sb, "Throughput (7d)", SvgCharts.MultiLine(m.Throughput));
        sb.Append("</div>");

        return sb.ToString();
    }

    private static void Tile(StringBuilder sb, string label, string value, string color) =>
        sb.Append($"""
            <div class="tile"><div class="tile-val" style="color:{color}">{Esc(value)}</div>
            <div class="tile-lbl">{Esc(label)}</div></div>
            """);

    private static void Card(StringBuilder sb, string title, string svg) =>
        sb.Append($"<div class=\"chartcard\"><h3>{Esc(title)}</h3>{svg}</div>");

    private static string FormatUptime(TimeSpan u) =>
        u.TotalDays >= 1 ? $"{(int)u.TotalDays}d {u.Hours}h"
        : u.TotalHours >= 1 ? $"{(int)u.TotalHours}h {u.Minutes}m"
        : $"{(int)u.TotalMinutes}m {u.Seconds}s";

    private static string Esc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
}
