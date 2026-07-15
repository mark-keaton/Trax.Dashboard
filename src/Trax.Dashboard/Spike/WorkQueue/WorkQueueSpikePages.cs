using Trax.Dashboard.Spike.DeadLetters;

namespace Trax.Dashboard.Spike.WorkQueuePage;

/// <summary>
/// Work Queue page — same htmx/Alpine/Grid.js shell as Dead Letters. The ONLY page-specific parts
/// are: the title/lede, the column set, the live-count label, and the single Cancel batch action.
/// Everything else (Alpine stores, polling pill, toast slot, Grid.js server config shape, theming)
/// is identical — proving the pattern is a reusable template, not a one-off. Reuses the shared CSS
/// from <see cref="DeadLettersSpikePages.Css"/>.
/// </summary>
internal static class WorkQueueSpikePages
{
    public static string Page(string prefix) => $$"""
        <!DOCTYPE html>
        <html lang="en" x-data :data-theme="$store.theme.mode">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>Work Queue — htmx spike</title>
          <script src="https://unpkg.com/htmx.org@2.0.4"></script>
          <script defer src="https://unpkg.com/alpinejs@3.14.8/dist/cdn.min.js"></script>
          <link href="https://unpkg.com/gridjs/dist/theme/mermaid.min.css" rel="stylesheet" />
          <script src="https://unpkg.com/gridjs/dist/gridjs.umd.js"></script>
          <link rel="stylesheet" href="{{prefix}}/assets/spike.css" />
        </head>
        <body>
          <script>
            document.addEventListener('alpine:init', () => {
              Alpine.store('selection', {
                ids: new Set(),
                has(id){return this.ids.has(id);},
                toggle(id){this.ids.has(id)?this.ids.delete(id):this.ids.add(id);},
                clear(){this.ids.clear();},
                get count(){return this.ids.size;},
                get csv(){return [...this.ids].join(',');},
              });
              Alpine.store('theme', {
                mode: (document.cookie.match(/trax_theme=(\w+)/)?.[1]) || 'light',
                toggle(){ this.mode = this.mode==='dark'?'light':'dark';
                  document.cookie='trax_theme='+this.mode+';path=/;max-age=31536000'; },
              });
            });
          </script>

          <header class="topbar">
            <div class="title-wrap">
              <h1>Work Queue</h1>
              <span id="live-count"
                    hx-get="{{prefix}}/work-queue/live-count"
                    hx-trigger="load, every 5s, refresh"
                    hx-sync="this:replace"></span>
            </div>
            <div class="title-wrap">
              <a class="btn btn--ghost" href="{{prefix}}/dead-letters">Dead Letters →</a>
              <button class="btn btn--ghost" @click="$store.theme.toggle()"
                      x-text="$store.theme.mode === 'dark' ? '☀ Light' : '☾ Dark'"></button>
            </div>
          </header>

          <p class="lede">
            Pending and dispatched train execution requests. Queued when a manifest is due, dispatched
            to the task server for execution. <em>Phase-2 spike — 2nd page on the same template.</em>
          </p>

          <div class="toolbar">
            <template x-if="$store.selection.count > 0">
              <span class="selgroup">
                <button class="btn btn--primary"
                        hx-post="{{prefix}}/work-queue/cancel-selected"
                        hx-target="#toast-slot" hx-swap="none"
                        :hx-vals="JSON.stringify({ ids: $store.selection.csv })">
                  ✕ Cancel Selected (<span x-text="$store.selection.count"></span>)
                </button>
                <button class="btn btn--ghost" @click="$store.selection.clear()">Clear</button>
              </span>
            </template>
          </div>

          <div id="toast-slot"></div>
          <div id="grid"></div>

          <script>
            const PREFIX = '{{prefix}}';
            const COLUMNS = ['Id','TrainName','Status','Priority','ManifestId','MetadataId','CreatedAt','DispatchedAt'];

            const grid = new gridjs.Grid({
              columns: [
                { name: gridjs.html('<span title="select">◻</span>'), sort:false, width:'44px', minWidth:'44px',
                  formatter: (_, row) => {
                    const id = row.cells[1].data;
                    return gridjs.html(
                      `<input type="checkbox" class="rowcheck" data-id="${id}"
                              onchange="Alpine.store('selection').toggle(${id})"
                              ${Alpine.store('selection').has(id) ? 'checked' : ''} />`);
                  } },
                { name:'Id', width:'80px', minWidth:'60px' },
                { name:'Train', width:'200px', minWidth:'120px',
                  formatter:(c)=>gridjs.html(shortName(String(c))) },
                { name:'Status', width:'120px', minWidth:'100px',
                  formatter:(c)=>gridjs.html(`<span class="badge badge--${String(c).toLowerCase()}">${c}</span>`) },
                { name:'Priority', width:'90px', minWidth:'70px' },
                { name:'Manifest', width:'130px', minWidth:'90px' },
                { name:'Metadata', width:'130px', minWidth:'90px' },
                { name:'Created At', width:'180px', minWidth:'140px' },
                { name:'Dispatched At', width:'180px', minWidth:'140px' },
              ],
              server: {
                url: `${PREFIX}/work-queue/data`,
                then: (res) => res.data.map(d => [
                  null, d.id, d.trainName, d.status, d.priority,
                  d.manifestId ?? '—', d.metadataId ?? '—',
                  fmtDate(d.createdAt), fmtDate(d.dispatchedAt),
                ]),
                total: (res) => res.total,
              },
              pagination: { limit: 15, server: {
                url: (prev, page, limit) => `${prev}${prev.includes('?')?'&':'?'}limit=${limit}&offset=${page*limit}`,
              } },
              sort: { multiColumn:false, server: {
                url: (prev, cols) => {
                  if (!cols.length) return prev;
                  const c = cols[0]; const name = COLUMNS[c.index - 1];
                  if (!name) return prev;
                  const glue = prev.includes('?')?'&':'?';
                  return `${prev}${glue}sortColumn=${name}&sortDir=${c.direction===1?'asc':'desc'}`;
                },
              } },
              search: { server: {
                url: (prev, kw) => `${prev}${prev.includes('?')?'&':'?'}search=${encodeURIComponent(kw)}`,
              } },
              resizable: true,
              className: { table: 'trax-grid' },
            });
            grid.render(document.getElementById('grid'));

            document.body.addEventListener('workqueue:changed', () => {
              grid.forceRender();
              htmx.trigger('#live-count', 'refresh');
            });

            function fmtDate(v){ if(!v) return '—'; return new Date(v).toISOString().slice(0,19).replace('T',' '); }
            // Mirror DashboardFormatters.ShortName: last dotted segment.
            function shortName(s){ const i = s.lastIndexOf('.'); return i>=0 ? s.slice(i+1) : s; }
          </script>
        </body>
        </html>
        """;
}
