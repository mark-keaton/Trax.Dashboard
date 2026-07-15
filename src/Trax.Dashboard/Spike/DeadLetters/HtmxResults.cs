using System.Text;
using Microsoft.AspNetCore.Http;

namespace Trax.Dashboard.Spike.DeadLetters;

/// <summary>
/// Tiny helper so batch endpoints can return an HTML body AND an <c>HX-Trigger</c> response header
/// in one call. htmx listens for the named event to reload the grid — this is the "server tells the
/// client what to refresh" convention the plan wants standardized in Phase 2.
/// </summary>
internal static class HtmxResultsExtensions
{
    public static IResult HtmlWithTrigger(this IResultExtensions _, string html, string? triggerEvent)
        => new HtmlWithTriggerResult(html, triggerEvent);

    private sealed class HtmlWithTriggerResult(string html, string? triggerEvent) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            if (!string.IsNullOrEmpty(triggerEvent))
                httpContext.Response.Headers["HX-Trigger"] = triggerEvent;

            httpContext.Response.ContentType = "text/html; charset=utf-8";
            await httpContext.Response.WriteAsync(html, Encoding.UTF8);
        }
    }
}
