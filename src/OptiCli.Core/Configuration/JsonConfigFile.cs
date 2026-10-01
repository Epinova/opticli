using System.Globalization;
using System.Text.Json;

namespace OptiCli.Core.Configuration;

/// <summary>
/// Reads the JSON files ASP.NET Core reads (appsettings, user secrets, launchSettings) with the same
/// leniency: UTF-8 BOM, <c>//</c> and <c>/* */</c> comments, trailing commas.
/// </summary>
public static class JsonConfigFile
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <exception cref="JsonException">The file is not valid JSON.</exception>
    public static JsonDocument Parse(string path) =>
        // ReadAllText detects and drops a BOM, which JsonDocument would otherwise reject.
        JsonDocument.Parse(File.ReadAllText(path), Options);

    /// <summary>
    /// Flattens to configuration keys (<c>ConnectionStrings:EPiServerDB</c>), case-insensitive like
    /// IConfiguration. Keys that already contain ':' (common in secrets.json) are kept as-is. Values come out as
    /// ASP.NET Core's JSON provider gives them: <c>True</c>/<c>False</c> for booleans, <c>""</c> for JSON null, and an
    /// empty object or array sets its key to null.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Flatten(JsonElement root)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Visit(root, null, result);
        return result;
    }

    private static void Visit(JsonElement element, string? prefix, Dictionary<string, string?> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var empty = true;
                foreach (var property in element.EnumerateObject())
                {
                    empty = false;
                    Visit(property.Value, prefix is null ? property.Name : $"{prefix}:{property.Name}", result);
                }
                if (empty && prefix is not null)
                {
                    result[prefix] = null;
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item, $"{prefix}:{index.ToString(CultureInfo.InvariantCulture)}", result);
                    index++;
                }
                if (index == 0 && prefix is not null)
                {
                    result[prefix] = null;
                }
                break;
            default:
                if (prefix is not null)
                {
                    // JsonElement.ToString(): a string's value, "True"/"False", a number as written, "" for null.
                    result[prefix] = element.ToString();
                }
                break;
        }
    }
}
