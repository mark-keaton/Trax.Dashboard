using Microsoft.AspNetCore.Http;

namespace Trax.Dashboard.Spike;

/// <summary>
/// Shared htmx helpers for the spike pages. Extracted when the SECOND page (Work Queue) needed the
/// exact same toast + HX-Trigger convention as the first (Dead Letters) — i.e. this is the part of
/// the pattern that generalizes across every data page.
/// </summary>
internal static class SpikeHtmx
{
    /// <summary>
    /// Returns an out-of-band toast fragment plus (optionally) an <c>HX-Trigger</c> header that tells
    /// the page's grid to reload. Same convention every batch endpoint uses.
    /// </summary>
    public static IResult Toast(bool ok, string label, string message, string? trigger)
    {
        var kind = ok ? "toast--ok" : "toast--err";
        var html =
            "<div id=\"toast-slot\" hx-swap-oob=\"innerHTML\">" +
            $"<div class=\"toast {kind}\" x-data=\"{{show:true}}\" x-show=\"show\" x-init=\"setTimeout(() => show=false, 4000)\">" +
            $"<strong>{Escape(label)}</strong> {Escape(message)}</div></div>";
        return new TriggeringHtml(html, ok ? trigger : null);
    }

    public static string Escape(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

    private sealed class TriggeringHtml(string html, string? trigger) : IResult
    {
        public async Task ExecuteAsync(HttpContext ctx)
        {
            if (!string.IsNullOrEmpty(trigger))
                ctx.Response.Headers["HX-Trigger"] = trigger;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(html);
        }
    }
}
