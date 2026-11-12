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

        var requestedAssets = body.SiteAssets is null
            ? null
            : SiteAssetsModes.Parse(body.SiteAssets) ?? throw AgentException.Usage($"siteAssets '{body.SiteAssets}' is not {SiteAssetsModes.Own} or {SiteAssetsModes.Global}.");
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
        var assets = PlanAssets(existing, requestedAssets, store.AssetsFolderBelow(startPage), others);
        if (assets.Issue is { } assetsIssue)
        {
            throw AgentException.Invalid(
                [.. issues, new ValidationIssue(null, assetsIssue)],
                issues.Count > 0 ? ValidationHint : "Nothing was saved. `opticli sites` lists every site's start page and assetsRoot.",
                AgentErrorReasons.SiteHosts);
        }
        if (issues.Count > 0)
        {
            throw AgentException.Invalid(issues, ValidationHint, AgentErrorReasons.SiteHosts);
        }

        var (after, changes) = existing is null
            ? New(name, requested, startPage, applications, store)
            : Merge(existing, requested, startPage, applications);
        if (assets.Change is { } assetsChange)
        {
            changes = [.. changes, assetsChange];
        }
        if (existing is not null && assets.Change is not null)
        {
            warnings.Add(assets.Mode == SiteAssetsModes.Own
                ? $"{name}'s /siteassets/ URLs now lead to its own folder: media it had in the global assets folder stays there, at /globalassets/."
                : $"{name}'s /siteassets/ URLs now lead to the global assets folder: what its own folder ({existing.AssetsRoot}) holds stays there, but no longer at /siteassets/.");
        }
        var status = existing is null ? SiteHostStatus.Created : changes.Count > 0 ? SiteHostStatus.Changed : SiteHostStatus.Unchanged;
        var saved = false;
        var assetsInfo = new SiteAssetsInfo(assets.Mode, assets.Folder, assets.Create ? true : null);
        if (!body.DryRun && status != SiteHostStatus.Unchanged)
        {
            try
            {
                after = await store.SaveSiteAsync(existing, after, startPage, assets, CancellationToken.None);
            }
            catch (SiteRefusedException ex)
            {
                throw AgentException.Invalid([new ValidationIssue(null, $"{name}: the CMS refused the site: {ex.Message}")], ValidationHint, AgentErrorReasons.SiteHosts);
            }
            saved = true;
            warnings.Add(SiteHostsOperation.RestartWarning);
            assetsInfo = new SiteAssetsInfo(after.AssetsRoot is null ? SiteAssetsModes.Global : SiteAssetsModes.Own, after.AssetsRoot, assetsInfo.Created);
        }

        var site = applications
            ? new SiteHostsSite(null, after.Name, status, changes, after.Hosts.Where(h => !SiteHostPlanner.IsDefaultApplication(h)).ToList(), after.SiteUrl)
            {
                Application = after.Key,
                IsDefault = after.Hosts.Any(SiteHostPlanner.IsDefaultApplication),
                SiteAssets = assetsInfo,
            }
            : new SiteHostsSite(after.Id, after.Name, status, changes, after.Hosts, after.SiteUrl) { SiteAssets = assetsInfo };
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

    /// <summary>
    /// What happens to the site's assets root. <paramref name="requested"/> null: <c>own</c> for a new site, an existing
    /// site's left as it is. <c>own</c> uses the start page's <c>SysSiteAssets</c> folder where it has one (as the CMS does),
    /// else creates one; a folder another site has as its root is an issue, since the CMS won't start with two such sites.
    /// </summary>
    /// <param name="existing">The site of that name (<c>--update-existing</c>); null for a new one.</param>
    /// <param name="folderBelowStart">The start page's <c>SysSiteAssets</c> folder; null when it has none.</param>
    /// <param name="others">The other sites.</param>
    internal static SiteAssetsPlan PlanAssets(SiteState? existing, string? requested, int? folderBelowStart, IReadOnlyList<SiteState> others)
    {
        var current = existing is null ? null : existing.AssetsRoot is null ? SiteAssetsModes.Global : SiteAssetsModes.Own;
        var wanted = requested ?? current ?? SiteAssetsModes.Own;
        if (wanted == current)
        {
            return new SiteAssetsPlan(wanted, existing!.AssetsRoot, Create: false, Change: null, Issue: null);
        }
        var prefix = current is null ? "site assets: " : $"site assets: {current} -> ";
        if (wanted == SiteAssetsModes.Global)
        {
            return new SiteAssetsPlan(wanted, null, Create: false, Change: $"{prefix}global (/globalassets/)", Issue: null);
        }
        if (folderBelowStart is { } folder)
        {
            var issue = others.FirstOrDefault(o => o.AssetsRoot == folder) is { } other
                ? $"{SiteAssetsModes.FolderName} ({folder}) below the start page is {other.Name}'s assets folder already, and the CMS doesn't start with two sites on one folder: use the global assets folder (--site-assets global, \"siteAssets\": \"global\" in a plan), or a start page of its own."
                : null;
            return new SiteAssetsPlan(wanted, folder, Create: false, Change: $"{prefix}own, the start page's {SiteAssetsModes.FolderName} folder ({folder})", issue);
        }
        return new SiteAssetsPlan(wanted, null, Create: true, Change: $"{prefix}own, a {SiteAssetsModes.FolderName} folder created below the start page", Issue: null);
    }

    private static string Describe(SiteHost host) =>
        $"{host.Name} ({HostTypes.Display(host.Type)}{(host.Language is null ? "" : $", {host.Language}")}{host.Https switch { true => ", https", false => ", http", null => "" }})";
}

/// <summary>What <c>sites add</c> does to a site's assets root (<see cref="SiteAddOperation.PlanAssets"/>).</summary>
/// <param name="Mode"><see cref="SiteAssetsModes"/>, as the site ends.</param>
/// <param name="Folder"><c>own</c>: the folder to use; null when one is to be created (or for <c>global</c>).</param>
/// <param name="Create">A <c>SysSiteAssets</c> folder is created below the start page.</param>
/// <param name="Change">The line for the site's changes; null when the root stays as it is.</param>
/// <param name="Issue">Why it can't be done; null when it can.</param>
internal sealed record SiteAssetsPlan(string Mode, int? Folder, bool Create, string? Change, string? Issue);
