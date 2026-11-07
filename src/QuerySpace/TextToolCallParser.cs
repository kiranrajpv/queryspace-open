using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuerySpace;

internal static partial class TextToolCallParser
{
    public static bool TryParse(
        string text,
        IReadOnlyList<AgentToolDefinition> definitions,
        out List<AgentToolCall> calls)
    {
        calls = [];
        if (string.IsNullOrWhiteSpace(text)) return false;

        var allowed = definitions.Select(definition => definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();
        candidates.AddRange(ToolTag().Matches(text).Select(match => match.Groups[1].Value));
        candidates.AddRange(JsonFence().Matches(text).Select(match => match.Groups[1].Value));
        if (candidates.Count == 0)
        {
            var trimmed = text.Trim();
            if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
                (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
                candidates.Add(trimmed);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                AddElement(document.RootElement, allowed, calls);
            }
            catch (JsonException)
            {
                // The model may emit prose or incomplete JSON; only complete registered calls are translated.
            }
        }

        return calls.Count > 0;
    }

    public static bool IsTranslated(AgentToolCall call) =>
        call.Id.StartsWith("text_call_", StringComparison.Ordinal);

    private static void AddElement(JsonElement element, HashSet<string> allowed, List<AgentToolCall> calls)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) AddElement(item, allowed, calls);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        var callObject = element;
        if (element.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            callObject = function;
        if (!TryString(callObject, "name", out var name) || !allowed.Contains(name)) return;

        var arguments = "{}";
        if (callObject.TryGetProperty("arguments", out var argumentElement) ||
            callObject.TryGetProperty("parameters", out argumentElement))
        {
            arguments = argumentElement.ValueKind == JsonValueKind.String
                ? argumentElement.GetString() ?? "{}"
                : argumentElement.GetRawText();
        }
        calls.Add(new AgentToolCall($"text_call_{Guid.NewGuid():N}", name, arguments));
    }

    private static bool TryString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    [GeneratedRegex(@"<tool_call>\s*([\s\S]*?)\s*</tool_call>", RegexOptions.IgnoreCase)]
    private static partial Regex ToolTag();

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase)]
    private static partial Regex JsonFence();
}
