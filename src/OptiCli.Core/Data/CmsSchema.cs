namespace OptiCli.Core.Data;

/// <summary>
/// Which CMS schema a database has, read once per connection (<see cref="CmsDatabase.SchemaAsync"/>): readers pick their
/// SQL from it instead of trying a query and falling back. CMS 13 (schema 21000 and up) moved block property types to
/// <c>tblPropertyDefinition.ItemTypeID</c> and sites to <c>tblApplication</c>, and added content variations and
/// blueprints; each is probed by the column or table itself, so a database between the two (none is known) still reads.
/// </summary>
/// <param name="Version"><c>dbo.sp_DatabaseVersion</c>; null when it couldn't be read.</param>
/// <param name="BlockTypeOnPropertyDefinition">
/// A block property's type is <c>tblPropertyDefinition.ItemTypeID</c> (CMS 13), not
/// <c>tblPropertyDefinitionType.fkContentTypeGUID</c> (CMS 12, gone on 13).
/// </param>
/// <param name="Applications">Sites are applications (<c>tblApplication</c>, <c>tblApplicationHost</c>); <c>tblSiteDefinition</c> is then empty or stale.</param>
/// <param name="Variations">Versions can belong to a content variation (<c>tblWorkContent.fkVariationID</c>).</param>
/// <param name="Blueprints">Content can be a blueprint (<c>tblContent.Blueprint</c>).</param>
public sealed record CmsSchema(int? Version, bool BlockTypeOnPropertyDefinition, bool Applications, bool Variations, bool Blueprints)
{
    /// <summary>The first CMS 13 schema version (CMS 13.0.0); CMS 12 ends at 8023.</summary>
    public const int FirstCms13Version = 21000;

    /// <summary>The CMS 12 schema as opticli always read it.</summary>
    public static readonly CmsSchema Cms12 = new(null, false, false, false, false);

    /// <summary>A CMS 13 schema, for tests.</summary>
    public static readonly CmsSchema Cms13 = new(FirstCms13Version, true, true, true, true);

    internal const string Sql = """
        DECLARE @v int;
        IF OBJECT_ID(N'dbo.sp_DatabaseVersion', N'P') IS NOT NULL EXEC @v = dbo.sp_DatabaseVersion;
        SELECT @v AS SchemaVersion,
               CONVERT(bit, CASE WHEN COL_LENGTH(N'dbo.tblPropertyDefinition', N'ItemTypeID') IS NULL THEN 0 ELSE 1 END) AS ItemTypeID,
               CONVERT(bit, CASE WHEN OBJECT_ID(N'dbo.tblApplication', N'U') IS NULL OR OBJECT_ID(N'dbo.tblApplicationHost', N'U') IS NULL THEN 0 ELSE 1 END) AS Applications,
               CONVERT(bit, CASE WHEN COL_LENGTH(N'dbo.tblWorkContent', N'fkVariationID') IS NULL THEN 0 ELSE 1 END) AS Variations,
               CONVERT(bit, CASE WHEN COL_LENGTH(N'dbo.tblContent', N'Blueprint') IS NULL THEN 0 ELSE 1 END) AS Blueprints
        """;

    /// <summary>The CMS major the schema belongs to (12 or 13), from the version, else from what the tables look like.</summary>
    public int Major => Version is { } version ? MajorOf(version) : Applications ? 13 : 12;

    /// <summary>12 below <see cref="FirstCms13Version"/>, 13 from it.</summary>
    public static int MajorOf(int schemaVersion) => schemaVersion >= FirstCms13Version ? 13 : 12;

    /// <summary>
    /// <c> AND &lt;alias&gt;.fkVariationID IS NULL</c> on a schema with variations, else nothing: a variation's versions
    /// are patches over the published version, not versions of the content itself.
    /// </summary>
    public string DefaultVariationOnly(string alias) => Variations ? $" AND {alias}.fkVariationID IS NULL" : "";
}
