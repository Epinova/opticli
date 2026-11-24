using System.Text.Json;
using EPiServer;
using EPiServer.Commerce.SpecializedProperties;
using EPiServer.Core;
using EPiServer.ServiceLocation;
using OptiCli.Cms;
using OptiCli.Cms.Content;

namespace OptiCli.Agent.Commerce.Catalog;

/// <summary>
/// Catalog content's media list (<c>CommerceMediaCollection</c>, the item's Assets tab), which product listings and
/// default-image helpers read. Written as an array of CMS media: a ref (an id, a GUID, a plan's <c>$id</c> of uploaded
/// media), or <c>{ref, groupName, sortOrder}</c>; the array is the whole list. Shown as <c>[{ref, groupName,
/// sortOrder}]</c>, ordered by sort order, so a write of what is there already changes nothing.
/// </summary>
internal sealed class CommerceMediaConverter : IPropertyValueConverter
{
    /// <summary>What Commerce calls CMS media in an asset row (<c>typeof(IContentMedia).FullName.ToLower()</c>).</summary>
    internal const string MediaAssetType = "episerver.core.icontentmedia";

    internal const string DefaultGroup = "default";

    private const string Syntax = """an array of CMS media: ["123", {"ref": "<media ref>", "groupName": "default", "sortOrder": 1}]""";

    public bool Handles(PropertyData property) => property is PropertyCommerceMediaCollection;

    public object? FromJson(PropertyData property, JsonElement value, Func<string, ContentReference> resolve)
    {
        var items = Items(property.Name, value);
        var media = new List<CommerceMedia>();
        var seen = new Dictionary<ContentReference, string>();
        foreach (var item in items)
        {
            var link = resolve(item.Ref).ToReferenceWithoutVersion();
            // Commerce keys an item's assets by the media item: a second row of it fails only when saving.
            if (seen.TryGetValue(link, out var first))
            {
                throw AgentException.Usage(
                    $"'{property.Name}' has the media item {link} twice ('{first}' and '{item.Ref}'): an item's assets are one row per media item.",
                    "Give each media item once, in the group and sort order it should have.");
            }
            seen[link] = item.Ref;
            media.Add(new CommerceMedia(link, MediaAssetType, item.GroupName, item.SortOrder));
        }
        foreach (var (link, reference) in seen)
        {
            RequireMedia(property.Name, reference, link);
        }
        return new ItemCollection<CommerceMedia>(media.OrderBy(m => m.SortOrder));
    }

    public object? Show(object value) => value is IEnumerable<CommerceMedia> items
        ? items.OrderBy(m => m.SortOrder).Select(m => new Dictionary<string, object?>
        {
            ["ref"] = m.AssetLink is null ? null : m.AssetLink.ToReferenceWithoutVersion().ToString(),
            ["groupName"] = m.GroupName,
            ["sortOrder"] = m.SortOrder,
        }).ToList()
        : null;

    internal sealed record Item(string Ref, string GroupName, int SortOrder);

    /// <summary>The items a write gives: refs, or objects with a ref; a sort order defaults to the item's place.</summary>
    /// <exception cref="AgentException">Not an array of those.</exception>
    internal static IReadOnlyList<Item> Items(string property, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw AgentException.Usage($"'{property}' takes {Syntax}.", "The array is the whole list: [] empties it.");
        }
        var items = new List<Item>();
        var index = 0;
        foreach (var element in value.EnumerateArray())
        {
            items.Add(element.ValueKind switch
            {
                JsonValueKind.String when element.GetString() is { Length: > 0 } reference => new Item(reference, DefaultGroup, index),
                JsonValueKind.Number => new Item(element.GetRawText(), DefaultGroup, index),
                JsonValueKind.Object when Text(element, "ref") is { Length: > 0 } reference => new Item(
                    reference,
                    Text(element, "groupName") is { Length: > 0 } group ? group : DefaultGroup,
                    element.TryGetProperty("sortOrder", out var sort) && sort.ValueKind == JsonValueKind.Number ? sort.GetInt32() : index),
                _ => throw AgentException.Usage($"Item {index} of '{property}' isn't media: '{property}' takes {Syntax}."),
            });
            index++;
        }
        return items;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        } : null;

    /// <exception cref="AgentException">The ref names content that isn't media.</exception>
    private static void RequireMedia(string property, string reference, ContentReference link)
    {
        if (ServiceLocator.Current.GetInstance<IContentLoader>().TryGet<IContent>(link, out var content) && content is not IContentMedia)
        {
            throw AgentException.Usage($"'{reference}' in '{property}' is {content.GetType().BaseType?.Name ?? "content"} '{content.Name}', not media.",
                "Upload the image first (opticli upload <file> --parent <folder>) and give its ref.");
        }
    }
}
