using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptiCli.Core.Properties;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Export;

/// <summary>
/// Turns values as <c>get</c> shows them (<c>{type, value}</c>, references as identities) into the plain values a plan's
/// <c>properties</c> take in another database: references by GUID, or <c>$id</c> when the plan creates the content; rich
/// text with permanent links and blocks by GUID (<c>data-contentlink</c>, an id, means other content there); links without
/// what <c>get</c> adds for reading.
/// </summary>
/// <param name="reference">
/// What a reference to content becomes: <c>$id</c> for content the plan creates, else the GUID. It also learns which
/// content a value refers to (the plan builder orders by it and holds back forward references).
/// </param>
/// <param name="where">Prefix for <see cref="Dropped"/> entries, e.g. the item's GUID and language.</param>
internal sealed partial class ExportValues(Func<Guid, string> reference)
{
    /// <summary>Values that can't be written to another database, left out: property path and why.</summary>
    public List<(string Property, string Reason)> Dropped { get; } = [];

    /// <summary>Visitor group (or role) ids that personalized values show to.</summary>
    public HashSet<string> VisitorGroups { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>References kept as they are: content providers' (<c>id__provider</c>), which no GUID names.</summary>
    public List<string> ProviderRefs { get; } = [];

    /// <summary>Wrapped top-level (or block) properties, each as a plan value; dropped ones are left out.</summary>
    public JsonObject Properties(JsonObject wrapped, string prefix = "")
    {
        var result = new JsonObject();
        foreach (var (name, value) in wrapped)
        {
            if (Wrapped(value, $"{prefix}{name}", out var plain))
            {
                result[name] = plain;
            }
        }
        return result;
    }

    /// <summary>One wrapped value (<c>{type, value, ...}</c>) as a plan value; false when it can't be written.</summary>
    public bool Wrapped(JsonNode? wrapped, string path, out JsonNode? plain)
    {
        plain = null;
        if (wrapped is not JsonObject property || !property.ContainsKey("value"))
        {
            // Not get's shape: written as it is.
            plain = wrapped?.DeepClone();
            return true;
        }
        var value = property["value"];
        if (value is null)
        {
            // Empty in the source: cleared in the target too.
            return true;
        }
        var type = (string?)property["type"];
        try
        {
            plain = type switch
            {
                "ContentReference" or "PageReference" => Reference(value, path),
                "ContentReferenceList" => value is JsonArray list ? new JsonArray(list.Select(item => Reference(item, path)).ToArray()) : value.DeepClone(),
                "ContentArea" => value is JsonArray items ? Area(items, path) : value.DeepClone(),
                "Block" => value is JsonObject block ? Properties(block, $"{path}.") : value.DeepClone(),
                "BlockList" => value is JsonArray blocks
                    ? new JsonArray(blocks.Select((item, i) => item is JsonObject block ? (JsonNode)Properties(block, $"{path}[{i}].") : item?.DeepClone()).ToArray())
                    : value.DeepClone(),
                "XhtmlString" => value is JsonValue text && text.TryGetValue<string>(out var html) ? JsonValue.Create(RichText(html, property, path)) : value.DeepClone(),
                "LinkCollection" or "LinkItemCollection" => value is JsonArray links ? new JsonArray(links.Select(l => Link(l)).ToArray()) : value.DeepClone(),
                "LinkItem" => value is JsonObject link ? Link(link) : Text(value),
                _ => value is JsonObject identity && IsIdentity(identity) ? Reference(identity, path) : Text(value),
            };
            return true;
        }
        catch (DroppedValue dropped)
        {
            Dropped.Add((path, dropped.Message));
            return false;
        }
    }

    /// <summary>
    /// A Visual Builder composition as <c>get</c> shows it, as <c>composition</c> in a plan's values: every node with its
    /// key (so a second run finds it), shared blocks by reference, inline blocks' values plain. What <c>get</c> adds for
    /// reading (<c>layout</c>, <c>culture</c>, <c>inline</c>) is left out; nodes whose block isn't stored are dropped.
    /// </summary>
    public JsonObject Composition(JsonObject composition, string path)
    {
        var result = new JsonObject();
        foreach (var (key, value) in composition)
        {
            switch (key)
            {
                case "layout" or "culture" or "inline":
                    break;
                case "unplaced":
                    if (value is JsonArray { Count: > 0 } unplaced)
                    {
                        Dropped.Add(($"{path}.unplaced", $"{unplaced.Count} stored item(s) that no node shows can't be written back"));
                    }
                    break;
                case "sections" or "rows" or "columns" or "elements" or "nodes":
                    var nodes = new JsonArray();
                    var i = 0;
                    foreach (var node in value as JsonArray ?? [])
                    {
                        var nodePath = $"{path}.{key}[{i++}]";
                        if (node is not JsonObject child)
                        {
                            continue;
                        }
                        if (child["missing"] is not null)
                        {
                            Dropped.Add((nodePath, "a node whose block isn't stored (missing)"));
                            continue;
                        }
                        nodes.Add(Composition(child, nodePath));
                    }
                    result[key] = nodes;
                    break;
                case "content":
                    // A shared block, as get shows it: placed by reference.
                    result["ref"] = Reference(value, path);
                    break;
                case "properties" when value is JsonObject properties:
                    result[key] = Properties(properties, $"{path}.properties.");
                    break;
                default:
                    result[key] = value?.DeepClone();
                    break;
            }
        }
        return result;
    }

    /// <summary>A reference: the identity <c>get</c> shows (or a GUID), as the plan writes it.</summary>
    /// <exception cref="DroppedValue">One without a GUID: an id that names no content.</exception>
    public JsonNode Reference(JsonNode? value, string path)
    {
        if (value is JsonObject identity)
        {
            if (identity["provider"] is not null && (string?)identity["ref"] is { } providerRef)
            {
                ProviderRefs.Add(providerRef);
                return JsonValue.Create(providerRef);
            }
            if (Guid.TryParse((string?)identity["guid"], out var guid) && guid != Guid.Empty)
            {
                return JsonValue.Create(reference(guid));
            }
            throw new DroppedValue($"a reference to {(string?)identity["ref"] ?? "content"} that no content has, so it has no GUID");
        }
        if (value is JsonValue text && text.TryGetValue<string>(out var raw) && Guid.TryParse(raw, out var parsed))
        {
            return JsonValue.Create(reference(parsed));
        }
        throw new DroppedValue($"'{value?.ToJsonString()}' isn't a reference opticli can write by GUID");
    }

    /// <summary>ContentArea items as <c>set</c> takes them, shared ones by reference and inline ones with plain values.</summary>
    private JsonArray Area(JsonArray items, string path)
    {
        var result = new JsonArray();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not JsonObject item)
            {
                continue;
            }
            var itemPath = $"{path}[{i}]";
            JsonObject written;
            if (item["inline"] is JsonValue inline && inline.GetValueKind() == JsonValueKind.True)
            {
                written = new JsonObject { ["type"] = item["type"]?.DeepClone() };
                if (item["name"] is { } name)
                {
                    written["name"] = name.DeepClone();
                }
                if (item["properties"] is JsonObject properties)
                {
                    written["properties"] = Properties(properties, $"{itemPath}.");
                }
            }
            else
            {
                written = new JsonObject { ["ref"] = Reference(item, itemPath) };
            }
            foreach (var field in (string[])["displayOption", "group", "visitorGroups"])
            {
                if (item[field] is { } kept)
                {
                    written[field] = kept.DeepClone();
                }
            }
            foreach (var group in item["visitorGroups"] as JsonArray ?? [])
            {
                if ((string?)group is { } id)
                {
                    VisitorGroups.Add(id);
                }
            }
            result.Add(written);
        }
        return result;
    }

    /// <summary>A link without the content <c>get</c> adds; a permanent link in it stays one (it names the content by GUID).</summary>
    private JsonNode? Link(JsonNode? link)
    {
        if (link is not JsonObject value)
        {
            return link?.DeepClone();
        }
        var copy = (JsonObject)value.DeepClone();
        copy.Remove("content");
        if ((string?)copy["href"] is { } href)
        {
            Links(href);
        }
        return copy;
    }

    /// <summary>A text value; a permanent link in it (a URL property) still counts as a reference.</summary>
    private JsonNode? Text(JsonNode value)
    {
        if (value is JsonValue text && text.TryGetValue<string>(out var raw))
        {
            Links(raw);
        }
        return value.DeepClone();
    }

    /// <summary>Learns the content permanent links in <paramref name="text"/> name, which stay as they are.</summary>
    private void Links(string text)
    {
        foreach (var link in PermanentLinks.Find(text))
        {
            reference(link.Guid);
        }
    }

    /// <summary>
    /// Rich text as the target takes it: a block's <c>data-contentlink</c> (an id, which the CMS trusts over the GUID and
    /// which is other content in another database) dropped, or turned into <c>data-contentguid</c> when it is all the block
    /// has; links (<c>href</c>, <c>src</c>) and blocks that name content the plan creates as <c>$id</c>.
    /// </summary>
    /// <param name="property">get's value, whose <c>blocks</c> are the identities of the text's blocks in order.</param>
    /// <exception cref="DroppedValue">An inline block in the text: its type is stored by id, which differs per database.</exception>
    private string RichText(string html, JsonObject property, string path)
    {
        var identities = property["blocks"] as JsonArray ?? [];
        foreach (var section in property["personalized"] as JsonArray ?? [])
        {
            foreach (var group in section?["visitorGroups"] as JsonArray ?? [])
            {
                if ((string?)group is { } id)
                {
                    VisitorGroups.Add(id);
                }
            }
        }

        var index = 0;
        var text = StartTag().Replace(html, tag =>
        {
            var attributes = MarkupAttributes.Parse(tag.Groups["attributes"].Value);
            if (!attributes.ContainsKey(TextRefs.ContentGuid) && !attributes.ContainsKey(TextRefs.ContentLink) && !attributes.ContainsKey("data-inlineblocktypeid"))
            {
                return tag.Value;
            }
            var identity = index < identities.Count ? identities[index] as JsonObject : null;
            index++;
            if (attributes.ContainsKey("data-inlineblocktypeid") || identity?["inline"] is JsonValue)
            {
                throw new DroppedValue("rich text with an inline block, whose type the CMS stores by id (another type in another database)");
            }
            var guid = Guid.TryParse(attributes.GetValueOrDefault(TextRefs.ContentGuid), out var given) && given != Guid.Empty ? given
                : Guid.TryParse((string?)identity?["guid"], out var resolved) && resolved != Guid.Empty ? resolved
                : throw new DroppedValue($"rich text with a block by id ({attributes.GetValueOrDefault(TextRefs.ContentLink)}) that no content has");
            var rewritten = ContentLinkAttribute().Replace(tag.Value, "");
            var value = reference(guid);
            return ContentGuidAttribute().IsMatch(rewritten)
                ? ContentGuidAttribute().Replace(rewritten, m => $"{m.Groups["name"].Value}{m.Groups["equals"].Value}{m.Groups["quote"].Value}{value}{m.Groups["quote"].Value}", 1)
                : rewritten.Insert(rewritten.Length - (rewritten.EndsWith("/>", StringComparison.Ordinal) ? 2 : 1), $" {TextRefs.ContentGuid}=\"{value}\"");
        });

        // Links to content: kept as permanent links, or $id (with what follows the link) for content the plan creates.
        text = LinkAttribute().Replace(text, link =>
        {
            if (!Guid.TryParse(link.Groups["guid"].Value, out var guid))
            {
                return link.Value;
            }
            var value = reference(guid);
            return value.StartsWith('$')
                ? $"{link.Groups["attribute"].Value}{link.Groups["equals"].Value}{link.Groups["quote"].Value}{value}{link.Groups["rest"].Value}{link.Groups["quote"].Value}"
                : link.Value;
        });
        // Anywhere else (an attribute opticli doesn't map): the content still counts.
        Links(text);
        return text;
    }

    /// <summary>Whether an object is a reference as <c>get</c> shows it (rather than a structured value).</summary>
    private static bool IsIdentity(JsonObject value) =>
        value.ContainsKey("guid") && value.All(p => p.Key is "ref" or "guid" or "type" or "name" or "language" or "status" or "url" or "kind" or "missing" or "deleted" or "blueprint" or "provider");

    /// <summary>A value that can't be written to another database; its message says why.</summary>
    public sealed class DroppedValue(string message) : Exception(message);

    [GeneratedRegex(@"<(?:div|span)\b(?<attributes>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex StartTag();

    [GeneratedRegex("""\s+data-contentlink\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'>]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ContentLinkAttribute();

    [GeneratedRegex("""(?<name>data-contentguid)(?<equals>\s*=\s*)(?<quote>["'])[^"']*\k<quote>""", RegexOptions.IgnoreCase)]
    private static partial Regex ContentGuidAttribute();

    [GeneratedRegex("""(?<attribute>\b(?:href|src))(?<equals>\s*=\s*)(?<quote>["'])~?/link/(?<guid>[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\.aspx(?<rest>[?#][^"']*)?\k<quote>""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkAttribute();
}
