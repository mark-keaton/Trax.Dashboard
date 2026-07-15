using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Trax.Dashboard.Models;

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
    public List<TrainFailureCount> TopFailures { get; init; } = [];
    public List<ThroughputSeries> Throughput { get; init; } = [];
}
