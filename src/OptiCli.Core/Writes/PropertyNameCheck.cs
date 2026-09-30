using System.Text.Json.Nodes;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Text;

namespace OptiCli.Core.Writes;

/// <summary>
/// Checks property names against the content model in the database before anything is sent to the
/// site, so a typo fails fast with "did you mean" instead of a round trip.
/// </summary>
public static class PropertyNameCheck
{
    /// <summary>
    /// Names the agent accepts that are not property definitions: the content name, the writable built-in metadata and
    /// the publish dates (<c>StartPublish</c>, <c>StopPublish</c>, also under their page metadata names).
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltIn = ["Name", "PageURLSegment", "PageVisibleInMenu", "StartPublish", "StopPublish", "PageStartPublish", "PageStopPublish"];

    /// <exception cref="UsageException">A name is not a property of the type (or of the local block it is nested in).</exception>
    public static void Check(CmsModel model, int contentTypeId, JsonObject? properties)
    {
        if (properties is not null)
        {
            Check(model, contentTypeId, properties, prefix: "", topLevel: true);
        }
    }

    /// <summary>The ContentArea property <paramref name="name"/> of the type, with its exact name.</summary>
    /// <exception cref="UsageException">No such property, or it is not a ContentArea.</exception>
    public static PropertyDefinition RequireContentArea(CmsModel model, int contentTypeId, string name)
    {
        var definition = Find(model, contentTypeId, name, "", topLevel: false);
        return definition!.TypeName == "ContentArea"
            ? definition
            : throw new UsageException(
                $"'{definition.Name}' is a {definition.TypeName} property, not a ContentArea.",
                Hint("ContentArea properties", model.PropertiesOf(contentTypeId).Where(p => p.TypeName == "ContentArea").Select(p => p.Name), name));
    }

    private static void Check(CmsModel model, int contentTypeId, JsonObject properties, string prefix, bool topLevel)
    {
        foreach (var (name, value) in properties)
        {
            var definition = Find(model, contentTypeId, name, prefix, topLevel);
            if (value is JsonObject nested && definition?.BlockType is { } blockType)
            {
                Check(model, blockType, nested, $"{prefix}{definition.Name}.", topLevel: false);
            }
            else if (value is JsonArray items && definition is { IsList: true, BlockType: { } itemType })
            {
                // A block list: every item is an object of the block type's properties.
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i] is not JsonObject item)
                    {
                        throw new UsageException(
                            $"'{prefix}{definition.Name}[{i}]' must be an object of {model.TypeName(itemType)} property names to values.",
                            $"A block list is an array of objects, e.g. [{{\"Name\": \"...\"}}]; see `opticli type {model.TypeName(itemType)}`.");
                    }
                    Check(model, itemType, item, $"{prefix}{definition.Name}[{i}].", topLevel: false);
                }
            }
        }
    }

    /// <returns>Null for a built-in name.</returns>
    private static PropertyDefinition? Find(CmsModel model, int contentTypeId, string name, string prefix, bool topLevel)
    {
        var definitions = model.PropertiesOf(contentTypeId).ToList();
        var match = definitions.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match;
        }
        if (topLevel && BuiltIn.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var owner = model.TypeName(contentTypeId);
        throw new UsageException(
            $"'{prefix}{name}' is not a property of {owner}.",
            Hint($"{owner} properties", definitions.Select(p => p.Name), name));
    }

    private static string Hint(string what, IEnumerable<string> names, string input)
    {
        var list = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var suggestion = Suggestions.DidYouMean(input, list);
        var all = list.Count == 0 ? $"{what}: none." : $"{what}: {string.Join(", ", list)}.";
        return suggestion is null ? all : $"{suggestion} {all}";
    }
}
