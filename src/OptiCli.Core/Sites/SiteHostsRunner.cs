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
/// <param name="Status">One of <see cref="SiteHostStatus"/>.</param>
/// <param name="Url">The site's URL (SiteUrl) after the change.</param>
/// <param name="DryRun">True for a dry run; omitted otherwise.</param>
public sealed record SiteHostsView(string Site, int? Id, Guid Guid, string Status, IReadOnlyList<string> Changes, string? Url, IReadOnlyList<HostInfo> Hosts, bool? DryRun = null);

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
        var views = result.Sites.Select(s => new SiteHostsView(
            s.Name,
            sites.FirstOrDefault(d => d.Guid == s.Id)?.Id,
            s.Id,
            s.Status,
            s.Changes,
            s.SiteUrl,
            s.Hosts.Select(ToHostInfo).ToList(),
            result.DryRun ? true : null)).ToList();
        return new SiteHostsOutcome(views, result.Warnings ?? [], result.Saved);
    }

    /// <exception cref="RefusedException">See <see cref="SiteHostsRequest.AllowedOnSharedDatabase"/>.</exception>
    public static void RequireAllowed(IReadOnlyList<SiteHostChange> changes, IReadOnlyList<SiteInfo> sites, bool sharedDatabase)
    {
        if (sharedDatabase && changes.FirstOrDefault(c => !SiteHostsRequest.AllowedOnSharedDatabase(c)) is { } refused)
        {
            var name = Guid.TryParse(refused.Site, out var guid) ? sites.FirstOrDefault(s => s.Guid == guid)?.Name ?? refused.Site : refused.Site;
            throw new RefusedException($"{Describe(refused, name)}: {SiteHostsRequest.SharedRefusal}", SiteHostsRequest.SharedHint);
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
