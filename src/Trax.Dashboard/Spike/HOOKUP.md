# Hooking the spike to real Trax

The spikes so far ran against a **render host with stub data**. This is what it takes to point them
at a live Trax application, and what's proven vs. still open.

## The wiring (three lines)

The spike endpoints are ordinary Minimal-API endpoints that inject Trax services via `[FromServices]`.
Mount them in any app that already calls `AddTrax(...)`:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTrax(trax =>
    trax.AddEffects(effects => effects.UsePostgres(connectionString))   // <-- storage REQUIRED (see below)
        .AddMediator(typeof(Program).Assembly)
        .AddScheduler()   // <-- REQUIRED for the home page: registers IOperationsService (metrics)
);

var app = builder.Build();
app.UseAntiforgery();                 // the batch POSTs need this, like the Blazor page does today

app.MapDeadLettersSpike();            // IDataContextProviderFactory + ITraxScheduler
app.MapWorkQueueSpike();              // IDataContextProviderFactory (+ ExecuteUpdateAsync)
app.MapDialogSpike();                 // self-contained (sample input types; no Trax services)
app.MapHomeSpike();                   // IOperationsService  ← the real overload, not the stub Func

app.Run();
```

That's it — DI injects the genuine `IDataContextProviderFactory`, `ITraxScheduler`, and
`IOperationsService`. No stubs. `HomeSpikeEndpoints` has two overloads: the `Func<HomeMetrics>` one
(used by the render host) and the real `IOperationsService` one above, which maps metrics exactly like
`Index.razor.cs`.

## What's proven

- **Type-level hook-up.** The main project compiles with the real `IOperationsService` overload and
  the `[FromServices]` `IDataContextProviderFactory` / `ITraxScheduler` injections — against the genuine
  Trax signatures, not mocks.
- **The query/scheduler calls** reuse the same `db.DeadLetters` / `db.WorkQueues` / batch methods the
  Radzen pages use.

## Proven end to end against real Postgres ✅

A `traxhost` (in `scratchpad/traxhost/`) wired the four endpoints to a live Postgres via
`AddTrax(trax => trax.AddEffects(e => e.UsePostgres(conn)).AddMediator(...))` and ran the full loop:

- **Boot + migrations:** the real context created the whole `trax.*` schema on startup — `trax.migrations`
  shows **35 migrations applied**. No manual DDL.
- **Seed:** 20 work-queue rows inserted via the real `WorkQueue.Create(...)` factory + `db.SaveChanges()`.
- **Read path:** `GET /work-queue/data` returned genuine rows; sort (`ORDER BY priority DESC` → `[9,8,7,7,7]`)
  and search (`WHERE ... LIKE '%Invoice%'` → 9 matches) resolved in Postgres, not in memory. The real enum
  serialized as lowercase `queued` (a detail the stub couldn't show).
- **Write path:** `POST /work-queue/cancel-selected` for ids 16–20 ran the real `ExecuteUpdateAsync`;
  `psql` confirmed those five rows flipped to `cancelled` (`queued|15, cancelled|5`), the live-count pill
  dropped 20→15, and the grid re-rendered from the DB.
- **Home page:** rendered real KPIs (13 executions today, 100% success, live memory/GC) from
  `IOperationsService.GetDashboardMetricsAsync` + `GetServerMetrics`, all four SVG charts drawn from the
  real metrics.

**Finding — `.AddScheduler()` is required.** `IOperationsService` (and `ITraxScheduler`) are registered by
`AddScheduler()`, NOT by effects/storage. Omitting it boots fine but the home page 500s with
*"No service for type IOperationsService"*. Easy to miss; it's in the wiring snippet above.

Tables live in a **`trax` schema** (`trax.work_queue`, `trax.dead_letter`, …), not `public`.

**Storage is required** (verified separately): `AddEffects(e => e)` with *no* provider registers none of the
three services — they come from the storage layer, and `Trax.Effect.Data.Postgres` is the only provider
package (no in-memory/sqlite). So a real run needs Postgres; the `scratchpad/spikehost/` render host stays
useful for DB-less UI/JS iteration.

### Reproduce
```
createdb trax_spike   # or CREATE DATABASE trax_spike;
# host uses: Host=localhost;Port=5432;Database=trax_spike;Username=admin;Password=...
cd scratchpad/traxhost && ASPNETCORE_URLS=http://localhost:5200 dotnet run
# browse http://localhost:5200/trax-spike/{home,work-queue,dead-letters,dialogs}
```

## Known gaps to close before this is a real page (not spike)

1. **CPU%** on the home page is reported as `0` — it needs per-sample state the Radzen page keeps in a
   component; a stateless endpoint can't. Options: a small singleton sampler, or drop CPU% to a
   client-read.
2. **Antiforgery** — the batch/dialog POSTs must carry the token once `UseAntiforgery()` is on.
3. **Client assets** — spike loads htmx/Alpine/Grid.js from CDN; the real package bundles them as
   `_content/Trax.Dashboard/...` static assets (and the strict-CSP story).
4. **The enum-converter fix** (see README) should land in the real dialog regardless of the rewrite.

## Recommended next step

Stand up a throwaway **Postgres-backed** host (docker postgres + `UsePostgres` + the four `Map*` calls),
seed a handful of trains/dead-letters, and click through `/trax-spike/*` against real data. That closes
the last unknown — the data path — end to end.
