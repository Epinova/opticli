namespace OptiCli.Core.Cms;

/// <param name="SchemaVersion">Return value of <c>dbo.sp_DatabaseVersion</c>, the CMS schema version.</param>
/// <param name="SqlServerVersion">SERVERPROPERTY('ProductVersion').</param>
public sealed record DatabaseInfo(
    string Database,
    int? SchemaVersion,
    string? SqlServerVersion,
    int ContentTypes,
    int ContentItems);
