using System.Text.Json.Serialization;

namespace OptiCli.Protocol;

/// <summary>
/// Body of <see cref="AgentRoutes.TypesRemove"/>: remove content types and properties that removed code left in the
/// database, through the CMS's own repositories. Either named items (<see cref="Types"/>, <see cref="Properties"/>),
/// which must all be removable or nothing is removed, or <see cref="Prune"/>, which removes what can go and reports the
/// rest.
/// </summary>
public sealed record OrphanRemovalRequest
{
    /// <summary><c>types remove</c>: content types by name or GUID.</summary>
    public IReadOnlyList<string>? Types { get; init; }

    /// <summary><c>types remove-property</c>: properties, each by its type and its name.</summary>
    public IReadOnlyList<OrphanPropertyRef>? Properties { get; init; }

    /// <summary><c>types prune</c>: every orphaned type that can go (and with <see cref="PruneProperties"/>, every orphaned property).</summary>
    public bool Prune { get; init; }

    /// <summary>
    /// With <see cref="Prune"/>: orphaned properties too. Off by default: a property added in admin mode to a type with a
    /// class looks exactly like one removed from the code (<see cref="OrphanRemoval.AdminModeLookalike"/>).
    /// </summary>
    public bool PruneProperties { get; init; }

    /// <summary>Remove properties that have stored values, which go with them for good.</summary>
    public bool AllowDestructive { get; init; }

    /// <summary>Run every check without removing anything.</summary>
    public bool DryRun { get; init; }
}

/// <param name="Type">The content type's name or GUID.</param>
/// <param name="Property">The property's name.</param>
public sealed record OrphanPropertyRef(string Type, string Property);

/// <summary>
/// Response of <see cref="AgentRoutes.TypesRemove"/>. Content types and property definitions aren't versioned and can't
/// be restored: <see cref="Types"/> and <see cref="Properties"/> hold what is needed to make them again by hand.
/// </summary>
/// <param name="Types">Types removed (or, for a dry run, that would be), in the order they were removed.</param>
/// <param name="Properties">Properties removed on their own (not with their type), with the values that went with them.</param>
/// <param name="Kept">With <see cref="OrphanRemovalRequest.Prune"/>: orphans that stay, and why.</param>
/// <param name="Removed">Something was removed (false for a dry run, or when there was nothing to remove).</param>
public sealed record OrphanRemovalResult(
    IReadOnlyList<RemovedContentType> Types,
    IReadOnlyList<RemovedProperty> Properties,
    IReadOnlyList<KeptOrphan> Kept,
    bool DryRun,
    bool Removed)
{
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>
    /// The file the site wrote each record to before removing it (<see cref="OrphanRemoval.RecordFileName"/> in opticli's
    /// state directory), one JSON line per removal; null for a dry run or when nothing was removed.
    /// </summary>
    public string? RecordFile { get; init; }
}

/// <summary>One line of <see cref="OrphanRemovalResult.RecordFile"/>: written just before the CMS is asked to remove it.</summary>
/// <param name="Project">The site's content root, as the site process sees it.</param>
/// <param name="Database">The database's server and name.</param>
/// <param name="Type">A type that is being removed, with its properties.</param>
/// <param name="Property">A property that is being removed on its own, with its values.</param>
public sealed record RemovalRecord(DateTime Time, string Project, string Database, RemovedContentType? Type, RemovedProperty? Property)
{
    /// <summary>Set on the line written after a removal the CMS refused or that failed: it wasn't removed.</summary>
    public string? Failed { get; init; }
}

/// <summary>A content type as it was before it was removed.</summary>
/// <param name="Base">The CMS's base (<c>Page</c>, <c>Block</c>, <c>Media</c>, ...).</param>
/// <param name="ModelType">The class the CMS had on record for it, which the site couldn't load.</param>
public sealed record RemovedContentType(
    int Id,
    Guid Guid,
    string Name,
    string Base,
    string? DisplayName,
    string? Description,
    string ModelType,
    IReadOnlyList<RemovedPropertyDefinition> Properties)
{
    /// <summary>The types it allowed below it (admin mode's "Available content types"), when it named them.</summary>
    public IReadOnlyList<string>? AllowedChildren { get; init; }

    /// <summary>Types that allowed it below them by name; the CMS takes it out of their settings.</summary>
    public IReadOnlyList<string>? AvailableUnder { get; init; }
}

/// <summary>A property removed on its own, with the values the CMS deleted with it.</summary>
/// <param name="Type">The content type it belonged to (which stays).</param>
public sealed record RemovedProperty(string Type, RemovedPropertyDefinition Property, StoredValueCounts Values);

/// <summary>A property definition as it was, to make it again by hand.</summary>
/// <param name="DataType">The CMS's data type (<c>String</c>, <c>LongString</c>, <c>Block</c>, ...).</param>
/// <param name="TypeName">The property type's class (<c>EPiServer.SpecializedProperties.PropertyXhtmlString</c>, ...), when it has one.</param>
/// <param name="BlockType">For a block property: its block type's name.</param>
public sealed record RemovedPropertyDefinition(
    int Id,
    string Name,
    string? DataType,
    string? TypeName,
    string? BlockType,
    bool CultureSpecific,
    bool Required,
    bool Searchable,
    bool DisplayEditUi,
    string? EditCaption,
    string? HelpText,
    string? Tab,
    int FieldOrder);

/// <summary>
/// How much is stored for a property, everywhere the CMS deletes it with the property: its own values, values inside it
/// when it is a block (local blocks, block lists), and category selections.
/// </summary>
/// <param name="Content">Content items (any language, the recycle bin included).</param>
/// <param name="Versions">Versions holding a value.</param>
public sealed record StoredValueCounts(int Content, int Versions)
{
    /// <summary>
    /// Content providers (other than the CMS's own database) that report using the property, or <c>unknown</c> when the CMS's
    /// usage check says it is used where these counts see nothing. Their values can't be counted or seen, and may be kept
    /// outside the CMS's tables, so such a property is never removed.
    /// </summary>
    public IReadOnlyList<string>? Providers { get; init; }

    [JsonIgnore]
    public bool Any => Content > 0 || Versions > 0;

    [JsonIgnore]
    public bool ProviderUse => Providers is { Count: > 0 };
}

/// <summary>What uses a content type and keeps it from being removed.</summary>
/// <param name="Content">Content items of the type (not deleted).</param>
/// <param name="InRecycleBin">Content items of the type in the recycle bin.</param>
/// <param name="InlineBlocks">Content whose ContentAreas hold inline blocks of the type.</param>
/// <param name="PageTypeValues">Page-type property values (in versions and on content) that name the type; the CMS would clear them.</param>
/// <param name="UsedBy">Properties (<c>Type.Property</c>) whose block type it is.</param>
public sealed record TypeUsage(int Content, int InRecycleBin, int InlineBlocks, int PageTypeValues, IReadOnlyList<string> UsedBy)
{
    public static readonly TypeUsage None = new(0, 0, 0, 0, []);

    /// <summary>
    /// Content the CMS itself reports for the type when none of the above counts any (its own check, which also covers
    /// values of a block type's properties stored elsewhere and content providers).
    /// </summary>
    public int OtherUses { get; init; }

    /// <summary>The versions (<c>123_456</c>) whose page-type property values name it, at most <see cref="OrphanRemoval.MaxRefs"/>.</summary>
    public IReadOnlyList<string>? PageTypeVersions { get; init; }

    /// <summary>Content or values use it: anything but <see cref="UsedBy"/>.</summary>
    [JsonIgnore]
    public bool InUse => Content + InRecycleBin + InlineBlocks + PageTypeValues + OtherUses > 0;
}

/// <summary>An orphan that stays (prune), or a named item that can't go.</summary>
/// <param name="Type">The content type's name.</param>
/// <param name="Property">The property's name, for a property.</param>
/// <param name="Code">Why, as an error code: <c>refused</c> (not an orphan, or values without the flag), <c>conflict</c> (content uses it), <c>not_found</c>.</param>
public sealed record KeptOrphan(string Type, string? Property, string Code, string Reason)
{
    /// <summary>For a property: its stored values.</summary>
    public StoredValueCounts? Values { get; init; }

    /// <summary>For a type: what uses it.</summary>
    public TypeUsage? Usage { get; init; }
}

/// <summary>Rules and wording the CLI and the site agent share for removing orphaned content types and properties.</summary>
public static class OrphanRemoval
{
    public const string SharedRefusal =
        "Against a shared database opticli doesn't remove content types or properties: the deployed site uses the same content model.";

    public const string SharedHint = "Remove them in that environment's own admin UI (Content Types), or in a local copy of the database.";

    /// <summary>Content types and property definitions have no versions and no recycle bin.</summary>
    public const string NoUndo =
        "Content types and properties aren't versioned and can't be restored: the output is the only record of what was removed, to make it again by hand (admin mode, or code).";

    /// <summary>The file in opticli's state directory the site agent writes every removal to, before it removes it.</summary>
    public const string RecordFileName = "removals.jsonl";

    /// <summary>Stands for a content provider the CMS's usage check found but that couldn't be named.</summary>
    public const string UnknownProvider = "unknown";

    /// <summary>How many content refs an answer lists at most.</summary>
    public const int MaxRefs = 20;

    public const string ProviderHint =
        "A content provider (a catalog, a DAM, ...) keeps content of the site that uses the property; opticli can't count or see those values, and the provider may store them outside the CMS's tables. Remove its values through that provider (or remove the property in admin mode once it has none).";

    public const string AdminModeLookalike =
        "A property added in admin mode to a type that has a class looks the same in the database (existsOnModel: false) as one removed from the code: check that none of these was made in admin mode on purpose.";

    /// <summary>The CMS's own types, which have no class (<c>ContentTypeSynchronizer</c>'s list): never removed.</summary>
    public static readonly IReadOnlyList<string> SystemTypes = ["SysRoot", "SysRecycleBin", "SysContentFolder", "SysContentAssetFolder"];

    /// <summary>
    /// Counts, for the property <c>@id</c>, what the CMS's <c>netPropertyDefinitionDelete</c> deletes with it: its values
    /// (<c>Content</c>: distinct content items, <c>Versions</c>: distinct versions) and its category selections, and for a
    /// block property (<paramref name="block"/>) every value stored inside it, whose scope names it (<c>.12.</c>, a list's
    /// <c>.12(</c>). Read-only; shared by <c>type</c> (a database read) and the site agent, so both count alike.
    /// </summary>
    public static string PropertyValuesSql(bool block)
    {
        var scoped = block ? " OR ScopeName LIKE @inner OR ScopeName LIKE @list" : "";
        return $"""
            DECLARE @inner nvarchar(30) = N'%.' + CAST(@id AS nvarchar(11)) + N'.%';
            DECLARE @list nvarchar(30) = N'%.' + CAST(@id AS nvarchar(11)) + N'(%';
            SELECT
                (SELECT COUNT(*) FROM (
                    SELECT fkContentID FROM tblContentProperty WHERE fkPropertyDefinitionID = @id{scoped}
                    UNION SELECT fkContentID FROM tblContentCategory WHERE CategoryType = @id) c) AS Content,
                (SELECT COUNT(*) FROM (
                    SELECT fkWorkContentID FROM tblWorkContentProperty WHERE fkPropertyDefinitionID = @id{scoped}
                    UNION SELECT fkWorkContentID FROM tblWorkContentCategory WHERE CategoryType = @id) v) AS Versions
            """;
    }

    /// <summary>"3 items, 1 in the recycle bin" style text for what keeps a type.</summary>
    public static string Describe(TypeUsage usage)
    {
        var parts = new List<string>();
        if (usage.Content > 0)
        {
            parts.Add(Count(usage.Content, "content item"));
        }
        if (usage.InRecycleBin > 0)
        {
            parts.Add($"{Count(usage.InRecycleBin, "content item")} in the recycle bin");
        }
        if (usage.InlineBlocks > 0)
        {
            parts.Add($"inline blocks in the ContentAreas of {Count(usage.InlineBlocks, "content item")}");
        }
        if (usage.PageTypeValues > 0)
        {
            parts.Add($"{Count(usage.PageTypeValues, "page-type property value")} naming it{(usage.PageTypeVersions is { Count: > 0 } refs ? $" (in {string.Join(", ", refs)})" : "")}");
        }
        if (usage.OtherUses > 0)
        {
            parts.Add($"{Count(usage.OtherUses, "content item")} the CMS counts as using it");
        }
        if (usage.UsedBy.Count > 0)
        {
            parts.Add($"the block property {string.Join(", ", usage.UsedBy)}");
        }
        return string.Join("; ", parts);
    }

    /// <summary>"1 content item, 3 versions".</summary>
    public static string Describe(StoredValueCounts values) =>
        $"{Count(values.Content, "content item")}, {Count(values.Versions, "version")}{(values.ProviderUse ? $"; used by the content provider {string.Join(", ", values.Providers!)}" : "")}";

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
