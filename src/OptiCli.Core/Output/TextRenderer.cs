using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptiCli.Core.Output;

/// <summary>
/// Renders any JSON-shaped result for a terminal, so commands only produce data and never need
/// their own text formatting: arrays of objects become tables, objects become aligned
/// <c>key: value</c> lines with nested sections, arrays of scalars become bullet lists.
/// </summary>
public static class TextRenderer
{
    private const int MaxCellWidth = 60;
    private const int IndentStep = 2;
    private const string None = "(none)";

    public static void Render(JsonNode? node, TextWriter writer) => RenderNode(node, writer, 0);

    private static void RenderNode(JsonNode? node, TextWriter writer, int indent)
    {
        switch (node)
        {
            case JsonArray array:
                RenderArray(array, writer, indent);
                break;
            case JsonObject obj:
                RenderObject(obj, writer, indent);
                break;
            default:
                writer.WriteLine(Pad(indent) + Scalar(node));
                break;
        }
    }

    private static void RenderObject(JsonObject obj, TextWriter writer, int indent)
    {
        var width = obj.Where(p => IsInline(p.Value)).Select(p => p.Key.Length).DefaultIfEmpty(0).Max();

        foreach (var (key, value) in obj)
        {
            if (IsInline(value))
            {
                writer.WriteLine($"{Pad(indent)}{(key + ":").PadRight(width + 1)} {Inline(value)}");
            }
            else
            {
                writer.WriteLine($"{Pad(indent)}{key}:");
                RenderNode(value, writer, indent + IndentStep);
            }
        }
    }

    private static void RenderArray(JsonArray array, TextWriter writer, int indent)
    {
        if (array.Count == 0)
        {
            writer.WriteLine(Pad(indent) + None);
        }
        else if (array.All(item => item is JsonObject))
        {
            RenderTable(array.Cast<JsonObject>().ToList(), writer, indent);
        }
        else
        {
            foreach (var item in array)
            {
                writer.WriteLine($"{Pad(indent)}- {(item is JsonValue or null ? Scalar(item) : item.ToJsonString(JsonOutput.Options))}");
            }
        }
    }

    private static void RenderTable(IReadOnlyList<JsonObject> rows, TextWriter writer, int indent)
    {
        var columns = rows.SelectMany(r => r.Where(p => p.Value is not null).Select(p => p.Key)).Distinct().ToList();
        var cells = rows.Select(r => columns.Select(c => Truncate(Cell(r[c]))).ToArray()).ToList();
        var widths = columns.Select((c, i) => Math.Max(c.Length, cells.Max(row => row[i].Length))).ToArray();

        writer.WriteLine(Pad(indent) + Row(columns, widths));
        writer.WriteLine(Pad(indent) + string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in cells)
        {
            writer.WriteLine(Pad(indent) + Row(row, widths));
        }
    }

    private static string Row(IReadOnlyList<string> values, int[] widths) =>
        string.Join("  ", values.Select((v, i) => i == values.Count - 1 ? v : v.PadRight(widths[i]))).TrimEnd();

    /// <summary>Scalars and short scalar lists fit on the key's line; everything else gets its own section.</summary>
    private static bool IsInline(JsonNode? value) => value switch
    {
        JsonArray array => array.Count == 0 || (array.All(i => i is JsonValue) && Inline(value).Length <= MaxCellWidth),
        JsonObject obj => obj.Count == 0,
        _ => true,
    };

    private static string Inline(JsonNode? value) => value switch
    {
        JsonArray { Count: 0 } => None,
        JsonObject { Count: 0 } => None,
        JsonArray array => string.Join(", ", array.Select(Scalar)),
        _ => Scalar(value),
    };

    /// <summary>A table cell: nested lists are summarised by their names, or by count.</summary>
    private static string Cell(JsonNode? value) => value switch
    {
        null => "",
        JsonArray array when array.All(i => i is JsonValue) => string.Join(", ", array.Select(Scalar)),
        JsonArray array when array.All(i => i is JsonObject o && o["name"] is JsonValue) =>
            string.Join(", ", array.Select(i => Scalar(i!["name"]))),
        // Otherwise objects are named by their first field (e.g. search matches by property).
        JsonArray array when array.Count > 0 && array.All(i => i is JsonObject { Count: > 0 } o && o.First().Value is JsonValue) =>
            string.Join(", ", array.Select(i => Scalar(((JsonObject)i!).First().Value))),
        JsonArray array => $"[{array.Count}]",
        JsonObject obj => string.Join(" ", obj.Where(p => p.Value is JsonValue).Select(p => $"{p.Key}={Scalar(p.Value)}")),
        _ => Scalar(value),
    };

    private static string Scalar(JsonNode? value)
    {
        if (value is not JsonValue scalar)
        {
            return value is null ? "" : value.ToJsonString(JsonOutput.Options);
        }
        return scalar.GetValueKind() switch
        {
            JsonValueKind.True => "yes",
            JsonValueKind.False => "no",
            JsonValueKind.String => scalar.GetValue<string>().ReplaceLineEndings(" "),
            _ => scalar.ToJsonString(JsonOutput.Options),
        };
    }

    private static string Truncate(string value) =>
        value.Length <= MaxCellWidth ? value : value[..(MaxCellWidth - 1)] + "…";

    private static string Pad(int indent) => new(' ', indent);
}
