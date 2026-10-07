using EPiServer.DataAbstraction;
using OptiCli.Agent.Compat;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Sites;

/// <summary>
/// <c>POST /v1/sites/hosts</c>: plans the changes with <see cref="SiteHostPlanner"/>, then saves every changed site through
/// the CMS (<see cref="SiteStore"/>: site definitions on CMS 12, applications on CMS 13), which clears its cache and raises
/// the change events (to other servers too, where remote events are set up). That is why this goes through the site
/// rather than SQL.
/// </summary>
/// <remarks>
/// Here and not in <c>OptiCli.Cms</c>, which the MCP module compiles in: site definitions are for the developer only, and
/// a production site must never be able to change them through opticli. They have no access rights of their own (only
/// admin mode guards them), so there would be nothing to check an editor against.
/// </remarks>
internal static class SiteHostsOperation
{
    public static string RestartWarning =>
        $"Saved through the site that `opticli serve` runs, which uses the new hosts now. Another process running this site against the same database (your IDE's, say) keeps its cached {SiteStore.What} until it restarts, unless remote events are set up: restart the site there.";

    public static async Task<SiteHostsResult> RunAsync(AgentRequest request, SiteHostsRequest body)
    {
        var store = new SiteStore(request.Context.RequestServices);
        var applications = SiteStore.Applications;
        var languages = request.Service<ILanguageBranchRepository>().ListEnabled()
            .Select(l => l.LanguageID?.Trim() ?? "")
            .Where(code => code.Length > 0)
            .ToList();
        var plan = SiteHostPlanner.Plan(
            store.List(),
            body.Changes ?? throw AgentException.Usage("changes is missing."),
            languages,
            request.Service<AgentSettings>().SharedDatabase,
            applications);

        var warnings = plan.Warnings.ToList();
        var saved = new Dictionary<string, SiteState>(StringComparer.OrdinalIgnoreCase);
        if (!body.DryRun)
        {
            // Everything that can fail before a save is done for every site first: loading it and setting its hosts (the
            // CMS parses each name). A host moved from one site to another in the same batch must be gone from the first
            // before the CMS sees it on the second, so sites that lose hosts are saved first.
            // An application that only loses the default to another one (CMS 13) isn't saved itself: that one's save moves it.
            var pending = plan.Sites.Where(s => s.Changed && !s.OnlyLosesDefault).OrderBy(s => s.RemovesHosts ? 0 : 1).Select(s => Prepare(store, s)).ToList();
            foreach (var prepared in pending)
            {
                var site = prepared.Site;
                try
                {
                    if (request.Call.Aborted.IsCancellationRequested)
                    {
                        throw new AgentException(AgentErrorCodes.Internal, "The caller stopped waiting, so nothing more was saved.", "the CLI timed out or was interrupted");
                    }
                    saved[site.After.Key] = await Save(store, prepared);
                }
                catch (Exception ex) when (saved.Count > 0)
                {
                    throw Partial(ex, saved.Values, pending.Select(p => p.Site.After).Where(s => !saved.ContainsKey(s.Key)));
                }
            }
            if (saved.Count > 0)
            {
                warnings.Add(RestartWarning);
            }
            // As the CMS has them after the save that took their default away.
            foreach (var site in plan.Sites.Where(s => s.DefaultMovesTo is not null && saved.ContainsKey(s.DefaultMovesTo)))
            {
                saved[site.After.Key] = store.Get(site.After.Key) ?? site.After;
            }
        }

        return new SiteHostsResult
        {
            Sites = plan.Sites.Select(s =>
            {
                var state = saved.GetValueOrDefault(s.After.Key) ?? s.After;
                return applications
                    ? new SiteHostsSite(null, state.Name, Status(s), s.Changes, state.Hosts.Where(h => !SiteHostPlanner.IsDefaultApplication(h)).ToList(), state.SiteUrl)
                    {
                        Application = state.Key,
                        IsDefault = state.Hosts.Any(SiteHostPlanner.IsDefaultApplication),
                    }
                    : new SiteHostsSite(state.Id, state.Name, Status(s), s.Changes, state.Hosts, state.SiteUrl);
            }).ToList(),
            DryRun = body.DryRun,
            Saved = saved.Count > 0,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    private static string Status(PlannedSite site) => site.Changed ? SiteHostStatus.Changed : SiteHostStatus.Unchanged;

    /// <summary>A writable copy of the site with the planned hosts (and SiteUrl, CMS 12), not saved yet.</summary>
    private static PreparedSite Prepare(SiteStore store, PlannedSite site)
    {
        try
        {
            return store.Prepare(site);
        }
        catch (SiteRefusedException ex)
        {
            throw Refused(site, ex);
        }
    }

    /// <returns>The site as the CMS has it after the save.</returns>
    private static async Task<SiteState> Save(SiteStore store, PreparedSite prepared)
    {
        try
        {
            return await store.SaveAsync(prepared, CancellationToken.None);
        }
        catch (SiteRefusedException ex)
        {
            throw Refused(prepared.Site, ex);
        }
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
}
