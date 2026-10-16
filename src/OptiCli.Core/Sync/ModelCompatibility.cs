using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Sync;

/// <summary>One way the source's content model differs from the target's that a sync can't carry over.</summary>
/// <param name="Type">The content type, by the source's name.</param>
/// <param name="Property">The property; null when the whole type is missing.</param>
/// <param name="Difference">What differs: "not in the target's build", "XhtmlString in the source, String in the target".</param>
public sealed record ModelDifference(string Type, string? Property, string Difference);

/// <summary>
/// The source database's content model compared with the target database's, over SQL: what a sync can write there.
/// The target's database holds its build's model once its site has started on that build (in local mode the CMS commits
/// the code's types at startup), so it stands for the build. Types are matched by GUID, then by name; properties by name,
/// never by id (ids differ even between copies of one database). A target property the code no longer has
/// (<c>ExistsOnModel = 0</c>: admin mode, or removed code) isn't in the build. Content of a type the target lacks is
/// skipped; a property it lacks, or has as another type, is dropped from the values.
/// </summary>
public sealed class ModelCompatibility
{
    private readonly IReadOnlyDictionary<int, ContentTypeInfo> _types;
    private readonly IReadOnlyDictionary<int, string> _missingTypes;
    private readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> _dropped;

    private ModelCompatibility(
        IReadOnlyDictionary<int, ContentTypeInfo> types,
        IReadOnlyDictionary<int, string> missingTypes,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> dropped)
    {
        _types = types;
        _missingTypes = missingTypes;
        _dropped = dropped;
    }

    /// <summary>The target's type for a source type; null when the target doesn't have it (<see cref="MissingType"/> says why).</summary>
    public ContentTypeInfo? TargetType(int sourceTypeId) => _types.GetValueOrDefault(sourceTypeId);

    /// <summary>Why content of the source type can't be written to the target; null when it can.</summary>
    public string? MissingType(int sourceTypeId) => _missingTypes.GetValueOrDefault(sourceTypeId);

    /// <summary>The source type's properties that are dropped, with why: missing in the target's build, or of another type.</summary>
    public IReadOnlyDictionary<string, string> DroppedProperties(int sourceTypeId) =>
        _dropped.GetValueOrDefault(sourceTypeId) ?? new Dictionary<string, string>();

    /// <summary>Every difference of the given source types (the types the synced content has), types first.</summary>
    public IReadOnlyList<ModelDifference> Differences(CmsModel source, IEnumerable<int> sourceTypeIds)
    {
        var ids = sourceTypeIds.Distinct().OrderBy(source.TypeName, StringComparer.OrdinalIgnoreCase).ToList();
        return ids.Where(_missingTypes.ContainsKey).Select(id => new ModelDifference(source.TypeName(id), null, _missingTypes[id]))
            .Concat(ids.Where(_dropped.ContainsKey).SelectMany(id => _dropped[id].OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => new ModelDifference(source.TypeName(id), p.Key, p.Value))))
            .ToList();
    }

    /// <summary>Reads which properties each database's code no longer has, and compares the models.</summary>
    public static async Task<ModelCompatibility> CompareAsync(ContentSession source, ContentSession target, CancellationToken cancellationToken) =>
        Compare(source.Model, await RemovedAsync(source.Db, cancellationToken), target.Model, await RemovedAsync(target.Db, cancellationToken));

    /// <param name="sourceRemoved">The source's property definitions with <c>ExistsOnModel = 0</c> (an export leaves those out anyway).</param>
    /// <param name="targetRemoved">The target's, which aren't in its build.</param>
    public static ModelCompatibility Compare(CmsModel source, IReadOnlySet<int> sourceRemoved, CmsModel target, IReadOnlySet<int> targetRemoved)
    {
        var byGuid = target.Types.GroupBy(t => t.Guid).ToDictionary(g => g.Key, g => g.First());
        var byName = target.Types.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<int, ContentTypeInfo>();
        var missing = new Dictionary<int, string>();
        var dropped = new Dictionary<int, IReadOnlyDictionary<string, string>>();
        foreach (var type in source.Types)
        {
            var match = type.Guid != Guid.Empty && byGuid.TryGetValue(type.Guid, out var sameGuid) ? sameGuid
                : byName.TryGetValue(type.Name, out var sameName) ? sameName
                : null;
            if (match is null)
            {
                missing[type.Id] = "not in the target's build (no content type with its GUID or name)";
                continue;
            }
            if (match.Kind != type.Kind && !(match.Kind.IsBlock() && type.Kind.IsBlock()))
            {
                missing[type.Id] = $"a {type.Kind.Name()} type in the source, a {match.Kind.Name()} type in the target";
                continue;
            }
            types[type.Id] = match;

            var properties = target.PropertiesOf(match.Id).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in source.PropertiesOf(type.Id).Where(p => !sourceRemoved.Contains(p.Id)))
            {
                if (!properties.TryGetValue(property.Name, out var other))
                {
                    problems[property.Name] = "not in the target's build";
                }
                else if (targetRemoved.Contains(other.Id))
                {
                    problems[property.Name] = "not in the target's build (only in its database: removed code, or added in admin mode)";
                }
                else if (Kind(source, property) != Kind(target, other))
                {
                    problems[property.Name] = $"{Shown(property)} in the source, {Shown(other)} in the target";
                }
            }
            if (problems.Count > 0)
            {
                dropped[type.Id] = problems;
            }
        }
        return new ModelCompatibility(types, missing, dropped);
    }

    /// <summary>
    /// A property's type as both CMS versions can compare it: CMS 13 stores a page reference as a content reference, and
    /// names a block property after its block type (by GUID here, as the block type's name may differ); a list says so.
    /// </summary>
    internal static string Kind(CmsModel model, PropertyDefinition property)
    {
        var name = property.BaseType == PropertyBaseType.Block && property.BlockType is { } block && model.Type(block) is { } blockType
            ? $"block {blockType.Guid:D}"
            : property.TypeName is "PageReference" ? "ContentReference" : property.TypeName;
        return property.IsList ? $"{name}[]" : name;
    }

    private static string Shown(PropertyDefinition property) => property.IsList ? $"{property.TypeName}[]" : property.TypeName;

    private static async Task<IReadOnlySet<int>> RemovedAsync(CmsDatabase db, CancellationToken cancellationToken) =>
        (await db.QueryAsync("SELECT pkID FROM tblPropertyDefinition WHERE ExistsOnModel = 0", r => r.GetInt32("pkID"), cancellationToken)).ToHashSet();
}
