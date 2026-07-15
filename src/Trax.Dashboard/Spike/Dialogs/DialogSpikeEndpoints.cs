using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Trax.Dashboard.Spike.Dialogs;

/// <summary>
/// PHASE-2 SPIKE — the reflection-driven dialog (Run Train), rebuilt on htmx + Alpine.
///
/// Proves the last genuinely-different surface from the plan: a modal whose form is generated from
/// a Type's shape via reflection. In the Radzen version this was a DialogService modal; here:
///   • Alpine owns the modal open/close (a signal),
///   • htmx GETs the reflected form fragment into the modal body,
///   • the form POSTs; the server reassembles + coerces values back into the input Type
///     (ReflectionForm.BuildFromForm — a near-verbatim port of the Radzen logic),
///   • success closes the modal (HX-Trigger) and shows the round-tripped object.
///
/// Two sample input types stand in for real TrainRegistration.InputType values, exercising string /
/// int / bool / enum / nullable / DateTime coercion.
/// </summary>
internal static class DialogSpikeEndpoints
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    // Stand-ins for real train input types. Keyed by a short id used in the route.
    internal static readonly Dictionary<string, Type> SampleInputs = new()
    {
        ["invoice"] = typeof(GenerateInvoiceInput),
        ["reminder"] = typeof(SendReminderInput),
    };

    public static IEndpointRouteBuilder MapDialogSpike(
        this IEndpointRouteBuilder app,
        string routePrefix = "/trax-spike"
    )
    {
        routePrefix = "/" + routePrefix.Trim('/');
        var group = app.MapGroup(routePrefix);

        group.MapGet("/dialogs", () => Results.Content(DialogSpikePages.Page(routePrefix), "text/html"));

        // htmx GETs the reflected modal form for a given input type.
        group.MapGet("/dialogs/run/{key}", (string key) =>
        {
            if (!SampleInputs.TryGetValue(key, out var type))
                return Results.NotFound($"Unknown input '{key}'.");
            return Results.Content(DialogSpikePages.Modal(routePrefix, key, type), "text/html");
        });

        // Submit: reassemble + coerce → report the round-tripped object (stand-in for enqueue).
        group.MapPost("/dialogs/run/{key}", async (string key, HttpContext ctx) =>
        {
            if (!SampleInputs.TryGetValue(key, out var type))
                return Results.NotFound($"Unknown input '{key}'.");

            var form = await ctx.Request.ReadFormAsync();
            var posted = form.Keys.ToDictionary(k => k, k => (string?)form[k].ToString());

            try
            {
                var input = ReflectionForm.BuildFromForm(type, posted);
                if (input is null)
                    return Result(false, $"Deserialization returned null for {type.Name}.", null);

                // In the real dialog this is: Track(metadata) + SaveChanges + JobSubmitter.EnqueueAsync.
                // Here we just echo the reconstructed object to prove the round-trip.
                var json = JsonSerializer.Serialize(input, type, Pretty);
                return Result(true, $"Enqueued {type.Name} (round-tripped below).", json);
            }
            catch (Exception ex)
            {
                return Result(false, ex.Message, null);
            }
        });

        return app;
    }

    // On success: HX-Trigger closes the modal (Alpine listens) + swaps a result panel.
    private static IResult Result(bool ok, string message, string? json)
    {
        var kind = ok ? "toast--ok" : "toast--err";
        var body = json is null
            ? ""
            : $"<pre class=\"result-json\">{SpikeHtmxEscape(json)}</pre>";
        var html =
            "<div id=\"dialog-result\" hx-swap-oob=\"innerHTML\">" +
            $"<div class=\"toast {kind}\">{SpikeHtmxEscape(message)}</div>{body}</div>";
        var res = new TriggeringResult(html, ok ? "dialog:done" : null);
        return res;
    }

    private static string SpikeHtmxEscape(string s) => System.Net.WebUtility.HtmlEncode(s);

    private sealed class TriggeringResult(string html, string? trigger) : IResult
    {
        public async Task ExecuteAsync(HttpContext ctx)
        {
            if (trigger is not null) ctx.Response.Headers["HX-Trigger"] = trigger;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(html);
        }
    }
}

// ---- Sample input types (stand in for real train inputs; exercise varied property kinds) ----

internal enum InvoiceFormat { Pdf, Html, Csv }

internal sealed class GenerateInvoiceInput
{
    public string CustomerId { get; set; } = "";
    public int AmountCents { get; set; }
    public InvoiceFormat Format { get; set; }
    public bool SendEmail { get; set; }
    public DateTime? DueDate { get; set; }
}

internal sealed class SendReminderInput
{
    public string Recipient { get; set; } = "";
    public int RetryCount { get; set; }
    public bool Urgent { get; set; }
}
