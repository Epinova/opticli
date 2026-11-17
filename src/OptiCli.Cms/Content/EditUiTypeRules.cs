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
/// with a UIHint and without <c>[AllowedTypes]</c>. The content's own properties are checked, not those of the blocks in
/// it (a local block's, an inline block's).
/// </remarks>
internal static class EditUiTypeRules
{
    /// <param name="before">The starting point's snapshot (<see cref="PropertyValues.Snapshot"/>): what the content holds already.</param>
    public static IEnumerable<ValidationIssue> Check(CmsCall call, IContent content, IReadOnlyDictionary<string, JsonElement?> before)
    {
        if (call.OptionalService<IEditUiMetadata>() is not { } metadata
            || call.Service<IContentTypeRepository>() is not { } types
            || types.Load(content.ContentTypeID) is not { } type
            || metadata.Types(type) is not { Count: > 0 } rules)
        {
            return [];
        }
        var issues = new List<ValidationIssue>();
        var model = type.ModelType;
        foreach (var (name, rule) in rules)
        {
            if (content.Property[name] is not { } property || !SetByHint(model, type.PropertyDefinitions.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), name))
            {
                continue;
            }
            var had = Keys(before.GetValueOrDefault(name));
            foreach (var (key, item) in Items(call, property.Value))
            {
                // A key the property held already is kept, as often as it held it.
                if (had.Remove(key) || item is null || Allows(rule, item))
                {
                    continue;
                }
                issues.Add(new ValidationIssue(name,
                    $"'{name}' doesn't take {NameOf(types, item)} in the CMS edit UI; it takes {string.Join(", ", rule.Allowed.Select(a => NameOf(types, a)))}"
                    + (rule.Restricted.Count > 0 ? $", but not {string.Join(", ", rule.Restricted.Select(r => NameOf(types, r)))}" : "")
                    + ". get_content_type gives a property's allowedTypes."));
            }
        }
        return issues;
    }

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
    private static IEnumerable<(string Key, Type? Model)> Items(CmsCall call, object? value)
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
