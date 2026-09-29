namespace OptiCli.Integration.Comparison;

public enum MismatchKind
{
    /// <summary>An identity field (name, status, url, ...) differs.</summary>
    Identity,

    /// <summary>Both sides have the property with a value, but of different types.</summary>
    Type,

    /// <summary>Same type, different value.</summary>
    Value,

    /// <summary>Only the DB side has a value.</summary>
    DbOnly,

    /// <summary>Only the CMS has a value.</summary>
    AgentOnly,

    /// <summary>One side failed to load the item at all.</summary>
    Error,
}

/// <param name="Item">What was compared: <c>123 [en]</c>, <c>123_456 [en]</c>.</param>
/// <param name="Field">Identity field or property name.</param>
/// <param name="PropertyType">The property's type; <c>DbType->CmsType</c> when the sides disagree; null for identity fields.</param>
/// <param name="Path">Where inside the value the first difference is (<c>[2].ref</c>); empty at the top.</param>
/// <param name="Db">The DB side's canonical value, as JSON.</param>
/// <param name="Agent">The CMS's canonical value, as JSON.</param>
public sealed record Mismatch(
    string Item,
    string ContentType,
    string Kind,
    MismatchKind Difference,
    string Field,
    string? PropertyType,
    string Path,
    string Db,
    string Agent)
{
    /// <summary>What is known about the compared item, for the allow-list.</summary>
    public ItemFacts Facts { get; init; } = ItemFacts.Unknown;

    /// <summary>What mismatches are grouped by in the report and matched on by the allow-list.</summary>
    public string Category => Difference switch
    {
        MismatchKind.Identity => $"identity:{Field}",
        MismatchKind.Error => $"error:{Field}",
        MismatchKind.Type => $"type:{PropertyType}",
        _ => $"{Difference switch { MismatchKind.Value => "value", MismatchKind.DbOnly => "db-only", _ => "agent-only" }}:{PropertyType}",
    };
}

/// <param name="CmsLocalizable">The CMS's model class for the item is localizable (it reports language branches).</param>
/// <param name="CmsVersionable">The CMS's model class for the item is versionable (it reports a status).</param>
/// <param name="CmsChangeTracked">The CMS's model class for the item is change-tracked (it reports when it was saved).</param>
/// <param name="DbMasterBranch">The DB side shows the item's master branch, or the item has no language.</param>
/// <param name="UnderSiteOrAssets">The item is below a site's start page or an asset root, the places opticli builds URLs for.</param>
public sealed record ItemFacts(bool CmsLocalizable, bool CmsVersionable, bool CmsChangeTracked, bool DbMasterBranch, bool UnderSiteOrAssets)
{
    public static readonly ItemFacts Unknown = new(true, true, true, true, true);
}
