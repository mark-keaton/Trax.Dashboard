namespace Trax.Dashboard.Spike.Dialogs;

/// <summary>
/// Renders the dialog spike page + the reflected modal fragment.
///
/// THE DIALOG htmx/Alpine BOUNDARY:
///   • Alpine owns the modal's open/close state (a component-local `open` flag).
///   • htmx GETs the reflected form into the modal body and POSTs the submit.
///   • On success the server sends HX-Trigger "dialog:done"; a listener flips Alpine `open=false`
///     and the result panel is swapped in via hx-swap-oob. No Blazor circuit, no DialogService.
/// </summary>
internal static class DialogSpikePages
{
    public static string Page(string prefix) => $$"""
        <!DOCTYPE html>
        <html lang="en" x-data :data-theme="$store.theme.mode">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <title>Dialogs — htmx spike</title>
          <script src="https://unpkg.com/htmx.org@2.0.4"></script>
          <script defer src="https://unpkg.com/alpinejs@3.14.8/dist/cdn.min.js"></script>
          <link rel="stylesheet" href="{{prefix}}/assets/spike.css" />
        </head>
        <body>
          <script>
            document.addEventListener('alpine:init', () => {
              Alpine.store('theme', {
                mode: (document.cookie.match(/trax_theme=(\w+)/)?.[1]) || 'light',
                toggle(){ this.mode = this.mode==='dark'?'light':'dark';
                  document.cookie='trax_theme='+this.mode+';path=/;max-age=31536000'; },
              });
            });
          </script>

          <header class="topbar">
            <h1>Run Train</h1>
            <div class="title-wrap">
              <a class="btn btn--ghost" href="{{prefix}}/dead-letters">Dead Letters →</a>
              <button class="btn btn--ghost" @click="$store.theme.toggle()"
                      x-text="$store.theme.mode === 'dark' ? '☀ Light' : '☾ Dark'"></button>
            </div>
          </header>

          <p class="lede">
            Enqueue a train with input built from a reflected form. <em>Phase-2 spike — the
            reflection-driven dialog, rebuilt on htmx + Alpine (no DialogService).</em>
          </p>

          <!-- One button per sample input type. Each opens the modal and htmx-loads its reflected form. -->
          <div class="toolbar"
               x-data="{ open:false }"
               @dialog:done.window="open=false">
            <button class="btn btn--primary"
                    @click="open=true"
                    hx-get="{{prefix}}/dialogs/run/invoice"
                    hx-target="#modal-body" hx-swap="innerHTML">▶ Run GenerateInvoice…</button>
            <button class="btn btn--primary"
                    @click="open=true"
                    hx-get="{{prefix}}/dialogs/run/reminder"
                    hx-target="#modal-body" hx-swap="innerHTML">▶ Run SendReminder…</button>

            <!-- MODAL — Alpine owns open/close; htmx fills #modal-body. -->
            <div class="modal-backdrop" x-show="open" x-cloak @click.self="open=false">
              <div class="modal">
                <div class="modal-head">
                  <strong>Run Train</strong>
                  <button class="btn btn--ghost" @click="open=false">✕</button>
                </div>
                <div id="modal-body"><!-- reflected form swapped in here --></div>
              </div>
            </div>
          </div>

          <!-- Result panel: the round-tripped object after a successful submit. -->
          <div id="dialog-result"></div>
        </body>
        </html>
        """;

    /// <summary>The reflected form fragment for one input type.</summary>
    public static string Modal(string prefix, string key, Type inputType) => $$"""
        <form hx-post="{{prefix}}/dialogs/run/{{key}}"
              hx-target="#dialog-result" hx-swap="none">
          <p class="modal-type">Input: <code>{{Esc(inputType.Name)}}</code></p>
          {{ReflectionForm.RenderFields(inputType)}}
          <div class="modal-actions">
            <button class="btn btn--ok" type="submit">Enqueue</button>
          </div>
        </form>
        """;

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
}
