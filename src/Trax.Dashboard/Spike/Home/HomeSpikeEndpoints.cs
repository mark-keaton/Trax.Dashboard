using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Trax.Dashboard.Models;
using Trax.Scheduler.Services.Operations;
// Both namespaces define TrainFailureCount / ThroughputSeries; alias the dashboard-model versions.
using DashFailure = Trax.Dashboard.Models.TrainFailureCount;
using DashThroughput = Trax.Dashboard.Models.ThroughputSeries;

namespace Trax.Dashboard.Spike.Home;

/// <summary>
/// PHASE-2 SPIKE — the home dashboard, proving KPIs + charts render as server-side SVG with NO
/// client chart library. Stat tiles, KPI cards, and the four Radzen charts (grouped column,
/// two horizontal bars, multi-series line) all come back as one HTML document. A live SSE-style
/// refresh is left to the plan; here an htmx poll re-fetches the metrics fragment.
///
/// In the real dashboard the data comes from IOperationsService.GetDashboardMetricsAsync(...);
/// this spike accepts a HomeMetrics snapshot so it runs without the full Trax stack.
/// </summary>
internal static class HomeSpikeEndpoints
{
    public static IEndpointRouteBuilder MapHomeSpike(
        this IEndpointRouteBuilder app,
        Func<HomeMetrics> metricsProvider,
        string routePrefix = "/trax-spike"
    )
    {
        routePrefix = "/" + routePrefix.Trim('/');
        var group = app.MapGroup(routePrefix);

        group.MapGet("/home", () =>
            Results.Content(HomeSpikePages.Page(routePrefix, metricsProvider()), "text/html"));

        // htmx-polled fragment: just the tiles + charts, swapped in place.
        group.MapGet("/home/metrics", () =>
            Results.Content(HomeSpikePages.MetricsFragment(metricsProvider()), "text/html"));

        return app;
    }

    /// <summary>
    /// REAL hook-up overload. Injects the genuine <see cref="IOperationsService"/> from Trax DI and
    /// maps its metrics into <see cref="HomeMetrics"/> — the same mapping Index.razor.cs does. This is
    /// how the home page mounts against a live Trax app (no stub provider). CPU% needs per-request
    /// sampling state the Radzen page keeps in a component; omitted here (reported as 0) since a
    /// stateless endpoint can't hold the previous sample.
    /// </summary>
    public static IEndpointRouteBuilder MapHomeSpike(
        this IEndpointRouteBuilder app,
        string routePrefix = "/trax-spike"
    )
    {
        routePrefix = "/" + routePrefix.Trim('/');
        var group = app.MapGroup(routePrefix);

        group.MapGet("/home", async ([FromServices] IOperationsService ops, CancellationToken ct) =>
            Results.Content(HomeSpikePages.Page(routePrefix, await Map(ops, ct)), "text/html"));

        group.MapGet("/home/metrics", async ([FromServices] IOperationsService ops, CancellationToken ct) =>
            Results.Content(HomeSpikePages.MetricsFragment(await Map(ops, ct)), "text/html"));

        return app;
    }

    private static async Task<HomeMetrics> Map(IOperationsService ops, CancellationToken ct)
    {
        var m = await ops.GetDashboardMetricsAsync(MetricsRange.Last24Hours, hideAdminTrains: false, ct);
        var s = ops.GetServerMetrics();

        static string Short(string full)
        {
            var dot = full.LastIndexOf('.');
            return dot >= 0 ? full[(dot + 1)..] : full;
        }

        return new HomeMetrics
        {
            CpuPercent = 0, // needs per-sample state; not available in a stateless endpoint
            MemoryMb = (int)(s.WorkingSetBytes / 1024 / 1024),
            GcHeapMb = (int)(s.GcHeapBytes / 1024 / 1024),
            Uptime = TimeSpan.FromSeconds(s.UptimeSeconds),
            ExecutionsToday = m.Kpis.ExecutionsToday,
            SuccessRate = (int)Math.Round(m.Kpis.SuccessRate),
            CurrentlyRunning = m.Kpis.CurrentlyRunning,
            UnresolvedDeadLetters = m.Kpis.UnresolvedDeadLetters,
            ExecutionsOverTime = m.ExecutionsOverTime
                .Select(b => new ExecutionTimePoint
                {
                    Label = b.Timestamp.ToString("HH"),
                    Completed = b.Completed, Failed = b.Failed, Cancelled = b.Cancelled,
                }).ToList(),
            AvgDurations = m.TopAverageDurations
                .Select(d => new TrainDuration { Name = Short(d.TrainName), AvgMs = Math.Round(d.AverageMilliseconds) })
                .ToList(),
            TopFailures = m.TopFailures
                .Select(f => new DashFailure { Name = Short(f.TrainName), Count = f.Count })
                .ToList(),
            Throughput = m.ThroughputSeries
                .Select((sr, i) => new DashThroughput
                {
                    Name = sr.TrainName == "Other" ? "Other" : Short(sr.TrainName),
                    Color = i == 0 ? SvgCharts.Completed : i == 1 ? SvgCharts.Failed : SvgCharts.Cancelled,
                    Points = sr.Buckets.Select(b => new ThroughputPoint { Label = b.Timestamp.ToString("MMM dd HH"), Count = b.Count }).ToList(),
                }).ToList(),
        };
    }
}

/// <summary>Snapshot the home page renders — mirrors what IOperationsService.GetDashboardMetricsAsync returns.</summary>
internal sealed class HomeMetrics
{
    // server health
    public int CpuPercent { get; init; }
    public int MemoryMb { get; init; }
    public int GcHeapMb { get; init; }
    public TimeSpan Uptime { get; init; }
    // KPIs
    public int ExecutionsToday { get; init; }
    public int SuccessRate { get; init; }
    public int CurrentlyRunning { get; init; }
    public int UnresolvedDeadLetters { get; init; }
    // charts
    public List<ExecutionTimePoint> ExecutionsOverTime { get; init; } = [];
    public List<TrainDuration> AvgDurations { get; init; } = [];
    public List<DashFailure> TopFailures { get; init; } = [];
    public List<DashThroughput> Throughput { get; init; } = [];
}
