# Phase-2 Spike — htmx + Alpine + Grid.js

Self-contained proof of the Phase-2 patterns from the migration plan, rebuilding dashboard surfaces
without Blazor or Radzen:

- **Dead Letters** (`Spike/DeadLetters/`) — first page; proves the grid pattern end-to-end.
- **Work Queue** (`Spike/WorkQueue/`) — second page; proves the pattern **generalizes**.
- **Dialogs** (`Spike/Dialogs/`) — the reflection-driven "Run Train" modal; proves the last
  genuinely-different surface.

Lives in `Spike/` deliberately: does **not** touch the existing Blazor component tree, and the main
package compiles (and all tests stay green) with it included. Throwaway proof-of-concept code, not
production.

## The reflection dialog (Dialogs result)

The Radzen "Run Train" dialog builds a form by reflecting over a train's input `Type`, then reassembles
+ coerces the posted values back into that type. The spike proves this whole mechanism is **pure
server-side C#** that never needed Blazor — `ReflectionForm.cs` is a near-verbatim port of the Radzen
`BuildInputFromForm` / `ToJsonNode` / `FormatLabel` / `GetPlaceholder`. htmx just swaps the rendered
modal fragment; Alpine owns open/close.

Verified round-trip across property kinds (string, int, enum, bool checkbox, nullable DateTime),
including edge cases: unchecked checkbox → `false`, empty nullable → `null`, enum default → ordinal 0.

**Latent bug found in the existing dashboard.** The Radzen dialog deserializes form values with
`new JsonSerializerOptions { PropertyNameCaseInsensitive = true }` and **no `JsonStringEnumConverter`**.
Because the form renders enums as their *name* (e.g. `"Csv"`), running any train whose input has an
enum property via the **form tab** throws *"The JSON value could not be converted to <Enum>"*. The
spike registers the converter and works; **the real migration should apply the same fix.**

Faithful-to-original note: an unparseable number (e.g. `RetryCount=notanumber`) surfaces a
deserialization error toast — same as the Radzen path. A UX opportunity (per-field validation) for the
real migration, not fixed in the spike.

## Does it generalize? (Work Queue result)

Building the second page was the real test. Findings:

- **Shared with zero change:** the Alpine `selection`/`theme` stores, the polling live-count pill,
  the toast + `HX-Trigger` convention (extracted to `SpikeHtmx.cs` when the 2nd page needed it),
  the Grid.js server-config shape (data/sort/paging/search/resize), and all the CSS/theming.
- **Page-specific (the only per-page work):** the column list, the title/lede, the live-count label,
  and the batch action(s). That's it.
- **The pattern held under a different action mechanism.** Dead Letters' batch ops go through
  `ITraxScheduler`; Work Queue's Cancel runs `ExecuteUpdateAsync` **directly on the IDataContext**,
  with a "only Queued entries cancellable" guard. Same template accommodated both — the toast/trigger
  contract didn't care what the action did underneath.
- **One collision to note:** the entity `WorkQueue` and a `Spike.WorkQueue` namespace segment
  clashed; renamed the namespace to `Spike.WorkQueuePage`. Trivial, but real — worth a naming
  convention for the other 16 pages.

Conclusion: **the template is reusable, not Dead-Letters-specific.** A new data page is ~1 endpoints
file + ~1 page file, mostly the column list.

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
app.MapWorkQueueSpike();        // serves /trax-spike/work-queue
app.MapDialogSpike();           // serves /trax-spike/dialogs (self-contained; no Trax services needed)
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
