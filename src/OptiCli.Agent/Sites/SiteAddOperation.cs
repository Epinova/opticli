using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Agent.Compat;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Agent.Sites;

/// <summary>
/// <c>POST /v1/sites/add</c>: a new site definition (CMS 13: an in-process application) with its start page and hosts, for a
/// database built from scratch, which has none; or, with <see cref="SiteAddRequest.UpdateExisting"/>, the site of that name
/// made to match (a plan run again). Saved through the CMS (<see cref="SiteStore"/>), as <see cref="SiteHostsOperation"/> is.
/// </summary>
/// <remarks>Here and not in <c>OptiCli.Cms</c>, which the MCP module compiles in: site definitions are the developer's only.</remarks>
internal static class SiteAddOperation
{
    public const string ValidationHint = "Fix the listed hosts and retry; nothing was saved. `opticli sites` lists every site's hosts.";

    public static async Task<SiteAddResult> RunAsync(AgentRequest request, SiteAddRequest body)
    {
        if (request.Service<AgentSettings>().SharedDatabase)
        {
            throw AgentException.Refused(SiteAddRequest.SharedRefusal, SiteAddRequest.SharedHint);
        }
        var name = body.Name?.Trim() ?? "";
        if (name.Length == 0)
        {
            throw AgentException.Usage("The site needs a name.");
        }
        if (body.Hosts is not { Count: > 0 })
        {
            throw AgentException.Usage($"{name} needs at least one host.", "Give a host such as localhost:5000, or * to answer every host name no other site has.");
        }

        var applications = SiteStore.Applications;
        var store = new SiteStore(request.Context.RequestServices);
        var startPage = RequireStartPage(request, body.StartPage, applications);
        var languages = request.Service<ILanguageBranchRepository>().ListEnabled()
            .Select(l => l.LanguageID?.Trim() ?? "")
            .Where(code => code.Length > 0)
            .ToList();
        var requested = Hosts(body, languages, applications);

        var existing = store.Find(name);
        if (existing is not null && !body.UpdateExisting)
        {
            throw AgentException.Conflict(
                $"There is already a site named {existing.Name}.",
                "To make it match (start page, hosts added) run with --update-existing (`apply --update-existing` for a plan); `opticli sites host add` adds one host.");
        }
        var others = store.List().Where(s => existing is null || !string.Equals(s.Key, existing.Key, StringComparison.OrdinalIgnoreCase)).ToList();
        var issues = new List<ValidationIssue>();
        var warnings = new List<string>();
        foreach (var host in requested)
        {
            if (others.FirstOrDefault(s => s.Hosts.Any(h => HostNames.Same(h.Name, host.Name))) is not { } other)
            {
                continue;
            }
            if (applications && SiteHostPlanner.IsDefaultApplication(host))
            {
                warnings.Add($"{name} becomes the default application (*), which {other.Name} was.");
            }
            else
            {
                issues.Add(new ValidationIssue(null, $"{host.Name}: {other.Name} has this host already."));
            }
        }
        if (issues.Count > 0)
        {
            throw AgentException.Invalid(issues, ValidationHint, AgentErrorReasons.SiteHosts);
        }

        var (after, changes) = existing is null
            ? New(name, requested, startPage, applications, store)
            : Merge(existing, requested, startPage, applications);
        var status = existing is null ? SiteHostStatus.Created : changes.Count > 0 ? SiteHostStatus.Changed : SiteHostStatus.Unchanged;
        var saved = false;
        if (!body.DryRun && status != SiteHostStatus.Unchanged)
        {
            try
            {
                after = await store.SaveSiteAsync(existing, after, startPage, CancellationToken.None);
            }
            catch (SiteRefusedException ex)
            {
                throw AgentException.Invalid([new ValidationIssue(null, $"{name}: the CMS refused the site: {ex.Message}")], ValidationHint, AgentErrorReasons.SiteHosts);
            }
            saved = true;
            warnings.Add(SiteHostsOperation.RestartWarning);
        }

        var site = applications
            ? new SiteHostsSite(null, after.Name, status, changes, after.Hosts.Where(h => !SiteHostPlanner.IsDefaultApplication(h)).ToList(), after.SiteUrl)
            {
                Application = after.Key,
                IsDefault = after.Hosts.Any(SiteHostPlanner.IsDefaultApplication),
            }
            : new SiteHostsSite(after.Id, after.Name, status, changes, after.Hosts, after.SiteUrl);
        return new SiteAddResult { Site = site, DryRun = body.DryRun, Saved = saved, Warnings = warnings.Count > 0 ? warnings : null };
    }

    /// <exception cref="AgentException"><c>not_found</c>: no such content; <c>usage</c>: not a page (CMS 12).</exception>
    private static int RequireStartPage(AgentRequest request, int id, bool applications)
    {
        if (id <= 0 || !request.Service<IContentLoader>().TryGet<IContent>(new ContentReference(id), out var content))
        {
            throw AgentException.NotFound($"No content with id {id} for the start page.");
        }
        if (!applications && content is not PageData)
        {
            throw AgentException.Usage($"{content.Name} ({id}) is not a page; a site's start page is.");
        }
        if (content.ContentLink.CompareToIgnoreWorkID(ContentReference.RootPage))
        {
            throw AgentException.Usage("The root page can't be a site's start page; give a page below it.");
        }
        return id;
    }

    /// <summary>
    /// The requested hosts, normalised, with their types: in each language (and among those for every language), the first
    /// host without one is primary when no host there has that type, the rest undefined.
    /// </summary>
    /// <exception cref="AgentException"><c>validation</c>: every host that can't be one, with why.</exception>
    internal static IReadOnlyList<SiteHost> Hosts(SiteAddRequest body, IReadOnlyCollection<string> languages, bool applications)
    {
        var issues = new List<ValidationIssue>();
        var hosts = new List<(SiteHost Host, bool Typed)>();
        foreach (var given in body.Hosts)
        {
            var wildcard = given.Name?.Trim() == HostNames.Wildcard;
            string? name;
            bool? https;
            if (wildcard)
            {
                (name, https) = (HostNames.Wildcard, null);
            }
            else if (!HostNames.TryNormalize(given.Name, out name, out https, out var error, given.Https ?? body.Https))
            {
                issues.Add(new ValidationIssue(null, $"{given.Name}: {error}"));
                continue;
            }
            https = given.Https ?? https ?? body.Https ?? (applications && !wildcard ? !HostNames.IsLoopback(name) : null);
            var type = given.Type is null ? null : HostTypes.Parse(given.Type);
            if (given.Type is not null && type is null)
            {
                issues.Add(new ValidationIssue(null, $"{given.Name}: '{given.Type}' is not a host type ({(applications ? HostTypes.Syntax : HostTypes.Cms12Syntax)})."));
                continue;
            }
            if (!applications && type is not null && HostTypes.Cms13Only.Contains(type))
            {
                issues.Add(new ValidationIssue(null, $"{given.Name}: {type} hosts are CMS 13 only."));
                continue;
            }
            if (wildcard && type is not (null or HostTypes.Undefined))
            {
                issues.Add(new ValidationIssue(null, "*: the wildcard host is always undefined (it answers host names no other site has)."));
                continue;
            }
            var language = given.Language?.Trim() is { Length: > 0 } code ? code : null;
            if (language is not null)
            {
                if (languages.FirstOrDefault(l => l.Equals(language, StringComparison.OrdinalIgnoreCase)) is not { } enabled)
                {
                    issues.Add(new ValidationIssue(null, $"{given.Name}@{language}: '{language}' is not an enabled language ({string.Join(", ", languages)}); `opticli languages enable {language}` enables it."));
                    continue;
                }
                language = enabled;
            }
            if (wildcard && language is not null)
            {
                issues.Add(new ValidationIssue(null, "*: the wildcard host is for every language."));
                continue;
            }
            if (hosts.Any(h => HostNames.Same(h.Host.Name, name)))
            {
                issues.Add(new ValidationIssue(null, $"{name} is given twice."));
                continue;
            }
            hosts.Add((new SiteHost(name, type ?? HostTypes.Undefined, language, wildcard ? null : https), type is not null));
        }
        if (issues.Count > 0)
        {
            throw AgentException.Invalid(issues, ValidationHint, AgentErrorReasons.SiteHosts);
        }
        if (hosts.All(h => h.Host.Name == HostNames.Wildcard))
        {
            throw AgentException.Usage("A site needs a host besides *, for the URLs the CMS makes for it.", "Add one such as localhost:5000.");
        }

        var result = hosts.Select(h => h.Host).ToList();
        foreach (var group in hosts.Select((h, i) => (h.Host, h.Typed, Index: i)).GroupBy(h => h.Host.Language ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (group.Any(h => h.Host.Type == HostTypes.Primary))
            {
                continue;
            }
            if (group.FirstOrDefault(h => !h.Typed && h.Host.Name != HostNames.Wildcard) is { Host: not null } first)
            {
                result[first.Index] = first.Host with { Type = HostTypes.Primary };
            }
        }
        return result;
    }

    private static (SiteState After, IReadOnlyList<string> Changes) New(string name, IReadOnlyList<SiteHost> hosts, int startPage, bool applications, SiteStore store)
    {
        var changes = new List<string> { $"created, start page {startPage}" };
        changes.AddRange(hosts.Select(h => $"host {Describe(h)}"));
        if (applications)
        {
            return (new SiteState(store.ApplicationName(name), name, null, hosts) { StartPage = startPage }, changes);
        }
        var primary = hosts.FirstOrDefault(h => h is { Type: HostTypes.Primary, Language: null }) ?? hosts.FirstOrDefault(h => h.Type == HostTypes.Primary)
            ?? hosts.First(h => h.Name != HostNames.Wildcard);
        var siteUrl = HostNames.SiteUrl(primary.Name, primary.Https ?? !HostNames.IsLoopback(primary.Name));
        changes.Add($"SiteUrl {siteUrl}");
        return (new SiteState(Guid.Empty, name, siteUrl, hosts) { StartPage = startPage }, changes);
    }

    /// <summary>
    /// The site with the start page and the requested hosts: a host it has already takes the requested type, language and
    /// https setting; a new one is added. A requested primary (or edit) host makes the site's other one for that language
    /// undefined. Other hosts stay. A requested primary host for every language also sets SiteUrl (CMS 12).
    /// </summary>
    internal static (SiteState After, IReadOnlyList<string> Changes) Merge(SiteState existing, IReadOnlyList<SiteHost> requested, int startPage, bool applications)
    {
        var changes = new List<string>();
        if (existing.StartPage != startPage)
        {
            changes.Add($"start page {existing.StartPage?.ToString() ?? "none"} -> {startPage}");
        }
        var hosts = existing.Hosts.ToList();
        foreach (var host in requested)
        {
            if (host.Type is HostTypes.Primary or HostTypes.Edit)
            {
                for (var i = 0; i < hosts.Count; i++)
                {
                    var other = hosts[i];
                    if (other.Type == host.Type && !HostNames.Same(other.Name, host.Name)
                        && (host.Type == HostTypes.Edit || string.Equals(other.Language, host.Language, StringComparison.OrdinalIgnoreCase)))
                    {
                        hosts[i] = other with { Type = HostTypes.Undefined };
                        changes.Add($"{other.Name}: {other.Type} -> undefined");
                    }
                }
            }
            var index = hosts.FindIndex(h => HostNames.Same(h.Name, host.Name));
            if (index < 0)
            {
                hosts.Add(host);
                changes.Add($"added host {Describe(host)}");
                continue;
            }
            var current = hosts[index];
            var updated = current with
            {
                Type = host.Type,
                Language = host.Language,
                Https = host.Name == HostNames.Wildcard ? current.Https : host.Https,
            };
            if (updated != current)
            {
                hosts[index] = updated;
                changes.Add($"{current.Name}: {Describe(current)} -> {Describe(updated)}");
            }
        }
        var siteUrl = existing.SiteUrl;
        if (!applications && requested.FirstOrDefault(h => h is { Type: HostTypes.Primary, Language: null }) is { } primary)
        {
            var url = HostNames.SiteUrl(primary.Name, primary.Https ?? !HostNames.IsLoopback(primary.Name), existing.SiteUrl);
            if (!string.Equals(url, existing.SiteUrl, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add($"SiteUrl {existing.SiteUrl ?? "none"} -> {url}");
                siteUrl = url;
            }
        }
        return (existing with { Hosts = hosts, SiteUrl = siteUrl, StartPage = startPage }, changes);
    }

    private static string Describe(SiteHost host) =>
        $"{host.Name} ({HostTypes.Display(host.Type)}{(host.Language is null ? "" : $", {host.Language}")}{host.Https switch { true => ", https", false => ", http", null => "" }})";
}
