using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptiCli.Core.Output;

/// <summary>
/// CMS 13: a Visual Builder composition (<c>get</c>'s <c>composition</c>) for a terminal, as the tree it is: sections →
/// rows → columns → elements, one line per node with its key, type and display template, and an inline node's
/// properties below it. A table would flatten it.
/// </summary>
public static class CompositionText
{
    private const int MaxValueWidth = 70;

    /// <summary>A <c>get</c> result: everything but the composition as the generic rendering has it, then the composition.</summary>
    public static string Document(JsonNode? document)
    {
        using var text = new StringWriter();
        if (document is not JsonObject obj || obj["composition"] is not JsonObject composition)
        {
            TextRenderer.Render(document, text);
            return text.ToString();
        }
        var rest = (JsonObject)obj.DeepClone();
        rest.Remove("composition");
        TextRenderer.Render(rest, text);
        text.WriteLine($"composition: {Describe(composition)}");
        Children(composition, text, 2);
        return text.ToString();
    }

    /// <summary>The root's line: its layout, culture, display template and settings.</summary>
    private static string Describe(JsonObject root)
    {
        var parts = new List<string> { Text(root["layout"]) ?? "outline" };
        if (Text(root["culture"]) is { } culture)
        {
            parts.Add($"culture {culture}");
        }
        return string.Join(", ", parts) + Style(root);
    }

    private static void Children(JsonObject entry, TextWriter writer, int indent)
    {
        foreach (var (list, nodeType) in new[] { ("sections", "section"), ("rows", "row"), ("columns", "column"), ("elements", "element"), ("nodes", null), ("unplaced", "unplaced") })
        {
            if (entry[list] is not JsonArray children)
            {
                continue;
            }
            if (children.Count == 0 && list != "unplaced")
            {
                writer.WriteLine($"{Pad(indent)}(no {list})");
            }
            foreach (var child in children.OfType<JsonObject>())
            {
                // A section-enabled block in an outline says it is a component (no rows of its own).
                Node(child, Text(child["nodeType"]) ?? nodeType ?? "node", writer, indent);
            }
        }
    }

    private static void Node(JsonObject node, string nodeType, TextWriter writer, int indent)
    {
        var line = $"{Pad(indent)}{nodeType} \"{Text(node["name"]) ?? ""}\"";
        if (Text(node["type"]) is { } type)
        {
            var how = node["inline"] is JsonValue ? "inline" : node["content"] is JsonObject content ? $"shared {Text(content["ref"])}{(Text(content["name"]) is { } name ? $" \"{name}\"" : "")}{(Text(content["status"]) is { } status ? $" {status}" : "")}" : null;
            line += $" [{type}{(how is null ? "" : ", " + how)}]";
        }
        if (Text(node["key"]) is { } key)
        {
            line += $" key {key}";
        }
        if (node["missing"] is JsonValue)
        {
            line += " (missing: its block isn't stored)";
        }
        writer.WriteLine(line + Style(node));
        if (node["properties"] is JsonObject properties)
        {
            var width = properties.Select(p => p.Key.Length).DefaultIfEmpty(0).Max();
            foreach (var (name, value) in properties)
            {
                writer.WriteLine($"{Pad(indent + 4)}{(name + ":").PadRight(width + 1)} {Value(value)}");
            }
        }
        Children(node, writer, indent + 2);
    }

    /// <summary>" template vbSection {background=dark}" when the node is styled.</summary>
    private static string Style(JsonObject node)
    {
        var template = Text(node["displayTemplate"]);
        var settings = node["displaySettings"] is JsonObject { Count: > 0 } values
            ? "{" + string.Join(", ", values.Select(s => $"{s.Key}={Text(s.Value) ?? s.Value?.ToJsonString()}")) + "}"
            : null;
        return template is null && settings is null ? "" : $"  template {template ?? "(none)"}{(settings is null ? "" : " " + settings)}";
    }

    /// <summary>A decoded property (<c>{type, value, ...}</c>) on one line: text cut, a reference by its ref and name.</summary>
    private static string Value(JsonNode? property)
    {
        var value = property is JsonObject { } wrapper && wrapper.ContainsKey("value") ? wrapper["value"] : property;
        var text = value switch
        {
            null => "(none)",
            JsonValue scalar when scalar.GetValueKind() == JsonValueKind.String => scalar.GetValue<string>().ReplaceLineEndings(" "),
            JsonValue scalar => scalar.ToJsonString(JsonOutput.Options),
            JsonObject reference when reference["ref"] is not null => $"{Text(reference["ref"])}{(Text(reference["name"]) is { } name ? $" \"{name}\"" : "")}",
            JsonArray items when items.All(i => i is JsonObject o && (o["text"] ?? o["name"]) is JsonValue) =>
                string.Join(", ", items.Select(i => Text(i!["text"] ?? i["name"]))),
            _ => value.ToJsonString(JsonOutput.Options),
        };
        if (property is JsonObject { } decoded && decoded["truncated"] is JsonValue)
        {
            text += " …";
        }
        return text.Length <= MaxValueWidth ? text : text[..(MaxValueWidth - 1)] + "…";
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Pad(int indent) => new(' ', indent);
}
