using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OptiCli.Core.Output;

public static class JsonOutput
{
    /// <summary>camelCase, enums as camelCase strings, nulls omitted, compact, non-ASCII kept readable.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // Output goes to terminals and agents, never into HTML, so escaping æ, ø, < and > only costs bytes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);

    public static JsonNode? ToNode(object? value) => JsonSerializer.SerializeToNode(value, Options);
}
