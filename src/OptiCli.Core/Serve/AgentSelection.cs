using OptiCli.Core.Data;
using OptiCli.Core.Discovery;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Serve;

/// <summary>Which agent build the site gets.</summary>
/// <param name="CmsMajor">12 or 13.</param>
/// <param name="From">What decided it: <c>project</c> (its EPiServer.CMS.Core version) or <c>database</c> (its schema version).</param>
public sealed record AgentChoice(int CmsMajor, string From);

/// <summary>
/// Picks the agent build for a site from the CMS major it builds against (as <c>doctor</c> reads it: the
/// <see cref="CsprojFile.CmsPackage"/> reference, else <c>obj/project.assets.json</c>), else from its database's schema
/// version. A CMS 12
/// agent crashes a CMS 13 site at startup, and a CMS 13 one can't load into a CMS 12 site's .NET 8 runtime.
/// </summary>
public static class AgentSelection
{
    /// <summary>The CMS majors opticli has an agent build for.</summary>
    public static readonly IReadOnlyList<int> SupportedMajors = [12, 13];

    /// <summary>The CMS major a database's schema belongs to; null for a database without a CMS schema yet.</summary>
    public static int? DatabaseMajor(CmsSchema schema) =>
        schema.Version is { } version ? CmsSchema.MajorOf(version) : schema.Applications ? 13 : null;

    /// <param name="projectCmsVersion">The project's CMS version (<see cref="PackageVersions.FindCms"/>); null when unknown.</param>
    /// <param name="schema">The database's schema; null when it couldn't be read.</param>
    /// <param name="database">The database's name, for messages.</param>
    /// <param name="databaseError">Why the schema couldn't be read, for the message when nothing else tells.</param>
    /// <exception cref="RefusedException">The project and the database belong to different CMS majors.</exception>
    /// <exception cref="UsageException">Neither tells the major, or it is one opticli has no agent for.</exception>
    public static AgentChoice Choose(string? projectCmsVersion, CmsSchema? schema, string? database, string? databaseError = null)
    {
        var named = database is null ? "the database" : $"the database '{database}'";
        var project = PackageVersions.Major(projectCmsVersion);
        var stored = schema is null ? null : DatabaseMajor(schema);
        if (project is { } builds && stored is { } has && builds != has)
        {
            var version = schema!.Version is { } v ? $" (version {v})" : "";
            throw new RefusedException(
                $"The project builds against CMS {builds} ({projectCmsVersion}), but {named} has a CMS {has} schema{version}: opticli doesn't start the site against it.",
                builds > has
                    ? $"Starting it would upgrade the database to CMS {builds}, after which CMS {has} can't use it. Check that the connection string points at the right database (`opticli doctor`). To upgrade this database on purpose, start the site once yourself (dotnet run), then use `opticli serve`."
                    : $"CMS {builds} can't start against it. Check that the connection string points at the right database (`opticli doctor`), or update the project's EPiServer packages.");
        }
        var choice = project is { } fromProject ? new AgentChoice(fromProject, "project")
            : stored is { } fromDatabase ? new AgentChoice(fromDatabase, "database")
            : throw new UsageException(
                $"Can't tell which CMS major the site runs: the project's {CsprojFile.CmsPackage} version is unknown, and "
                + (databaseError is null ? $"{named} has no CMS schema yet." : $"{named} couldn't be read ({databaseError})."),
                "Restore the project (dotnet restore, or `opticli serve --build`), then start again; `opticli doctor` shows what it found.");
        if (!SupportedMajors.Contains(choice.CmsMajor))
        {
            var what = choice.From == "project" ? $"The project builds against CMS {choice.CmsMajor} ({projectCmsVersion})" : $"{char.ToUpperInvariant(named[0])}{named[1..]} has a CMS {choice.CmsMajor} schema";
            throw new UsageException($"{what}; opticli runs sites on CMS 12 and 13 only.", "Read commands still work where the schema allows; writes need a CMS 12 or 13 site.");
        }
        return choice;
    }
}
