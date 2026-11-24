using OptiCli.Core.Discovery;
using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Cli;

/// <summary>
/// For reads from the CMS database: catalog content a value names only by its GUID (a rich-text link), found by the running
/// site's Commerce agent. Only on a project that builds against Commerce, and only when the site runs: otherwise the GUID
/// stays missing, as before, and the read never fails over it.
/// </summary>
internal static class CommerceLookup
{
    public static Func<IReadOnlyList<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, (int Id, string Provider)>>>? For(CliContext context)
    {
        if (context.TryGetProject(out _) is not { } project || PackageVersions.FindCommerce(project.Project) is null)
        {
            return null;
        }
        return async (guids, cancellationToken) =>
        {
            var found = new Dictionary<Guid, (int Id, string Provider)>();
            try
            {
                var agent = await context.ConnectAgentAsync(cancellationToken);
                var response = await agent.SendAsync<CatalogRefsResponse>(HttpMethod.Post, AgentRoutes.CommerceRefs, new CatalogRefsRequest(guids.Select(g => g.ToString("D")).ToList()), cancellationToken);
                foreach (var item in response.Refs.Where(r => r.Found && r.Guid is not null && CatalogRefSyntax.ProviderId(r.Ref) is not null))
                {
                    found[item.Guid!.Value] = (CatalogRefSyntax.ProviderId(item.Ref)!.Value, CatalogRefSyntax.Provider);
                }
            }
            catch (OptiCliException)
            {
                // No site running, or no Commerce agent: the links stay missing.
            }
            return found;
        };
    }
}
