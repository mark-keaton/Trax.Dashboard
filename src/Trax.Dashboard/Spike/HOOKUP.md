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

## What's still open (needs a database)

**A full end-to-end run requires Postgres.** Verified finding: `AddTrax(trax => trax.AddEffects(e => e))`
with **no storage provider** registers *none* of `IDataContextProviderFactory`, `ITraxScheduler`, or
`IOperationsService` — they are wired by the storage layer. The only storage provider package that
exists is `Trax.Effect.Data.Postgres` (there is no in-memory/sqlite Trax provider). So:

- To exercise the real data path, point `UsePostgres(...)` at a dev/test database (a docker
  `postgres` + the Trax migrations), then browse `/trax-spike/*`.
- The render host under `scratchpad/spikehost/` stays useful for UI/JS iteration without a DB.

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
