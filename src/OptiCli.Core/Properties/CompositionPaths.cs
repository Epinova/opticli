using System.Globalization;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Properties;

/// <summary>A section or element of a Visual Builder composition, as <c>get</c>'s composition lists it.</summary>
/// <param name="Key">Its key (the binding key), as in <c>get</c>.</param>
/// <param name="Name">Its name in the composition.</param>
/// <param name="Type">The block's content type.</param>
public sealed record CompositionNodeRef(string? Key, string? Name, string? Type);

/// <summary>Where a stored value is in a Visual Builder composition.</summary>
/// <param name="Section">The section (or section-enabled block) of the experience it is in; null for a section of its own (a blueprint).</param>
/// <param name="Element">The element it is in; null for a value of the section itself.</param>
/// <param name="Property">The property inside that element or section (<c>Body</c>, <c>Links[0]</c>), or <c>composition</c> for the placement of a shared block itself.</param>
public sealed record CompositionPlace(CompositionNodeRef? Section, CompositionNodeRef? Element, string Property);

/// <summary>
/// CMS 13: names the place of a stored value inside a Visual Builder composition (search hits, where-used rows), by the
/// sections and elements <c>get</c> shows, instead of the storage path (<c>UnstructuredData[1].UnstructuredData[0].Body</c>).
/// Reads the composition ContentAreas' markup of the owners once (<see cref="PrepareAsync"/>): their items' keys and names.
/// </summary>
public sealed class CompositionPaths(ContentSession session)
{
    /// <summary>The markup of composition ContentAreas, by owner, language, definition and scope.</summary>
    private readonly Dictionary<(int Content, int Language, int Definition, string Scope), IReadOnlyList<ContentFragment>> _markup = [];

    private readonly HashSet<(int Content, int Language)> _loaded = [];

    private CmsModel Model => session.Model;

    /// <summary>The ContentArea properties compositions bind to (<see cref="Compositions.ItemsProperty"/> of a layouted type).</summary>
    private IEnumerable<PropertyDefinition> AreaDefinitions =>
        Model.Types.Where(t => Compositions.IsLayouted(Model, t.Id))
            .SelectMany(t => Model.PropertiesOf(t.Id))
            .Where(p => p.TypeName == "ContentArea" && p.Name.Equals(Compositions.ItemsProperty, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether content of the type has a composition, so its values are placed in it.</summary>
    public bool Applies(int typeId) => Model.Schema.Compositions && Compositions.IsLayouted(Model, typeId);

    /// <summary>Reads the composition markup of these owners in these languages (and their master language), once each.</summary>
    public async Task PrepareAsync(IEnumerable<(int Content, int Language)> owners, CancellationToken cancellationToken)
    {
        var wanted = new HashSet<(int, int)>();
        foreach (var (content, language) in owners)
        {
            if (session.Identities.Header(content) is { } header && Applies(header.TypeId))
            {
                wanted.Add((content, language));
                wanted.Add((content, header.MasterLanguageId));
            }
        }
        wanted.ExceptWith(_loaded);
        var definitions = AreaDefinitions.Select(d => d.Id).Distinct().ToList();
        if (wanted.Count == 0 || definitions.Count == 0)
        {
            return;
        }
        foreach (var ids in SqlLists.Ints(wanted.Select(w => w.Item1).Distinct()))
        {
            var rows = await session.Db.QueryAsync(string.Format(CultureInfo.InvariantCulture, """
                SELECT fkContentID, fkLanguageBranchID, fkPropertyDefinitionID, ISNULL(ScopeName, '') AS ScopeName, LongString
                FROM tblContentProperty
                WHERE fkContentID IN ({0}) AND fkPropertyDefinitionID IN ({1})
                """, ids, string.Join(",", definitions)),
                r => (Content: r.GetInt32("fkContentID"), Language: r.GetInt32("fkLanguageBranchID"), Definition: r.GetInt32("fkPropertyDefinitionID"),
                    Scope: r.GetString("ScopeName"), Text: r.GetStringOrNull("LongString")),
                cancellationToken);
            foreach (var row in rows)
            {
                _markup[(row.Content, row.Language, row.Definition, row.Scope)] = ContentFragmentParser.Parse(row.Text);
            }
        }
        _loaded.UnionWith(wanted);
    }

    /// <summary>
    /// The place of a stored value of <paramref name="contentId"/> (definition and scope as stored) in its composition; null
    /// when the content has none or the value isn't in it (a property of the experience itself).
    /// </summary>
    /// <param name="target">For a reference in a composition ContentArea itself: the shared block placed there.</param>
    public CompositionPlace? Describe(int contentId, int languageId, int definitionId, string? scopeName, Guid? target = null)
    {
        if (session.Identities.Header(contentId) is not { } header || !Applies(header.TypeId))
        {
            return null;
        }
        var scope = ScopePath.Parse(scopeName);
        var steps = scope?.Steps ?? [];
        var segments = (scopeName ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        var levels = Levels(header, languageId, steps, segments, out var consumed, out var typeId);

        if (consumed == steps.Count && IsArea(typeId, definitionId))
        {
            // The value is a composition ContentArea itself: a shared block placed in it.
            var area = Fragments(header, languageId, definitionId, Prefix(segments, consumed, definitionId));
            var fragment = target is { } guid ? area?.FirstOrDefault(f => f.ContentGuid == guid) : null;
            return fragment is null && levels.Count == 0 ? null : Place(header, [.. levels, Node(fragment, null)], "composition");
        }
        if (levels.Count == 0)
        {
            return null;
        }
        var rest = steps.Skip(consumed)
            .Select(step => Name(step.PropertyId) + (step.Index is { } index ? $"[{index.ToString(CultureInfo.InvariantCulture)}]" : ""))
            .Append(Name(definitionId) + (scope?.LeafIndex is { } leaf ? $"[{leaf.ToString(CultureInfo.InvariantCulture)}]" : ""));
        return Place(header, levels, string.Join(".", rest));
    }

    /// <summary>The place of an inline block itself, by its scope as <c>tblInlineBlockUsage</c> stores it (<c>.150:26(1).142:28(0)</c>).</summary>
    public CompositionPlace? DescribeItem(int contentId, int languageId, string itemScope)
    {
        if (session.Identities.Header(contentId) is not { } header || !Applies(header.TypeId)
            // The scope names the item, without a property of it: parsed as the scope of a value in it.
            || ScopePath.Parse(itemScope.TrimEnd('.') + ".0.") is not { } scope)
        {
            return null;
        }
        var segments = itemScope.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var levels = Levels(header, languageId, scope.Steps, segments, out var consumed, out _);
        return levels.Count == 0 || consumed < scope.Steps.Count ? null : Place(header, levels, "");
    }

    /// <summary>The composition nodes the leading steps of a scope go through, and how many steps they are.</summary>
    private List<CompositionNodeRef?> Levels(ContentHeader header, int languageId, IReadOnlyList<ScopeStep> steps, string[] segments, out int consumed, out int typeId)
    {
        var levels = new List<CompositionNodeRef?>();
        typeId = header.TypeId;
        consumed = 0;
        foreach (var step in steps)
        {
            if (!IsArea(typeId, step.PropertyId) || step.Index is not { } index)
            {
                break;
            }
            var fragment = Fragments(header, languageId, step.PropertyId, Prefix(segments, consumed, step.PropertyId))?.ElementAtOrDefault(index);
            levels.Add(Node(fragment, step.InlineTypeId));
            consumed++;
            if (step.InlineTypeId is not { } inner)
            {
                break;
            }
            typeId = inner;
        }
        return levels;
    }

    /// <summary>A section of an experience, then an element; a section of its own (a blueprint) has elements only.</summary>
    private CompositionPlace Place(ContentHeader header, IReadOnlyList<CompositionNodeRef?> levels, string property)
    {
        var section = Model.Kind(header.TypeId) == ContentKind.Section ? null : levels.ElementAtOrDefault(0);
        var element = Model.Kind(header.TypeId) == ContentKind.Section ? levels.ElementAtOrDefault(0) : levels.ElementAtOrDefault(1);
        return new CompositionPlace(section, element, property.Length == 0 ? Compositions.Field : property);
    }

    private CompositionNodeRef? Node(ContentFragment? fragment, int? typeId)
    {
        if (fragment is null)
        {
            return typeId is { } id ? new CompositionNodeRef(null, null, Model.TypeName(id)) : null;
        }
        var type = fragment.InlineTypeId ?? typeId;
        return new CompositionNodeRef(
            fragment.RenderSettings.TryGetValue("epi-block-id", out var key) ? key : null,
            fragment.InlineName ?? fragment.Name,
            type is { } inline ? Model.TypeName(inline) : fragment.ContentGuid is { } guid ? session.Identities.Header(guid) is { } shared ? Model.TypeName(shared.TypeId) : null : null);
    }

    /// <summary>The markup of a composition ContentArea, in the language asked for, else the master language.</summary>
    private IReadOnlyList<ContentFragment>? Fragments(ContentHeader header, int languageId, int definitionId, string scope) =>
        _markup.GetValueOrDefault((header.Id, languageId, definitionId, scope)) ?? _markup.GetValueOrDefault((header.Id, header.MasterLanguageId, definitionId, scope));

    /// <summary>The stored scope of a ContentArea after the first <paramref name="steps"/> segments: none at the top, <c>.150:26(1).142.</c> below.</summary>
    private static string Prefix(string[] segments, int steps, int definitionId) =>
        steps == 0 ? "" : $".{string.Join(".", segments.Take(steps))}.{definitionId.ToString(CultureInfo.InvariantCulture)}.";

    private bool IsArea(int typeId, int definitionId) =>
        Model.Properties.GetValueOrDefault(definitionId) is { TypeName: "ContentArea" } definition
        && definition.Name.Equals(Compositions.ItemsProperty, StringComparison.OrdinalIgnoreCase)
        && definition.ContentTypeId == typeId && Compositions.IsLayouted(Model, typeId);

    private string Name(int id) => Model.Properties.GetValueOrDefault(id)?.Name ?? $"#{id.ToString(CultureInfo.InvariantCulture)}";
}
