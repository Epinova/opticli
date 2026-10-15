using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Writes;

/// <summary>
/// Property values as <c>get</c> shows them (<c>{type, value}</c> with what it adds for reading), turned back into what
/// <c>set</c> takes, so a value read with <c>get</c> can be written back as it is.
/// </summary>
public static class GetShape
{
    /// <summary>What <c>get</c> puts around a property's value.</summary>
    private static readonly HashSet<string> Decoration = new(StringComparer.Ordinal)
    {
        "type", "value", "culture", "links", "truncated", "length", "blockType", "target", "blocks", "personalized",
    };

    /// <summary>What <c>get</c> shows of referenced content.</summary>
    private static readonly HashSet<string> Identity = new(StringComparer.Ordinal)
    {
        "ref", "guid", "type", "name", "language", "status", "url", "kind", "missing", "deleted", "blueprint", "provider",
    };

    /// <summary><c>get</c>'s <c>{type, value}</c> around a property value (only what <c>get</c> adds beside them).</summary>
    public static bool IsWrapped(JsonNode? value) =>
        value is JsonObject wrapped && wrapped.ContainsKey("value") && wrapped["type"] is JsonValue type
        && type.GetValueKind() == JsonValueKind.String && wrapped.All(p => Decoration.Contains(p.Key));

    /// <summary>
    /// The values of <paramref name="properties"/> that are in <c>get</c>'s shape, unwrapped (see <see cref="Unwrap"/>);
    /// other values are kept as they are.
    /// </summary>
    /// <exception cref="UsageException">A value <c>get</c> cut short.</exception>
    public static JsonObject UnwrapAll(JsonObject properties, string prefix = "")
    {
        var result = new JsonObject();
        foreach (var (name, value) in properties)
        {
            result[name] = IsWrapped(value) ? Unwrap((JsonObject)value!, $"{prefix}{name}") : value?.DeepClone();
        }
        return result;
    }

    /// <summary>
    /// One wrapped value as <c>set</c> takes it: the value, a reference by its GUID, a link without the content <c>get</c>
    /// adds; a local block's (or block list's) own values unwrapped too; a ContentArea's items as they are (set takes them).
    /// </summary>
    /// <exception cref="UsageException">The value (or one inside it) was cut short by <c>get</c>.</exception>
    public static JsonNode? Unwrap(JsonObject wrapped, string name)
    {
        if (wrapped["truncated"] is JsonValue truncated && truncated.TryGetValue<bool>(out var cut) && cut)
        {
            throw new UsageException($"{name} was cut short by get (truncated), so writing it back would lose the rest.",
                "Read it with `opticli get <ref> --full`, or leave it out to keep it as it is.");
        }
        var value = wrapped["value"];
        return (string?)wrapped["type"] switch
        {
            "Block" when value is JsonObject block => UnwrapAll(block, $"{name}."),
            "BlockList" when value is JsonArray items => new JsonArray(items
                .Select((item, i) => item is JsonObject block ? UnwrapAll(block, $"{name}[{i}].") : item?.DeepClone()).ToArray()),
            "ContentArea" => value?.DeepClone(),
            _ => Plain(value),
        };
    }

    /// <summary>
    /// A value as <c>get</c> shows it, as <c>set</c> takes it: references by their GUID (their ref when they have none, or
    /// are a content provider's), links without the content added for reading.
    /// </summary>
    public static JsonNode? Plain(JsonNode? value)
    {
        switch (value)
        {
            case JsonObject reference when reference["ref"] is JsonValue refValue && reference.All(p => Identity.Contains(p.Key)):
                // The GUID names the same content in another database (a copy, another environment); the id may name other
                // content there, which would be written without a word.
                return Guid(reference) is { } guid ? JsonValue.Create(guid.ToString("D")) : refValue.DeepClone();
            case JsonObject link when link["href"] is not null && link.ContainsKey("content"):
                var copy = (JsonObject)link.DeepClone();
                copy.Remove("content");
                return copy;
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, item) in obj)
                {
                    result[key] = Plain(item);
                }
                return result;
            case JsonArray array:
                return new JsonArray(array.Select(Plain).ToArray());
            default:
                return value?.DeepClone();
        }
    }

    /// <summary>
    /// The GUID of a reference as <c>get</c> shows it, unless it is a content provider's (known to the CMS by its
    /// <c>id__provider</c> ref) or missing an id's GUID.
    /// </summary>
    private static Guid? Guid(JsonObject reference) =>
        reference["provider"] is null && reference["guid"] is JsonValue value && value.TryGetValue<string>(out var text)
        && System.Guid.TryParse(text, out var guid) && guid != System.Guid.Empty
            ? guid
            : null;
}
