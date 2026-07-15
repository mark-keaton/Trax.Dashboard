using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Trax.Effect.Data.Services.IDataContextFactory;
using Trax.Effect.Enums;
using Trax.Effect.Models.DeadLetter;
using Trax.Scheduler.Services.TraxScheduler;

namespace Trax.Dashboard.Spike.DeadLetters;

/// <summary>
/// PHASE-2 SPIKE — Dead Letters, rebuilt on htmx + Alpine + Grid.js.
///
/// This is a self-contained proof of the Phase-2 patterns from the migration plan. It deliberately
/// lives outside the Blazor component tree and does NOT touch the existing dashboard. Mount it with
/// <c>app.MapDeadLettersSpike()</c> in a host app to compare it side-by-side with the Radzen page.
///
/// What it proves end-to-end, against the real Trax services:
///   • Grid.js in SERVER-SIDE mode  → a JSON endpoint reusing the same IQueryable the Radzen grid used.
///   • htmx polling                 → hx-trigger="every Ns" refreshes the live count + grid, with hx-sync.
///   • Alpine-store selection       → selected ids held client-side, survive a polling refresh.
///   • Batch ops + OOB toast        → POST to ITraxScheduler, respond with an hx-swap-oob toast fragment.
///
/// Everything server-side (query helper, models, scheduler) is reused verbatim — this is a view swap.
/// </summary>
internal static class DeadLettersSpikeEndpoints
{
    // Single source of truth for the sortable/filterable columns. Grid.js sends column index +
    // direction; we translate to a whitelisted property name so no client string reaches LINQ.
    private static readonly string[] SortableColumns =
    {
        nameof(DeadLetter.Id),
        nameof(DeadLetter.ManifestId),
        nameof(DeadLetter.Status),
        nameof(DeadLetter.DeadLetteredAt),
        nameof(DeadLetter.Reason),
        nameof(DeadLetter.RetryCountAtDeadLetter),
        nameof(DeadLetter.ResolvedAt),
        nameof(DeadLetter.ResolutionNote),
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapDeadLettersSpike(
        this IEndpointRouteBuilder app,
        string routePrefix = "/trax-spike"
    )
    {
        routePrefix = "/" + routePrefix.Trim('/');
        var group = app.MapGroup(routePrefix);

        // Full page shell (loads htmx + Alpine + Grid.js, renders the chrome once).
        group.MapGet("/dead-letters", (HttpContext ctx) =>
            Results.Content(DeadLettersSpikePages.Page(routePrefix), "text/html"));

        // Stylesheet (kept as an endpoint so the spike is a single drop-in with no static-asset wiring).
        group.MapGet("/assets/spike.css", () =>
            Results.Content(DeadLettersSpikePages.Css, "text/css"));

        // Detail view — a minimal read-only stub, enough to prove the row "view" link routes.
        group.MapGet("/dead-letters/{id:long}", GetDetailAsync);

        // Grid.js server-side data source. Returns { data, total } as Grid.js expects.
        group.MapGet("/dead-letters/data", GetGridDataAsync);

        // Live header: the "N awaiting intervention" pill, polled by htmx.
        group.MapGet("/dead-letters/live-count", GetLiveCountAsync);

        // Batch operations. Each returns an OOB toast fragment; htmx also re-triggers the grid.
        group.MapPost("/dead-letters/requeue-all", RequeueAllAsync);
        group.MapPost("/dead-letters/requeue-selected", RequeueSelectedAsync);
        group.MapPost("/dead-letters/acknowledge-all", AcknowledgeAllAsync);
        group.MapPost("/dead-letters/acknowledge-selected", AcknowledgeSelectedAsync);

        return app;
    }

    // ---- Grid.js server-side data source -------------------------------------------------------

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
        string sort = Whitelist(q["sortColumn"], nameof(DeadLetter.Id));
        bool desc = q["sortDir"] != "asc"; // default newest-first, matches the Radzen page

        using var db = await factory.CreateDbContextAsync(ct);

        // SAME base query the Radzen page used: db.DeadLetters.AsNoTracking().OrderByDescending(d => d.Id)
        IQueryable<DeadLetter> query = db.DeadLetters.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Server-side filter across the human-visible text columns. Translated to SQL by EF.
            var term = search.Trim();
            query = query.Where(d =>
                (d.Reason != null && d.Reason.Contains(term)) ||
                (d.ResolutionNote != null && d.ResolutionNote.Contains(term)) ||
                d.ManifestId.ToString().Contains(term));
        }

        query = ApplySort(query, sort, desc);

        int total = await query.CountAsync(ct);
        var rows = await query
            .Skip(offset)
            .Take(limit)
            .Select(d => new DeadLetterRow(
                d.Id,
                d.ManifestId,
                d.Status.ToString(),
                d.DeadLetteredAt,
                d.Reason ?? string.Empty,
                d.RetryCountAtDeadLetter,
                d.ResolvedAt,
                d.ResolutionNote ?? string.Empty))
            .ToListAsync(ct);

        return Results.Json(new { data = rows, total }, Json);
    }

    private static IQueryable<DeadLetter> ApplySort(IQueryable<DeadLetter> q, string column, bool desc) =>
        (column, desc) switch
        {
            (nameof(DeadLetter.Id), true) => q.OrderByDescending(d => d.Id),
            (nameof(DeadLetter.Id), false) => q.OrderBy(d => d.Id),
            (nameof(DeadLetter.ManifestId), true) => q.OrderByDescending(d => d.ManifestId),
            (nameof(DeadLetter.ManifestId), false) => q.OrderBy(d => d.ManifestId),
            (nameof(DeadLetter.Status), true) => q.OrderByDescending(d => d.Status),
            (nameof(DeadLetter.Status), false) => q.OrderBy(d => d.Status),
            (nameof(DeadLetter.DeadLetteredAt), true) => q.OrderByDescending(d => d.DeadLetteredAt),
            (nameof(DeadLetter.DeadLetteredAt), false) => q.OrderBy(d => d.DeadLetteredAt),
            (nameof(DeadLetter.Reason), true) => q.OrderByDescending(d => d.Reason),
            (nameof(DeadLetter.Reason), false) => q.OrderBy(d => d.Reason),
            (nameof(DeadLetter.RetryCountAtDeadLetter), true) => q.OrderByDescending(d => d.RetryCountAtDeadLetter),
            (nameof(DeadLetter.RetryCountAtDeadLetter), false) => q.OrderBy(d => d.RetryCountAtDeadLetter),
            (nameof(DeadLetter.ResolvedAt), true) => q.OrderByDescending(d => d.ResolvedAt),
            (nameof(DeadLetter.ResolvedAt), false) => q.OrderBy(d => d.ResolvedAt),
            (nameof(DeadLetter.ResolutionNote), true) => q.OrderByDescending(d => d.ResolutionNote),
            (nameof(DeadLetter.ResolutionNote), false) => q.OrderBy(d => d.ResolutionNote),
            _ => q.OrderByDescending(d => d.Id),
        };

    // ---- Detail view (proves the row "view" link routes) ---------------------------------------

    private static async Task<IResult> GetDetailAsync(
        long id,
        [FromServices] IDataContextProviderFactory factory,
        CancellationToken ct
    )
    {
        using var db = await factory.CreateDbContextAsync(ct);
        var d = await db.DeadLetters.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null)
            return Results.NotFound($"Dead letter {id} not found.");

        return Results.Content(DeadLettersSpikePages.Detail(d), "text/html");
    }

    // ---- Live count (htmx-polled fragment) -----------------------------------------------------

    private static async Task<IResult> GetLiveCountAsync(
        [FromServices] IDataContextProviderFactory factory,
        CancellationToken ct
    )
    {
        using var db = await factory.CreateDbContextAsync(ct);
        int awaiting = await db.DeadLetters
            .AsNoTracking()
            .CountAsync(d => d.Status == DeadLetterStatus.AwaitingIntervention, ct);

        var cls = awaiting > 0 ? "pill pill--warn" : "pill pill--ok";
        var html = $"""<span class="{cls}" title="Awaiting intervention">{awaiting} awaiting</span>""";
        return Results.Content(html, "text/html");
    }

    // ---- Batch operations → OOB toast ----------------------------------------------------------

    private static Task<IResult> RequeueAllAsync(
        [FromServices] ITraxScheduler scheduler, CancellationToken ct
    ) => RunBatch(() => scheduler.RequeueAllDeadLettersAsync(ct), "Requeue");

    private static async Task<IResult> RequeueSelectedAsync(
        HttpContext ctx, [FromServices] ITraxScheduler scheduler, CancellationToken ct
    )
    {
        var ids = await ReadIdsAsync(ctx, ct);
        if (ids.Length == 0) return SpikeHtmx.Toast(false, "Requeue", "No rows selected.", trigger: null);
        return await RunBatch(() => scheduler.RequeueDeadLettersAsync(ids, ct), "Requeue");
    }

    private static async Task<IResult> AcknowledgeAllAsync(
        HttpContext ctx, [FromServices] ITraxScheduler scheduler, CancellationToken ct
    )
    {
        var note = (await ctx.Request.ReadFormAsync(ct))["note"].ToString();
        return await RunBatch(() => scheduler.AcknowledgeAllDeadLettersAsync(note, ct), "Acknowledge");
    }

    private static async Task<IResult> AcknowledgeSelectedAsync(
        HttpContext ctx, [FromServices] ITraxScheduler scheduler, CancellationToken ct
    )
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        var note = form["note"].ToString();
        var ids = ParseIds(form["ids"].ToString());
        if (ids.Length == 0) return SpikeHtmx.Toast(false, "Acknowledge", "No rows selected.", trigger: null);
        return await RunBatch(() => scheduler.AcknowledgeDeadLettersAsync(ids, note, ct), "Acknowledge");
    }

    private static async Task<IResult> RunBatch(Func<Task<BatchDeadLetterResult>> op, string label)
    {
        try
        {
            var result = await op();
            return SpikeHtmx.Toast(true, label, result.Message, trigger: "deadletters:changed");
        }
        catch (Exception ex)
        {
            return SpikeHtmx.Toast(false, label, ex.Message, trigger: null);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static async Task<long[]> ReadIdsAsync(HttpContext ctx, CancellationToken ct)
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        return ParseIds(form["ids"].ToString());
    }

    private static long[] ParseIds(string raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<long>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Where(s => long.TryParse(s, out _))
                 .Select(long.Parse)
                 .Distinct()
                 .ToArray();

    private static string Whitelist(string? candidate, string fallback) =>
        candidate is not null && SortableColumns.Contains(candidate) ? candidate : fallback;

    private static int ParseInt(string? raw, int fallback, int min, int max) =>
        int.TryParse(raw, out var v) ? Math.Clamp(v, min, max) : fallback;
}

/// <summary>Flat DTO shaped for the Grid.js columns. Kept separate from the EF entity on purpose.</summary>
internal readonly record struct DeadLetterRow(
    long Id,
    long ManifestId,
    string Status,
    DateTime DeadLetteredAt,
    string Reason,
    int RetryCountAtDeadLetter,
    DateTime? ResolvedAt,
    string ResolutionNote
);
