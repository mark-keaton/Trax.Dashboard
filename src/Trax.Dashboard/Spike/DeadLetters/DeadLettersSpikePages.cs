namespace Trax.Dashboard.Spike.DeadLetters;

/// <summary>
/// Renders the Dead Letters spike page. Plain interpolated HTML on purpose — a real migration would
/// use Razor Pages/views, but keeping it string-based makes the htmx / Alpine / Grid.js wiring the
/// whole story of the file, with nothing else in the way.
///
/// THE htmx / Alpine BOUNDARY (the Phase-2 rule this spike locks in):
///   • htmx owns everything SERVER-DRIVEN — the polled live count, the batch POSTs, grid reloads.
///   • Alpine owns everything PURELY-CLIENT — the selected-row set, the acknowledge-note input toggle.
///   • Grid.js owns the TABLE — it fetches rows from the server-side data endpoint itself.
///   • They meet in exactly two places, both explicit below:
///       1. Grid.js 'rowClick'/checkbox → writes into the Alpine `selection` store.
///       2. A successful batch POST → HX-Trigger "deadletters:changed" → Grid.js .forceRender().
/// </summary>
internal static class DeadLettersSpikePages
{
    public static string Page(string prefix) => $$"""
        <!DOCTYPE html>
        <html lang="en" x-data :data-theme="$store.theme.mode">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>Dead Letters — htmx spike</title>

          <!-- Third-party client libs. In the real package these are bundled static assets served
               from _content/Trax.Dashboard/…; pinned CDN here keeps the spike self-contained. -->
          <script src="https://unpkg.com/htmx.org@2.0.4"></script>
          <script defer src="https://unpkg.com/alpinejs@3.14.8/dist/cdn.min.js"></script>
          <link href="https://unpkg.com/gridjs/dist/theme/mermaid.min.css" rel="stylesheet" />
          <script src="https://unpkg.com/gridjs/dist/gridjs.umd.js"></script>

          <link rel="stylesheet" href="{{prefix}}/assets/spike.css" />
        </head>
        <body>
          <!-- ALPINE STORES — the only client-side state in the app. -->
          <script>
            document.addEventListener('alpine:init', () => {
              // Selection survives Grid.js re-renders AND htmx polling because it lives here,
              // not in the DOM. This is the direct answer to the plan's "stateful selection
              // across polls" risk.
              Alpine.store('selection', {
                ids: new Set(),
                has(id) { return this.ids.has(id); },
                toggle(id) { this.ids.has(id) ? this.ids.delete(id) : this.ids.add(id); },
                clear() { this.ids.clear(); },
                get count() { return this.ids.size; },
                get csv() { return [...this.ids].join(','); },
              });
              // Theme via a cookie-backed store (no flash: server could stamp data-theme on first byte).
              Alpine.store('theme', {
                mode: (document.cookie.match(/trax_theme=(\w+)/)?.[1]) || 'light',
                toggle() {
                  this.mode = this.mode === 'dark' ? 'light' : 'dark';
                  document.cookie = 'trax_theme=' + this.mode + ';path=/;max-age=31536000';
                },
              });
            });
          </script>

          <header class="topbar">
            <div class="title-wrap">
              <h1>Dead Letters</h1>
              <!-- htmx OWNS this: server-rendered pill, polled every 5s, superseded if slow. -->
              <span id="live-count"
                    hx-get="{{prefix}}/dead-letters/live-count"
                    hx-trigger="load, every 5s"
                    hx-sync="this:replace"></span>
            </div>
            <button class="btn btn--ghost" @click="$store.theme.toggle()"
                    x-text="$store.theme.mode === 'dark' ? '☀ Light' : '☾ Dark'"></button>
          </header>

          <p class="lede">
            Train executions that exceeded their retry count and need manual intervention.
            Resolve by requeuing or acknowledging. <em>Phase-2 spike — htmx + Alpine + Grid.js.</em>
          </p>

          <!-- BATCH TOOLBAR. Buttons are htmx POSTs; the "selected" ones read the Alpine store. -->
          <div class="toolbar" x-data="{ ackOpen:false, ackSelected:false }">
            <button class="btn" hx-post="{{prefix}}/dead-letters/requeue-all"
                    hx-target="#toast-slot" hx-swap="none"
                    hx-confirm="Requeue ALL dead letters?">↻ Requeue All</button>

            <button class="btn" @click="ackOpen = true; ackSelected = false">✓ Acknowledge All…</button>

            <!-- These two only appear when the Alpine selection store is non-empty. -->
            <template x-if="$store.selection.count > 0">
              <span class="selgroup">
                <button class="btn btn--primary"
                        hx-post="{{prefix}}/dead-letters/requeue-selected"
                        hx-target="#toast-slot" hx-swap="none"
                        :hx-vals="JSON.stringify({ ids: $store.selection.csv })">
                  ↻ Requeue Selected (<span x-text="$store.selection.count"></span>)
                </button>
                <button class="btn btn--primary"
                        @click="ackOpen = true; ackSelected = true">
                  ✓ Acknowledge Selected (<span x-text="$store.selection.count"></span>)
                </button>
                <button class="btn btn--ghost" @click="$store.selection.clear()">Clear</button>
              </span>
            </template>

            <!-- ACKNOWLEDGE NOTE INPUT — Alpine owns the open/close; htmx owns the submit. -->
            <form class="ack-row" x-show="ackOpen" x-cloak
                  :hx-post="ackSelected
                      ? '{{prefix}}/dead-letters/acknowledge-selected'
                      : '{{prefix}}/dead-letters/acknowledge-all'"
                  hx-target="#toast-slot" hx-swap="none"
                  @htmx:after-request="ackOpen = false; if ($event.detail.successful) $store.selection.clear()">
              <input type="hidden" name="ids" :value="$store.selection.csv" />
              <input class="input" type="text" name="note"
                     placeholder="Resolution note (root cause, why no retry needed)…" />
              <button class="btn btn--ok" type="submit">Confirm</button>
              <button class="btn btn--ghost" type="button" @click="ackOpen = false">Cancel</button>
            </form>
          </div>

          <!-- OOB toast target. Batch responses swap into here via hx-swap-oob. -->
          <div id="toast-slot"></div>

          <!-- GRID.JS mounts here in server-side mode. It fetches its own data + does its own
               paging/sort; we translate its query params to the same IQueryable the Radzen grid used. -->
          <div id="grid"></div>

          <script>
            const PREFIX = '{{prefix}}';
            const COLUMNS = ['Id','ManifestId','Status','DeadLetteredAt','Reason','RetryCountAtDeadLetter','ResolvedAt','ResolutionNote'];

            const grid = new gridjs.Grid({
              columns: [
                // Selection checkbox column — the bridge from Grid.js into the Alpine store.
                {
                  name: gridjs.html('<span title="select">◻</span>'),
                  sort: false,
                  width: '44px',
                  formatter: (_, row) => {
                    const id = row.cells[1].data; // Id column
                    return gridjs.html(
                      `<input type="checkbox" class="rowcheck" data-id="${id}"
                              onchange="Alpine.store('selection').toggle(${id})"
                              ${Alpine.store('selection').has(id) ? 'checked' : ''} />`);
                  },
                },
                // Grid.js quirk: resizing needs BOTH width AND minWidth, or the handle
                // highlights but the drag won't take. https://gridjs.io/docs/examples/resizable
                { name: 'Id', width: '80px', minWidth: '60px' },
                { name: 'Manifest', width: '120px', minWidth: '90px' },
                { name: 'Status', width: '150px', minWidth: '110px',
                  formatter: (cell) => gridjs.html(
                    `<span class="badge badge--${String(cell).toLowerCase()}">${cell}</span>`) },
                { name: 'Dead Lettered At', width: '180px', minWidth: '140px' },
                { name: 'Reason', width: '260px', minWidth: '160px' },
                { name: 'Retries', width: '90px', minWidth: '70px' },
                { name: 'Resolved At', width: '160px', minWidth: '120px' },
                { name: 'Resolution Note', width: '220px', minWidth: '140px' },
                { name: '', width: '60px', sort: false,
                  formatter: (_, row) => {
                    const id = row.cells[1].data;
                    return gridjs.html(
                      `<a class="viewlink" href="${PREFIX}/dead-letters/${id}" title="View">🔍</a>`);
                  } },
              ],
              server: {
                url: `${PREFIX}/dead-letters/data`,
                // Grid.js hands us the built URL with its own pagination params; we append ours.
                then: (res) => res.data.map(d => [
                  null, // checkbox placeholder (formatter reads row.cells[1])
                  d.id, d.manifestId, d.status,
                  fmtDate(d.deadLetteredAt), d.reason, d.retryCountAtDeadLetter,
                  fmtDate(d.resolvedAt), d.resolutionNote,
                  null, // view link
                ]),
                total: (res) => res.total,
              },
              pagination: { limit: 15, server: {
                url: (prev, page, limit) => `${prev}${prev.includes('?') ? '&' : '?'}limit=${limit}&offset=${page * limit}`,
              } },
              sort: { multiColumn: false, server: {
                url: (prev, cols) => {
                  if (!cols.length) return prev;
                  const c = cols[0];
                  const name = COLUMNS[c.index - 1]; // -1 for the checkbox column
                  if (!name) return prev;
                  const glue = prev.includes('?') ? '&' : '?';
                  return `${prev}${glue}sortColumn=${name}&sortDir=${c.direction === 1 ? 'asc' : 'desc'}`;
                },
              } },
              search: { server: {
                url: (prev, keyword) => `${prev}${prev.includes('?') ? '&' : '?'}search=${encodeURIComponent(keyword)}`,
              } },
              resizable: true, // parity: the Radzen TraxDataGrid sets AllowColumnResize="true"
              className: { table: 'trax-grid' },
            });
            grid.render(document.getElementById('grid'));

            // THE OTHER BRIDGE: server says "data changed" → reload the grid + refresh the live count.
            document.body.addEventListener('deadletters:changed', () => {
              grid.forceRender();
              htmx.trigger('#live-count', 'refresh');
            });
            // Let the live-count pill respond to that custom event too.
            document.getElementById('live-count').setAttribute('hx-trigger', 'load, every 5s, refresh');

            function fmtDate(v) {
              if (!v) return '—';
              const d = new Date(v);
              return d.toISOString().slice(0, 19).replace('T', ' ');
            }
          </script>
        </body>
        </html>
        """;

    public static string Detail(Trax.Effect.Models.DeadLetter.DeadLetter d) => $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>Dead Letter #{{d.Id}}</title>
          <link rel="stylesheet" href="/trax-spike/assets/spike.css" />
        </head>
        <body>
          <header class="topbar"><h1>Dead Letter #{{d.Id}}</h1>
            <a class="btn btn--ghost" href="/trax-spike/dead-letters">← Back</a></header>
          <dl class="detail">
            <dt>Manifest Id</dt><dd>{{d.ManifestId}}</dd>
            <dt>Status</dt><dd><span class="badge badge--{{d.Status.ToString().ToLowerInvariant()}}">{{d.Status}}</span></dd>
            <dt>Dead Lettered At</dt><dd>{{d.DeadLetteredAt:yyyy-MM-dd HH:mm:ss}}</dd>
            <dt>Reason</dt><dd>{{SpikeHtmx.Escape(d.Reason)}}</dd>
            <dt>Retry Count</dt><dd>{{d.RetryCountAtDeadLetter}}</dd>
            <dt>Resolved At</dt><dd>{{(d.ResolvedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—")}}</dd>
            <dt>Resolution Note</dt><dd>{{SpikeHtmx.Escape(d.ResolutionNote)}}</dd>
          </dl>
        </body>
        </html>
        """;

    public const string Css = """
        :root{
          --bg:#f7f8fa; --card:#fff; --ink:#14191f; --muted:#5a6b7b; --line:#dde3e9;
          --accent:#c67518; --ok:#227a52; --warn:#b06a10; --err:#b23a2c;
          --ok-bg:#dcefe4; --warn-bg:#f6e7cd; --err-bg:#f6ddd8;
        }
        [data-theme=dark]:root, [data-theme=dark]{
          --bg:#0e1318; --card:#171f27; --ink:#eef2f6; --muted:#8695a3; --line:#263038;
          --accent:#e8973a; --ok:#4bb583; --warn:#d99326; --err:#e0685a;
          --ok-bg:#14261d; --warn-bg:#2f2412; --err-bg:#2e1a17;
        }
        *{box-sizing:border-box}
        body{margin:0;padding:24px;max-width:1180px;margin-inline:auto;background:var(--bg);color:var(--ink);
          font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;line-height:1.5}
        [x-cloak]{display:none!important}
        .topbar{display:flex;align-items:center;justify-content:space-between;gap:16px;margin-bottom:8px}
        .title-wrap{display:flex;align-items:center;gap:12px}
        h1{font-size:26px;margin:0;letter-spacing:-.02em}
        .lede{color:var(--muted);margin:0 0 18px;max-width:70ch}
        .pill{font:600 12px/1 ui-monospace,monospace;padding:5px 10px;border-radius:20px}
        .pill--warn{background:var(--warn-bg);color:var(--warn)}
        .pill--ok{background:var(--ok-bg);color:var(--ok)}
        .toolbar{display:flex;flex-wrap:wrap;align-items:center;gap:8px;margin-bottom:14px}
        .selgroup{display:inline-flex;gap:8px;align-items:center}
        .ack-row{display:flex;gap:8px;align-items:center;flex-basis:100%;margin-top:6px}
        .input{flex:1;padding:7px 10px;border:1px solid var(--line);border-radius:7px;background:var(--card);color:var(--ink)}
        .btn{border:1px solid var(--line);background:var(--card);color:var(--ink);padding:7px 12px;
          border-radius:7px;font-size:13px;font-weight:600;cursor:pointer}
        .btn:hover{border-color:var(--accent)}
        .btn--primary{border-color:var(--accent);color:var(--accent)}
        .btn--ok{background:var(--ok);border-color:var(--ok);color:#fff}
        .btn--ghost{background:transparent;color:var(--muted)}
        #toast-slot{position:fixed;top:18px;right:18px;z-index:50}
        .toast{padding:12px 16px;border-radius:8px;box-shadow:0 6px 24px rgba(0,0,0,.15);margin-bottom:8px;font-size:14px}
        .toast--ok{background:var(--ok-bg);color:var(--ok);border:1px solid var(--ok)}
        .toast--err{background:var(--err-bg);color:var(--err);border:1px solid var(--err)}
        .badge{font:600 11px/1 ui-monospace,monospace;padding:4px 8px;border-radius:5px;background:var(--line)}
        .badge--awaitingintervention{background:var(--warn-bg);color:var(--warn)}
        .badge--retried{background:var(--ok-bg);color:var(--accent)}
        .badge--acknowledged{background:var(--ok-bg);color:var(--ok)}
        .viewlink{text-decoration:none;font-size:15px}
        .rowcheck{width:16px;height:16px;cursor:pointer}
        .detail{display:grid;grid-template-columns:180px 1fr;gap:8px 16px;background:var(--card);
          border:1px solid var(--line);border-radius:10px;padding:20px;max-width:720px}
        .detail dt{color:var(--muted);font-weight:600}
        .detail dd{margin:0}

        /* --- Grid.js theming ---
           Grid.js ships a hardcoded light theme (mermaid.min.css) with no CSS variables, so its
           table stays white in dark mode unless we override its classes. We re-point them at the
           same theme tokens the rest of the page uses. (A real finding from the spike: a client-side
           grid widget forces you to theme over its baked-in styles — noted in the plan's Grid.js risk.) */
           NOTE ON SPECIFICITY: Grid.js targets cells as `td.gridjs-td` / `th.gridjs-th` (element+class)
           and sets `background-color`, so a flat `.gridjs-td{background:...}` loses. We match its
           selector shape and property exactly so the override actually wins. */
        .gridjs-container{color:var(--ink)}
        .gridjs-wrapper{background:var(--card);box-shadow:none;border:1px solid var(--line)}
        .gridjs-table{background:var(--card)}
        table.gridjs-table th.gridjs-th{background-color:var(--bg);color:var(--muted);border-color:var(--line)}
        table.gridjs-table th.gridjs-th-sort:hover,
        table.gridjs-table th.gridjs-th-sort:focus{background-color:var(--line)}
        .gridjs-th-content{color:var(--muted)}
        .gridjs-tbody,table.gridjs-table td.gridjs-td{background-color:var(--card);color:var(--ink);border-color:var(--line)}
        table.gridjs-table tr.gridjs-tr:hover td.gridjs-td{background-color:var(--bg)}
        table.gridjs-table tr.gridjs-tr-selected td.gridjs-td{background-color:var(--warn-bg)}
        .gridjs-footer{background-color:var(--card);border-color:var(--line);box-shadow:none}
        .gridjs-pagination,.gridjs-summary{color:var(--muted)}
        .gridjs-pagination .gridjs-pages button{background-color:var(--card);color:var(--ink);border-color:var(--line)}
        .gridjs-pagination .gridjs-pages button:hover{background-color:var(--bg)}
        .gridjs-pagination .gridjs-pages button.gridjs-currentPage{background-color:var(--bg);font-weight:700}
        input.gridjs-input,input.gridjs-search-input{background-color:var(--card);color:var(--ink);border-color:var(--line)}
        input.gridjs-search-input::placeholder{color:var(--muted)}
        /* Sort arrows are background-image SVGs tuned for a light bg; lift them in dark mode. */
        [data-theme=dark] .gridjs-sort{filter:invert(0.85)}
        """;
}
