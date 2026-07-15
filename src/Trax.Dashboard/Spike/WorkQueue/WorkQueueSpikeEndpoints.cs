using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Trax.Dashboard.Utilities;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Enums;
using Trax.Effect.Models.WorkQueue;

namespace Trax.Dashboard.Spike.WorkQueuePage;

/// <summary>
/// PHASE-2 SPIKE — Work Queue, the SECOND page through the htmx + Alpine + Grid.js template.
///
/// Purpose: prove the Dead Letters pattern GENERALIZES. Structurally identical (Grid.js server-side
/// data, htmx polling, Alpine selection, batch → OOB toast + HX-Trigger), but deliberately different
/// where it counts:
///   • different columns (Priority, shortened TrainName),
///   • a different batch action (Cancel), and
///   • a different action MECHANISM — cancel runs ExecuteUpdateAsync directly on the IDataContext,
///     NOT through ITraxScheduler. This is the real test: does the template accommodate an action
///     that isn't a scheduler call?
///
/// What stayed identical vs. Dead Letters is the interesting result — see the spike README.
/// </summary>
internal static class WorkQueueSpikeEndpoints
{
    private static readonly string[] SortableColumns =
    {
        nameof(WorkQueue.Id), nameof(WorkQueue.TrainName), nameof(WorkQueue.Status),
        nameof(WorkQueue.Priority), nameof(WorkQueue.ManifestId), nameof(WorkQueue.MetadataId),
        nameof(WorkQueue.CreatedAt), nameof(WorkQueue.DispatchedAt),
    };

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapWorkQueueSpike(
        this IEndpointRouteBuilder app,
        string routePrefix = "/trax-spike"
    )
    {
        routePrefix = "/" + routePrefix.Trim('/');
        var group = app.MapGroup(routePrefix);

        group.MapGet("/work-queue", () =>
            Results.Content(WorkQueueSpikePages.Page(routePrefix), "text/html"));
        group.MapGet("/work-queue/data", GetGridDataAsync);
        group.MapGet("/work-queue/live-count", GetLiveCountAsync);
        group.MapPost("/work-queue/cancel-selected", CancelSelectedAsync);

        return app;
    }

    private static async Task<IResult> GetGridDataAsync(
        HttpContext ctx,
        [FromServices] IDataContextProviderFactory factory,
        CancellationToken ct
    )
    {
        var q = ctx.Request.Query;
        int limit = ParseInt(q["limit"], 15, 1, 200);
        int offset = ParseInt(q["offset"], 0, 0, int.MaxValue);
        string? search = q["search"];
        string sort = Whitelist(q["sortColumn"], nameof(WorkQueue.Id));
        bool desc = q["sortDir"] != "asc";

        using var db = await factory.CreateDbContextAsync(ct);
        IQueryable<WorkQueue> query = db.WorkQueues.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(w =>
                (w.TrainName != null && w.TrainName.Contains(term)) ||
                w.ManifestId.ToString().Contains(term));
        }

        query = ApplySort(query, sort, desc);

        int total = await query.CountAsync(ct);
        var rows = await query
            .Skip(offset).Take(limit)
            .Select(w => new WorkQueueRow(
                w.Id,
                w.TrainName ?? string.Empty,
                w.Status.ToString(),
                w.Priority,
                w.ManifestId,
                w.MetadataId,
                w.CreatedAt,
                w.DispatchedAt))
            .ToListAsync(ct);

        return Results.Json(new { data = rows, total }, Json);
    }

    private static IQueryable<WorkQueue> ApplySort(IQueryable<WorkQueue> q, string column, bool desc) =>
        (column, desc) switch
        {
            (nameof(WorkQueue.Id), true) => q.OrderByDescending(w => w.Id),
            (nameof(WorkQueue.Id), false) => q.OrderBy(w => w.Id),
            (nameof(WorkQueue.TrainName), true) => q.OrderByDescending(w => w.TrainName),
            (nameof(WorkQueue.TrainName), false) => q.OrderBy(w => w.TrainName),
            (nameof(WorkQueue.Status), true) => q.OrderByDescending(w => w.Status),
            (nameof(WorkQueue.Status), false) => q.OrderBy(w => w.Status),
            (nameof(WorkQueue.Priority), true) => q.OrderByDescending(w => w.Priority),
            (nameof(WorkQueue.Priority), false) => q.OrderBy(w => w.Priority),
            (nameof(WorkQueue.ManifestId), true) => q.OrderByDescending(w => w.ManifestId),
            (nameof(WorkQueue.ManifestId), false) => q.OrderBy(w => w.ManifestId),
            (nameof(WorkQueue.MetadataId), true) => q.OrderByDescending(w => w.MetadataId),
            (nameof(WorkQueue.MetadataId), false) => q.OrderBy(w => w.MetadataId),
            (nameof(WorkQueue.CreatedAt), true) => q.OrderByDescending(w => w.CreatedAt),
            (nameof(WorkQueue.CreatedAt), false) => q.OrderBy(w => w.CreatedAt),
            (nameof(WorkQueue.DispatchedAt), true) => q.OrderByDescending(w => w.DispatchedAt),
            (nameof(WorkQueue.DispatchedAt), false) => q.OrderBy(w => w.DispatchedAt),
            _ => q.OrderByDescending(w => w.Id),
        };

    private static async Task<IResult> GetLiveCountAsync(
        [FromServices] IDataContextProviderFactory factory,
        CancellationToken ct
    )
    {
        using var db = await factory.CreateDbContextAsync(ct);
        int queued = await db.WorkQueues.AsNoTracking()
            .CountAsync(w => w.Status == WorkQueueStatus.Queued, ct);
        var cls = queued > 0 ? "pill pill--warn" : "pill pill--ok";
        return Results.Content($"""<span class="{cls}">{queued} queued</span>""", "text/html");
    }

    // Batch action differs from Dead Letters: no scheduler, a direct ExecuteUpdateAsync — and only
    // Queued entries are cancellable (mirrors the Radzen page's guard exactly).
    private static async Task<IResult> CancelSelectedAsync(
        HttpContext ctx,
        [FromServices] IDataContextProviderFactory factory,
        CancellationToken ct
    )
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        var ids = ParseIds(form["ids"].ToString());
        if (ids.Length == 0)
            return SpikeHtmx.Toast(false, "Cancel", "No rows selected.", trigger: null);

        try
        {
            using var db = await factory.CreateDbContextAsync(ct);
            int count = await db.WorkQueues
                .Where(w => ids.Contains(w.Id) && w.Status == WorkQueueStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, WorkQueueStatus.Cancelled), ct);

            return count == 0
                ? SpikeHtmx.Toast(false, "Cancel", "No queued entries selected. Only queued entries can be cancelled.", trigger: null)
                : SpikeHtmx.Toast(true, "Cancel", $"{count} work queue entry(s) cancelled.", trigger: "workqueue:changed");
        }
        catch (Exception ex)
        {
            return SpikeHtmx.Toast(false, "Cancel", ex.Message, trigger: null);
        }
    }

    private static long[] ParseIds(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<long>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Where(s => long.TryParse(s, out _)).Select(long.Parse).Distinct().ToArray();

    private static string Whitelist(string? candidate, string fallback) =>
        candidate is not null && SortableColumns.Contains(candidate) ? candidate : fallback;

    private static int ParseInt(string? raw, int fallback, int min, int max) =>
        int.TryParse(raw, out var v) ? Math.Clamp(v, min, max) : fallback;
}

internal readonly record struct WorkQueueRow(
    long Id,
    string TrainName,
    string Status,
    int Priority,
    long? ManifestId,
    long? MetadataId,
    DateTime CreatedAt,
    DateTime? DispatchedAt
);
