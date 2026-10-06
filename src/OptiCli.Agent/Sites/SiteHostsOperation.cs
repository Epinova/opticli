using System.Globalization;
using EPiServer.DataAbstraction;
using EPiServer.Web;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Sites;

/// <summary>
/// <c>POST /v1/sites/hosts</c>: plans the changes with <see cref="SiteHostPlanner"/>, then saves every changed site through
/// <see cref="ISiteDefinitionRepository"/>, which clears the site definition cache and raises the change events (to other
/// servers too, where remote events are set up). That is why this goes through the site rather than SQL.
/// </summary>
/// <remarks>
/// Here and not in <c>OptiCli.Cms</c>, which the MCP module compiles in: site definitions are for the developer only, and
/// a production site must never be able to change them through opticli. They have no access rights of their own (only
/// admin mode guards them), so there would be nothing to check an editor against.
/// </remarks>
internal static class SiteHostsOperation
{
    public const string RestartWarning =
        "Saved through the site that `opticli serve` runs, which uses the new hosts now. Another process running this site against the same database (your IDE's, say) keeps its cached site definitions until it restarts, unless remote events are set up: restart the site there.";

    public static SiteHostsResult Run(AgentRequest request, SiteHostsRequest body)
    {
        // CMS 13 keeps sites as applications; its site definition shim lists them but refuses to save them.
        Compat.AgentBuild.RequireCms12("Changing site hosts (sites primary, sites host)", "Change the application's hosts in the CMS's admin mode (Applications) for now.");
        var repository = request.Service<ISiteDefinitionRepository>();
        var languages = request.Service<ILanguageBranchRepository>().ListEnabled()
            .Select(l => l.LanguageID?.Trim() ?? "")
            .Where(code => code.Length > 0)
            .ToList();
        var plan = SiteHostPlanner.Plan(
            repository.List().Select(ToState).ToList(),
            body.Changes ?? throw AgentException.Usage("changes is missing."),
            languages,
            request.Service<AgentSettings>().SharedDatabase);

        var warnings = plan.Warnings.ToList();
        var saved = new Dictionary<Guid, SiteState>();
        if (!body.DryRun)
        {
            // Everything that can fail before a save is done for every site first: loading it and setting its hosts (the
            // CMS parses each name). A host moved from one site to another in the same batch must be gone from the first
            // before the CMS sees it on the second, so sites that lose hosts are saved first.
            var pending = plan.Sites.Where(s => s.Changed).OrderBy(s => s.RemovesHosts ? 0 : 1).Select(s => (Site: s, Definition: Prepare(repository, s))).ToList();
            foreach (var (site, definition) in pending)
            {
                try
                {
                    if (request.Call.Aborted.IsCancellationRequested)
                    {
                        throw new AgentException(AgentErrorCodes.Internal, "The caller stopped waiting, so nothing more was saved.", "the CLI timed out or was interrupted");
                    }
                    saved[site.After.Id] = Save(repository, site, definition);
                }
                catch (Exception ex) when (saved.Count > 0)
                {
                    throw Partial(ex, saved.Values, pending.Select(p => p.Site.After).Where(s => !saved.ContainsKey(s.Id)));
                }
            }
            if (saved.Count > 0)
            {
                warnings.Add(RestartWarning);
            }
        }

        return new SiteHostsResult
        {
            Sites = plan.Sites.Select(s =>
            {
                var state = saved.GetValueOrDefault(s.After.Id) ?? s.After;
                return new SiteHostsSite(state.Id, state.Name, s.Changed ? SiteHostStatus.Changed : SiteHostStatus.Unchanged, s.Changes, state.Hosts, state.SiteUrl);
            }).ToList(),
            DryRun = body.DryRun,
            Saved = saved.Count > 0,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    /// <summary>A writable copy of the site with the planned hosts and SiteUrl, not saved yet.</summary>
    private static SiteDefinition Prepare(ISiteDefinitionRepository repository, PlannedSite site)
    {
        var definition = (repository.Get(site.After.Id) ?? throw AgentException.NotFound($"The site {site.After.Name} was deleted meanwhile; nothing was saved.")).CreateWritableClone();
        try
        {
            definition.Hosts = site.After.Hosts.Select(ToDefinition).ToList();
            if (site.After.SiteUrl is { } url)
            {
                definition.SiteUrl = new Uri(url);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            throw Refused(site, ex);
        }
        return definition;
    }

    /// <returns>The site as the repository has it after the save.</returns>
    private static SiteState Save(ISiteDefinitionRepository repository, PlannedSite site, SiteDefinition definition)
    {
        try
        {
            repository.Save(definition);
        }
        catch (ArgumentException ex)
        {
            throw Refused(site, ex);
        }
        return ToState(repository.Get(site.After.Id) ?? definition);
    }

    private static AgentException Refused(PlannedSite site, Exception ex) => AgentException.Invalid(
        [new ValidationIssue(null, $"{site.After.Name}: the CMS refused the site's hosts: {ex.Message}")],
        SiteHostPlanner.ValidationHint,
        AgentErrorReasons.SiteHosts);

    /// <summary>
    /// A save that failed after other sites were saved: those stay saved (site definitions have no transaction across
    /// sites), and the message says so, whatever the failure was.
    /// </summary>
    private static AgentException Partial(Exception failure, IEnumerable<SiteState> saved, IEnumerable<SiteState> notSaved)
    {
        var agent = failure as AgentException;
        if (agent is null)
        {
            // What the middleware would log for an unexpected failure; the caller gets the message and type.
            Console.Error.WriteLine($"[opticli] saving site hosts failed: {failure}");
        }
        var savedText = $"Already saved: {string.Join(", ", saved.Select(s => s.Name))}; not saved: {string.Join(", ", notSaved.Select(s => s.Name))}.";
        return new AgentException(
            agent?.Code ?? AgentErrorCodes.Internal,
            $"{(agent?.Message ?? failure.Message).TrimEnd('.')}. {savedText}",
            $"The saved sites keep their new hosts (`opticli sites` shows them). Fix the problem and run the same command again: the saved sites are then unchanged.{(agent is null ? $" ({failure.GetType().FullName})" : "")}")
        {
            Validation = agent?.Validation,
            Reason = agent?.Reason,
        };
    }

    internal static SiteState ToState(SiteDefinition site) => new(
        site.Id,
        site.Name,
        site.SiteUrl?.ToString(),
        site.Hosts.Select(h => new SiteHost(
            h.Name,
            HostTypes.FromValue((int)h.Type),
            h.Language is { Name.Length: > 0 } language ? language.Name : null,
            h.UseSecureConnection)).ToList());

    private static HostDefinition ToDefinition(SiteHost host) => new()
    {
        Name = host.Name,
        Type = (HostDefinitionType)HostTypes.ToValue(host.Type),
        Language = host.Language is null ? null : CultureInfo.GetCultureInfo(host.Language),
        UseSecureConnection = host.Https,
    };
}
