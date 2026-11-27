using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAnnotations;
using OptiCli.Cms.Compat;
using OptiCli.Protocol;

namespace OptiCli.Cms.Content;

/// <summary>
/// For an editor: the content a ContentArea or reference property takes where an editor descriptor for its UIHint sets
/// the types (<see cref="IEditUiMetadata.Types"/>), not <c>[AllowedTypes]</c>. The edit UI offers nothing else there, and
/// the CMS checks only the attribute on save, so a write that adds other content gets a validation error, as one that
/// breaks <c>[AllowedTypes]</c> does. What the property already holds may stay, as in the edit UI.
/// </summary>
/// <remarks>
/// The properties are those <c>get_content_type</c> gives such types for (<see cref="Types.ContentTypeDescriber"/>):
/// with a UIHint and without <c>[AllowedTypes]</c>. Every content data the write changes is checked against its own type:
/// the content's own properties, those of the local blocks and inline blocks in it at any depth, and the shared blocks the
/// write makes or takes over in its "For this page" folder (<see cref="SharedAreaBlocks.Pending"/>), as the edit UI's
/// form for each offers its own types. What a property of a block held already may stay: for a local block, what it held
/// in the starting point; for an inline block, what any inline block of its type held in the same place (inline blocks
/// have no identity to pair them by); for a block taken over, what its latest version holds; for a new block, nothing.
/// </remarks>
internal static class EditUiTypeRules
{
    /// <summary>How deep blocks are followed, as <see cref="SharedAreaBlocks"/> follows them.</summary>
    private const int MaxDepth = 20;

    /// <param name="before">The starting point's snapshot (<see cref="PropertyValues.Snapshot"/>): what the content holds already.</param>
    /// <param name="pending">The shared blocks the write makes or takes over (<see cref="SharedAreaBlocks.All"/>); none by default.</param>
    public static IEnumerable<ValidationIssue> Check(CmsCall call, IContent content, IReadOnlyDictionary<string, JsonElement?> before,
        IReadOnlyList<SharedAreaBlocks.Pending>? pending = null)
    {
        if (call.OptionalService<IEditUiMetadata>() is not { } metadata || call.Service<IContentTypeRepository>() is not { } types)
        {
            return [];
        }
        var check = new Walk(call, metadata, types, pending ?? []);
        check.Data(content, types.Load(content.ContentTypeID), [before], "", 0);
        foreach (var block in pending ?? [])
        {
            check.Data(block.Values, block.Type, block.IsNew ? [] : [PropertyValues.Snapshot(block.Existing!)], $"{block.Path}.", 0);
        }
        return check.Issues;
    }

    /// <summary>The check of one write: its issues, and each type's rules, asked once.</summary>
    /// <param name="pending">
    /// The shared blocks the write makes or takes over: each checked on its own, not as the inline block a new one shows as
    /// while validating; an item that shows a new one by its GUID holds content of its type.
    /// </param>
    private sealed class Walk(CmsCall call, IEditUiMetadata metadata, IContentTypeRepository types, IReadOnlyList<SharedAreaBlocks.Pending> pending)
    {
        private readonly Dictionary<int, IReadOnlyDictionary<string, EditUiTypes>?> _rules = [];

        private readonly HashSet<IContentData> _shared = new(pending.Select(p => p.Values), ReferenceEqualityComparer.Instance);

        public List<ValidationIssue> Issues { get; } = [];

        /// <param name="type">The data's content type; null when it can't be told (then only the blocks in it are checked).</param>
        /// <param name="before">What the data held before, at the same place: a snapshot each (none for new data).</param>
        /// <param name="prefix">Its path, ending in a dot, for the issues: <c>MainArea[2].</c>; empty for the content.</param>
        public void Data(IContentData data, ContentType? type, IReadOnlyList<IReadOnlyDictionary<string, JsonElement?>> before, string prefix, int depth)
        {
            if (depth > MaxDepth)
            {
                return;
            }
            if (type is not null && Rules(type) is { Count: > 0 } rules)
            {
                Properties(data, type, rules, before, prefix);
            }
            foreach (var property in data.Property.Where(p => !p.IsMetaData).ToList())
            {
                switch (property.Value)
                {
                    case ContentArea area:
                        var items = area.Items.ToList();
                        for (var i = 0; i < items.Count; i++)
                        {
                            if (InlineBlocks.Of(items[i]) is { } inline && !_shared.Contains(inline))
                            {
                                Data(inline, TypeOf(inline), InlineBefore(before, property.Name, PropertyValues.InlineTypeName(inline)), $"{prefix}{property.Name}[{i}].", depth + 1);
                            }
                        }
                        break;
                    case BlockData local and not IContent:
                        Data(local, TypeOf(local), [.. before.Select(b => Nested(b.GetValueOrDefault(property.Name))).OfType<IReadOnlyDictionary<string, JsonElement?>>()],
                            $"{prefix}{property.Name}.", depth + 1);
                        break;
                }
            }
        }

        private void Properties(IContentData data, ContentType type, IReadOnlyDictionary<string, EditUiTypes> rules,
            IReadOnlyList<IReadOnlyDictionary<string, JsonElement?>> before, string prefix)
        {
            var model = type.ModelType;
            foreach (var (name, rule) in rules)
            {
                if (data.Property[name] is not { } property || !SetByHint(model, type.PropertyDefinitions.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), name))
                {
                    continue;
                }
                var had = before.SelectMany(b => Keys(b.GetValueOrDefault(name))).ToList();
                var at = prefix + name;
                foreach (var (key, item) in Items(call, property.Value, pending))
                {
                    // A key the property held already is kept, as often as it held it.
                    if (had.Remove(key) || item is null || Allows(rule, item))
                    {
                        continue;
                    }
                    Issues.Add(new ValidationIssue(at,
                        $"'{at}' doesn't take {NameOf(types, item)} in the CMS edit UI; it takes {string.Join(", ", rule.Allowed.Select(a => NameOf(types, a)))}"
                        + (rule.Restricted.Count > 0 ? $", but not {string.Join(", ", rule.Restricted.Select(r => NameOf(types, r)))}" : "")
                        + ". get_content_type gives a property's allowedTypes."));
                }
            }
        }

        private IReadOnlyDictionary<string, EditUiTypes>? Rules(ContentType type) =>
            _rules.TryGetValue(type.ID, out var known) ? known : _rules[type.ID] = metadata.Types(type);

        /// <summary>A block's content type: the one it names (an inline block), else its model's.</summary>
        private ContentType? TypeOf(BlockData block) =>
            InlineBlocks.TypeId(block) is var id and > 0 ? types.Load(id) : types.Load(EPiServer.RuntimeModelExtensions.GetOriginalType(block));
    }

    /// <summary>
    /// What the inline blocks of <paramref name="type"/> in <paramref name="area"/> held, in each of <paramref name="before"/>:
    /// a snapshot of each one's values (<see cref="PropertyValues"/> gives an inline item as <c>{type, properties}</c>).
    /// </summary>
    private static List<IReadOnlyDictionary<string, JsonElement?>> InlineBefore(IReadOnlyList<IReadOnlyDictionary<string, JsonElement?>> before, string area, string type) =>
        [.. before.Select(b => b.GetValueOrDefault(area))
            .Where(v => v is { ValueKind: JsonValueKind.Array })
            .SelectMany(v => v!.Value.EnumerateArray())
            .Where(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("type", out var t) && string.Equals(t.GetString(), type, StringComparison.OrdinalIgnoreCase)
                && i.TryGetProperty("properties", out _))
            .Select(i => Nested(i.GetProperty("properties")))
            .OfType<IReadOnlyDictionary<string, JsonElement?>>()];

    /// <summary>A snapshot's nested object (a local block's values) as a snapshot; null for anything else.</summary>
    private static IReadOnlyDictionary<string, JsonElement?>? Nested(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Object } values
            ? values.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (JsonElement?)g.First().Value, StringComparer.OrdinalIgnoreCase)
            : null;

    /// <summary>
    /// Whether the edit UI lets a property with <paramref name="rule"/> hold content of <paramref name="model"/>: one of
    /// its classes or interfaces is allowed (any, when none are named), and none is restricted.
    /// </summary>
    public static bool Allows(EditUiTypes rule, Type model)
    {
        var names = Hierarchy(model).Select(t => t.FullName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (rule.Allowed.Count == 0 || rule.Allowed.Any(names.Contains)) && !rule.Restricted.Any(names.Contains);
    }

    /// <summary>Whether an editor descriptor for the property's UIHint decides its types: it has a hint and no <c>[AllowedTypes]</c>.</summary>
    private static bool SetByHint(Type? model, PropertyDefinition? definition, string name)
    {
        var attributes = model?.GetProperty(name)?.GetCustomAttributes(inherit: true) ?? [];
        return !attributes.OfType<AllowedTypesAttribute>().Any()
            && (attributes.OfType<UIHintAttribute>().Any(h => !string.IsNullOrWhiteSpace(h.UIHint)) || !string.IsNullOrWhiteSpace(definition?.EditorHint));
    }

    /// <summary>
    /// The content a value holds, each with the key it has in a snapshot and its model type (null when it can't be
    /// loaded: other checks refuse a reference that leads nowhere).
    /// </summary>
    /// <param name="pending">The shared blocks the write makes, which an item shows by GUID until they are saved.</param>
    private static IEnumerable<(string Key, Type? Model)> Items(CmsCall call, object? value, IReadOnlyList<SharedAreaBlocks.Pending> pending)
    {
        switch (value)
        {
            case ContentArea area:
                foreach (var item in area.Items)
                {
                    if (InlineBlocks.Of(item) is { } inline)
                    {
                        yield return (InlineKey(PropertyValues.InlineTypeName(inline)), EPiServer.RuntimeModelExtensions.GetOriginalType(inline));
                    }
                    else if (!ContentReference.IsNullOrEmpty(item.ContentLink))
                    {
                        yield return (RefKey(item.ContentLink), ModelOf(call, item.ContentLink));
                    }
                    else if (item.ContentGuid != Guid.Empty && pending.FirstOrDefault(p => p.IsNew && p.Guid == item.ContentGuid) is { } made)
                    {
                        // A new block, never held before.
                        yield return ($"new {item.ContentGuid}", made.Type.ModelType);
                    }
                }
                break;
            case ContentReference reference when !ContentReference.IsNullOrEmpty(reference):
                yield return (RefKey(reference), ModelOf(call, reference));
                break;
            case IEnumerable<ContentReference> references:
                foreach (var reference in references.Where(r => !ContentReference.IsNullOrEmpty(r)))
                {
                    yield return (RefKey(reference), ModelOf(call, reference));
                }
                break;
        }
    }

    /// <summary>The keys of what a snapshot's value holds (<see cref="PropertyValues.Snapshot"/>): refs, and inline blocks by type.</summary>
    private static List<string> Keys(JsonElement? value)
    {
        var keys = new List<string>();
        switch (value)
        {
            case { ValueKind: JsonValueKind.String } single when ContentReference.TryParse(single.GetString(), out var reference):
                keys.Add(RefKey(reference));
                break;
            case { ValueKind: JsonValueKind.Array } list:
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && ContentReference.TryParse(item.GetString(), out var listed))
                    {
                        keys.Add(RefKey(listed));
                    }
                    else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("ref", out var link) && ContentReference.TryParse(link.GetString(), out var held))
                    {
                        keys.Add(RefKey(held));
                    }
                    else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out var inlineType) && inlineType.GetString() is { } typeName)
                    {
                        keys.Add(InlineKey(typeName));
                    }
                }
                break;
        }
        return keys;
    }

    private static string RefKey(ContentReference reference) => reference.ToReferenceWithoutVersion().ToString();

    private static string InlineKey(string type) => "inline " + type.ToLowerInvariant();

    private static Type? ModelOf(CmsCall call, ContentReference reference) =>
        call.Service<IContentLoader>().TryGet<IContent>(reference.ToReferenceWithoutVersion(), new LoaderOptions { LanguageLoaderOption.FallbackWithMaster() }, out var content)
            ? EPiServer.RuntimeModelExtensions.GetOriginalType(content)
            : null;

    private static IEnumerable<Type> Hierarchy(Type model)
    {
        for (var type = model; type is not null; type = type.BaseType)
        {
            yield return type;
        }
        foreach (var contract in model.GetInterfaces())
        {
            yield return contract;
        }
    }

    private static string NameOf(IContentTypeRepository types, Type model) => types.Load(model)?.Name ?? model.Name;

    /// <summary>A type the metadata names by its lowercase full name: a content type's name, else the last part of it.</summary>
    private static string NameOf(IContentTypeRepository types, string fullName) =>
        types.List().FirstOrDefault(t => string.Equals(t.ModelType?.FullName, fullName, StringComparison.OrdinalIgnoreCase))?.Name
        ?? fullName[(fullName.LastIndexOfAny(['.', '+']) + 1)..];
}
