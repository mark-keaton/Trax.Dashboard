using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Trax.Dashboard.Spike.Dialogs;

/// <summary>
/// PHASE-2 SPIKE — the reflection-driven dynamic form, lifted out of the Radzen RunTrainDialog into
/// framework-agnostic C#. This is the whole point of the dialog spike: the hard part (build a form
/// from a Type's shape, then reassemble + coerce posted values back into that Type) is PURE server
/// code — it never depended on Blazor. htmx just swaps the rendered fragment; the logic below is a
/// near-verbatim port of BuildInputFromForm / ToJsonNode / FormatLabel / GetPlaceholder.
/// </summary>
internal static partial class ReflectionForm
{
    /// <summary>Renders the input Type's public readable properties as HTML form fields.</summary>
    public static string RenderFields(Type inputType)
    {
        var sb = new StringBuilder();
        foreach (var prop in ReadableProps(inputType))
        {
            var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var label = Escape(FormatLabel(prop.Name));
            var name = Escape(prop.Name);

            sb.Append("<div class=\"field\">");
            if (underlying == typeof(bool))
            {
                // bool → checkbox (posts "true" when checked; absent otherwise)
                sb.Append($"<label class=\"check\"><input type=\"checkbox\" name=\"{name}\" value=\"true\" /> {label}</label>");
            }
            else if (underlying.IsEnum)
            {
                // enum → dropdown of Enum.GetNames
                sb.Append($"<label>{label}</label><select name=\"{name}\">");
                foreach (var opt in Enum.GetNames(underlying))
                    sb.Append($"<option value=\"{Escape(opt)}\">{Escape(opt)}</option>");
                sb.Append("</select>");
            }
            else
            {
                var ph = Escape(GetPlaceholder(underlying));
                sb.Append($"<label>{label}</label><input type=\"text\" name=\"{name}\" placeholder=\"{ph}\" />");
            }
            sb.Append("</div>");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reassembles posted form values into a JsonObject with the same type coercion the Radzen dialog
    /// used, then deserializes to <paramref name="inputType"/>. Returns the object (or throws on bad input).
    /// </summary>
    public static object? BuildFromForm(Type inputType, IReadOnlyDictionary<string, string?> posted)
    {
        var jsonObj = new JsonObject();
        foreach (var prop in ReadableProps(inputType))
        {
            // A missing checkbox key means false; a present one (value "true") means true.
            var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            string? raw = posted.TryGetValue(prop.Name, out var v) ? v : null;
            if (underlying == typeof(bool))
                raw = raw is not null ? "true" : "false";
            jsonObj[prop.Name] = ToJsonNode(raw, prop.PropertyType);
        }

        // NOTE: the form renders enums as their NAME (e.g. "Csv"), so deserialization needs a string
        // enum converter. The current Radzen RunTrainDialog omits this — a latent bug that surfaces
        // the moment a train input has an enum property submitted via the form tab. The spike fixes it.
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        return JsonSerializer.Deserialize(jsonObj.ToJsonString(), inputType, opts);
    }

    private static PropertyInfo[] ReadableProps(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead).ToArray();

    // ---- ported verbatim from RunTrainDialog.razor.cs ----

    private static JsonNode? ToJsonNode(object? value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value is bool b) return JsonValue.Create(b);
        if (value is not string s || string.IsNullOrEmpty(s))
            return Nullable.GetUnderlyingType(targetType) is not null ? null : ToDefault(underlying);

        if (underlying == typeof(string)) return JsonValue.Create(s);
        if (underlying.IsEnum) return JsonValue.Create(s);
        if (underlying == typeof(int) && int.TryParse(s, out var i)) return JsonValue.Create(i);
        if (underlying == typeof(long) && long.TryParse(s, out var l)) return JsonValue.Create(l);
        if (underlying == typeof(double) && double.TryParse(s, out var d)) return JsonValue.Create(d);
        if (underlying == typeof(decimal) && decimal.TryParse(s, out var dec)) return JsonValue.Create(dec);
        if (underlying == typeof(float) && float.TryParse(s, out var f)) return JsonValue.Create(f);
        if (underlying == typeof(short) && short.TryParse(s, out var sh)) return JsonValue.Create(sh);
        if (underlying == typeof(byte) && byte.TryParse(s, out var by)) return JsonValue.Create(by);
        if (underlying == typeof(Guid) && Guid.TryParse(s, out var g)) return JsonValue.Create(g);
        if (underlying == typeof(DateTime) && DateTime.TryParse(s, out var dt)) return JsonValue.Create(dt);
        if (underlying == typeof(DateTimeOffset) && DateTimeOffset.TryParse(s, out var dto)) return JsonValue.Create(dto);
        if (underlying == typeof(bool) && bool.TryParse(s, out var bo)) return JsonValue.Create(bo);

        try { return JsonNode.Parse(s); }
        catch { return JsonValue.Create(s); }
    }

    private static JsonNode? ToDefault(Type type)
    {
        if (type == typeof(string)) return JsonValue.Create("");
        if (type == typeof(bool)) return JsonValue.Create(false);
        if (type.IsValueType) return JsonValue.Create(0);
        return null;
    }

    public static string FormatLabel(string name) => SplitCamel().Replace(name, " ");

    private static string GetPlaceholder(Type type) => type switch
    {
        _ when type == typeof(string) => "Enter text",
        _ when type == typeof(int) || type == typeof(long) || type == typeof(short) => "Enter number",
        _ when type == typeof(double) || type == typeof(float) || type == typeof(decimal) => "Enter decimal",
        _ when type == typeof(Guid) => "Enter GUID",
        _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => "yyyy-MM-dd HH:mm:ss",
        _ => $"Enter {type.Name}",
    };

    private static string Escape(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex SplitCamel();
}
