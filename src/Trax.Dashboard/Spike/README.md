# Phase-2 Spike — Dead Letters on htmx + Alpine + Grid.js

A self-contained proof of the Phase-2 patterns from the htmx migration plan, rebuilding the
**Dead Letters** page without Blazor or Radzen. It lives in `Spike/` deliberately: it does **not**
touch the existing Blazor component tree, and the main package compiles with it included.

This is throwaway proof-of-concept code, not production. It exists to de-risk the two hardest
Phase-2 concerns before committing all 18 routes.

## What it proves

| Pattern | How | Status |
|---|---|---|
| **Grid.js server-side mode** | `GET /data` reuses the *same* `IQueryable<DeadLetter>` the Radzen grid used; Grid.js does paging/sort/search against it. | ✅ verified |
| **Server-side sort/filter/paging** | `sortColumn`/`sortDir`/`search`/`limit`/`offset` translated to a whitelisted LINQ sort — no client string reaches EF. | ✅ verified |
| **htmx polling** | Live-count pill uses `hx-trigger="load, every 5s"` + `hx-sync` supersession. | ✅ verified |
| **Alpine-store selection surviving refresh** | Selected ids live in an `Alpine.store('selection')` Set, not the DOM — untouched by grid re-render or a poll. | ✅ wired (see note) |
| **Batch op → OOB toast + grid reload** | POST → `ITraxScheduler` → returns `hx-swap-oob` toast **and** an `HX-Trigger: deadletters:changed` header that reloads the grid. | ✅ verified |
| **Row → detail routing** | Row "view" link → `GET /dead-letters/{id}`. | ✅ verified |
| **The htmx / Alpine boundary** | Documented + enforced: htmx owns server-driven, Alpine owns purely-client. See the header comment in `DeadLettersSpikePages.cs`. | ✅ established |

**Verified** = exercised over HTTP against a render host (see below) and the response asserted.
**Wired** = present and correct in the emitted HTML; the Set-survives-poll behavior is a client-runtime
property best confirmed with a browser (no Chromium was available in the spike environment, so it was
verified by inspection of the emitted attributes rather than a live render).

## Files

- `DeadLetters/DeadLettersSpikeEndpoints.cs` — all routes (Minimal API), query translation, batch ops.
- `DeadLetters/DeadLettersSpikePages.cs` — the page/detail HTML + CSS; the htmx/Alpine/Grid.js wiring.
- `DeadLetters/HtmxResults.cs` — `IResult` helper that emits HTML + an `HX-Trigger` header.

## Mount it in a host app

```csharp
using Trax.Dashboard.Spike.DeadLetters;
// ... after AddTrax(...) so IDataContextProviderFactory + ITraxScheduler are registered:
app.MapDeadLettersSpike();      // serves /trax-spike/dead-letters
```

It needs the same services the real dashboard does (`IDataContextProviderFactory`, `ITraxScheduler`),
so mount it in an app that already calls `AddTrax(...)`.

## How it was verified

A render host under `scratchpad/spikehost/` serves the spike's real page + CSS with a static data
stub (no Postgres), so the client wiring can be driven over HTTP. Representative checks that passed:

```
GET  /data?limit=5                     → 5 newest-first rows, total=47
GET  /data?sortColumn=Id&sortDir=asc   → ids [1,2,3], total=47
GET  /data?search=payment              → 8 matching rows (server-side filter)
POST /requeue-selected  ids=1,2,3      → toast + "HX-Trigger: deadletters:changed"; count 47→44
POST /acknowledge-all   note=...       → count 44→0; pill flips warn→ok; detail shows the note
```

The main project also compiles with this spike against the **genuine** Trax service signatures
(`IDataContextProviderFactory.CreateDbContextAsync`, `db.DeadLetters`, the four `ITraxScheduler`
batch methods) — so the server-side contract is real, not mocked.

## Known gaps (deliberately out of scope for the spike)

- CSS is minimal; not the final dashboard theme.
- Grid.js is CDN-loaded here; the real package bundles it as a `_content/` static asset.
- Theme is a cookie + Alpine toggle stub; the no-flash server-side stamp is described but not wired.
- No auth, no antiforgery on the POSTs — the real endpoints must add `UseAntiforgery` like today.
