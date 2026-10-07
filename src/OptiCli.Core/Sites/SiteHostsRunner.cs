using OptiCli.Core.Cms;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Sites;

/// <summary>
/// One site after <c>sites primary</c> or <c>sites host</c>: what changed, and its hosts in the shape <c>opticli sites</c>
/// prints them (for a dry run: what they would be).
/// </summary>
/// <param name="Site">The site's name.</param>
/// <param name="Guid">The site's GUID; null (left out) for a CMS 13 application, which has none.</param>
/// <param name="Status">One of <see cref="SiteHostStatus"/>.</param>
/// <param name="Url">The site's URL (SiteUrl; CMS 13: the application's, which follows its hosts) after the change.</param>
/// <param name="DryRun">True for a dry run; omitted otherwise.</param>
/// <param name="Application">CMS 13: the application's name, as <c>opticli sites</c> prints it; omitted on CMS 12.</param>
/// <param name="IsDefault">CMS 13: whether it is the default application (CMS 12's <c>*</c> host); omitted on CMS 12.</param>
public sealed record SiteHostsView(string Site, int? Id, Guid? Guid, string Status, IReadOnlyList<string> Changes, string? Url, IReadOnlyList<HostInfo> Hosts, bool? DryRun = null, string? Application = null, bool? IsDefault = null);

public sealed record SiteHostsOutcome(IReadOnlyList<SiteHostsView> Sites, IReadOnlyList<string> Warnings, bool Saved);

/// <summary>Sends site host changes to the agent and reshapes its answer.</summary>
public static class SiteHostsRunner
{
    /// <param name="sites">The database's sites, for the ids <c>opticli sites</c> prints.</param>
    /// <param name="sharedDatabase">The database is remote, so the agent runs in shared mode: refuse what it would refuse, before asking about drift.</param>
    /// <exception cref="RefusedException">Shared mode, and a change other than adding a host of type undefined.</exception>
    public static async Task<SiteHostsOutcome> RunAsync(
        AgentClient agent,
        IReadOnlyList<SiteInfo> sites,
        IReadOnlyList<SiteHostChange> changes,
        bool dryRun,
        bool sharedDatabase,
        CancellationToken cancellationToken)
    {
        RequireAllowed(changes, sites, sharedDatabase);
        var result = await agent.SendAsync<SiteHostsResult>(HttpMethod.Post, AgentRoutes.SiteHosts, new SiteHostsRequest { Changes = changes, DryRun = dryRun }, cancellationToken);
        return new SiteHostsOutcome(Views(result, sites), result.Warnings ?? [], result.Saved);
    }

    /// <summary>
    /// The agent's answer in the shape <c>opticli sites</c> prints, with the ids it prints: a CMS 12 site matched by its
    /// GUID, a CMS 13 application by its name.
    /// </summary>
    public static IReadOnlyList<SiteHostsView> Views(SiteHostsResult result, IReadOnlyList<SiteInfo> sites) =>
        result.Sites.Select(s => new SiteHostsView(
            s.Name,
            sites.FirstOrDefault(d => s.Application is { } application ? string.Equals(d.Application, application, StringComparison.OrdinalIgnoreCase) : d.Guid == s.Id)?.Id,
            s.Id,
            s.Status,
            s.Changes,
            s.SiteUrl,
            s.Hosts.Select(ToHostInfo).ToList(),
            result.DryRun ? true : null,
            s.Application,
            s.IsDefault)).ToList();

    /// <exception cref="RefusedException">See <see cref="SiteHostsRequest.AllowedOnSharedDatabase"/>.</exception>
    public static void RequireAllowed(IReadOnlyList<SiteHostChange> changes, IReadOnlyList<SiteInfo> sites, bool sharedDatabase)
    {
        if (sharedDatabase && changes.FirstOrDefault(c => !SiteHostsRequest.AllowedOnSharedDatabase(c)) is { } refused)
        {
            var name = sites.FirstOrDefault(s => string.Equals(s.Key, refused.Site, StringComparison.OrdinalIgnoreCase))?.Name ?? refused.Site;
            throw new RefusedException($"{Describe(refused, name)}: {SiteHostsRequest.SharedRefusal}", SiteHostsRequest.SharedHint);
        }
    }

    /// <summary>
    /// Refuses the host types only CMS 13 has (<see cref="HostTypes.Cms13Only"/>) on a CMS 12 database up front, as the
    /// agent would: it needs no running site to say so.
    /// </summary>
    /// <exception cref="UsageException">A preview or media host on CMS 12.</exception>
    public static void RequireHostTypes(IReadOnlyList<SiteHostChange> changes, int cmsMajor)
    {
        if (cmsMajor < 13 && changes.Select(c => c.Type is { } type ? HostTypes.Parse(type) : null).FirstOrDefault(t => t is not null && HostTypes.Cms13Only.Contains(t)) is { } cms13Type)
        {
            throw new UsageException($"{cms13Type} hosts are CMS 13 only; this site runs CMS 12.", $"Host types on CMS 12: {HostTypes.Cms12Syntax}.");
        }
    }

    public static SiteHost ToSiteHost(HostInfo host) => new(host.Name, HostTypes.FromValue((int)host.Type), host.Language, host.Https);

    public static HostInfo ToHostInfo(SiteHost host) => new(host.Name, (HostType)Math.Max(0, HostTypes.ToValue(host.Type)), host.Language, host.Https);

    private static string Describe(SiteHostChange change, string site) => change.Action switch
    {
        SiteHostActions.Primary => $"sites primary \"{site}{(change.Language is { } language ? $"@{language}" : "")}={change.Host}\"",
        SiteHostActions.Add => $"sites host add \"{site}\" {change.Host}{(change.Type is { } type ? $" --type {type}" : "")}",
        _ => $"sites host remove \"{site}\" {change.Host}",
    };
}
