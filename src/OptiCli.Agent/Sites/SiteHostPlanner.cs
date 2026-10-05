using OptiCli.Cms;
using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Agent.Sites;

/// <summary>A site definition as <see cref="SiteHostPlanner"/> sees it: no CMS types, so its rules are testable without a site.</summary>
/// <param name="SiteUrl">The site's URL (<c>SiteDefinition.SiteUrl</c>), as the CMS prints it.</param>
internal sealed record SiteState(Guid Id, string Name, string? SiteUrl, IReadOnlyList<SiteHost> Hosts);

/// <param name="Changes">What changed, one line each; empty when the site ends as it began.</param>
internal sealed record PlannedSite(SiteState Before, SiteState After, IReadOnlyList<string> Changes)
{
    public bool Changed => !SiteHostPlanner.Same(Before, After);

    /// <summary>The site loses a host: saved before the others, so a host moved within one batch is free when it is added.</summary>
    public bool RemovesHosts => Before.Hosts.Any(h => !After.Hosts.Any(a => HostNames.Same(a.Name, h.Name)));
}

/// <param name="Sites">Every site the changes named, once, in the order they named them.</param>
internal sealed record SiteHostPlan(IReadOnlyList<PlannedSite> Sites, IReadOnlyList<string> Warnings);

/// <summary>
/// Applies a <see cref="SiteHostsRequest"/>'s changes to copies of the sites and checks the result, before anything is
/// saved. The checks are the CMS's own (<c>ISiteDefinitionRepository.Save</c> throws an <see cref="ArgumentException"/>
/// for each) plus a few it doesn't make, so a dry run finds every problem a save would, and names the change that caused it.
/// </summary>
internal static class SiteHostPlanner
{
    public const string ValidationHint = "Fix the listed changes and retry; nothing was saved. `opticli sites` lists every site's hosts.";

    /// <param name="languages">The enabled language branches' codes.</param>
    /// <param name="sharedDatabase">Shared mode: only <see cref="SiteHostsRequest.AllowedOnSharedDatabase"/> changes pass.</param>
    /// <exception cref="AgentException">
    /// <c>usage</c> (no changes, an unknown action, type or https value), <c>refused</c> (shared mode, a site's last host),
    /// <c>not_found</c> (site, host to remove), <c>conflict</c> (adding a host the site has), <c>validation</c> with every
    /// issue found.
    /// </exception>
    public static SiteHostPlan Plan(IReadOnlyList<SiteState> sites, IReadOnlyList<SiteHostChange> changes, IReadOnlyCollection<string> languages, bool sharedDatabase)
    {
        if (changes.Count == 0)
        {
            throw AgentException.Usage("No changes: give at least one in changes.");
        }
        foreach (var change in changes)
        {
            CheckShape(change);
        }
        var work = sites.Select(s => new WorkingSite(s)).ToList();
        if (sharedDatabase && changes.FirstOrDefault(c => !SiteHostsRequest.AllowedOnSharedDatabase(c)) is { } refused)
        {
            throw AgentException.Refused($"{Describe(refused, Find(refused.Site, work)?.Name)}: {SiteHostsRequest.SharedRefusal}", SiteHostsRequest.SharedHint);
        }

        var touched = new List<WorkingSite>();
        var issues = new List<ValidationIssue>();
        var warnings = new List<string>();
        var sameLanguageTwice = false;
        foreach (var change in changes)
        {
            var site = Resolve(change.Site, work);
            if (!touched.Contains(site))
            {
                touched.Add(site);
            }
            var label = Describe(change, site.Name);
            site.LastChange = label;

            string? language = null;
            if (!string.IsNullOrWhiteSpace(change.Language))
            {
                language = languages.FirstOrDefault(l => l.Equals(change.Language.Trim(), StringComparison.OrdinalIgnoreCase));
                if (language is null)
                {
                    issues.Add(Issue(label, $"'{change.Language.Trim()}' is not an enabled language (enabled: {string.Join(", ", languages)})."));
                    continue;
                }
            }
            // The https setting is the scheme the host is reached by, so it decides which port is the default one.
            bool? flag = null;
            var flagGiven = change.Https is { } given && HostHttps.TryParse(given, out flag);
            var literal = HostNames.Literal(change.Host);
            if (!HostNames.TryNormalize(change.Host, out var name, out var schemeHttps, out var error, flag))
            {
                // A host the site has under a name opticli wouldn't write can still be removed.
                if (change.Action != SiteHostActions.Remove || site.Find(literal) is null)
                {
                    issues.Add(Issue(label, error));
                    continue;
                }
                name = literal;
            }
            // As typed first: a site's www.site.example:443 is that host, though opticli writes it www.site.example.
            var asTyped = site.Find(literal);
            var existing = asTyped ?? site.Find(name);
            // A scheme or an https setting is given. A default port alone says the scheme too, unless it is part of the
            // name of the host the site has (localhost:443 stored with http stays http): localhost:443 for the site's
            // localhost means https.
            var explicitScheme = flagGiven || change.Host.Contains("://", StringComparison.Ordinal);
            var https = flagGiven ? (true, flag) : (schemeHttps is not null && (explicitScheme || asTyped is null), schemeHttps);

            switch (change.Action)
            {
                case SiteHostActions.Primary:
                    MakePrimary(site, existing, name, language ?? Unqualified(site, [literal, name], languages, warnings), language is null, https, change, label, work, issues, ref sameLanguageTwice);
                    break;
                case SiteHostActions.Add:
                    Add(site, existing, name, HostTypes.Parse(change.Type ?? HostTypes.Undefined)!, language, https, label, work, issues, warnings);
                    break;
                default:
                    Remove(site, existing, name, label, issues, warnings);
                    break;
            }
        }

        // What the CMS does on save, so the plan (and the checks) have the host it would add.
        foreach (var site in touched)
        {
            site.AddSiteUrlHost();
        }
        foreach (var site in touched)
        {
            issues.AddRange(Rules(site, work.Where(w => w != site)));
        }
        if (issues.Count > 0)
        {
            throw AgentException.Invalid(
                issues,
                sameLanguageTwice
                    ? "A pair without @lang replaces the site's primary host, which for a site whose only primary host is bound to a language is that language's: give `Site=<host>` or `Site@<lang>=<host>`, not both. Nothing was saved."
                    : ValidationHint,
                AgentErrorReasons.SiteHosts);
        }

        foreach (var site in touched.Where(s => s.MadePrimary))
        {
            warnings.AddRange(ProductionLanguages(site, languages));
        }
        return new SiteHostPlan(touched.Select(s => s.ToPlanned()).ToList(), warnings);
    }

    /// <summary>Same hosts (in the same order, with the same settings) and the same site URL.</summary>
    public static bool Same(SiteState a, SiteState b) =>
        string.Equals(a.SiteUrl, b.SiteUrl, StringComparison.OrdinalIgnoreCase) && a.Hosts.SequenceEqual(b.Hosts);

    /// <summary>How a change reads in messages: <c>Site A@nb=localhost:5004</c> as <c>sites primary</c> takes it, else the command.</summary>
    public static string Describe(SiteHostChange change, string? siteName = null)
    {
        var site = siteName ?? change.Site;
        var language = string.IsNullOrWhiteSpace(change.Language) ? "" : $"@{change.Language.Trim()}";
        return change.Action switch
        {
            SiteHostActions.Primary => $"{site}{language}={change.Host}",
            SiteHostActions.Add => $"host add {site} {change.Host}{(change.Type is { } type ? $" --type {type}" : "")}{(language.Length > 0 ? $" --lang {language[1..]}" : "")}",
            _ => $"host remove {site} {change.Host}",
        };
    }

    private static void CheckShape(SiteHostChange change)
    {
        if (!SiteHostActions.All.Contains(change.Action))
        {
            throw AgentException.Usage($"'{change.Action}' is not an action.", $"Use {string.Join(", ", SiteHostActions.All)}.");
        }
        if (change.Type is { } type && (change.Action != SiteHostActions.Add || HostTypes.Parse(type) is null))
        {
            throw AgentException.Usage(
                change.Action == SiteHostActions.Add ? $"'{type}' is not a host type." : $"type only applies to {SiteHostActions.Add}.",
                $"Host types: {HostTypes.Syntax}.");
        }
        if (change.Https is { } https && !HostHttps.TryParse(https, out _))
        {
            throw AgentException.Usage($"https '{https}' is not one of {string.Join(", ", HostHttps.All)}.");
        }
        if (change.Action == SiteHostActions.Remove && (change.Https is not null || !string.IsNullOrWhiteSpace(change.Language)))
        {
            throw AgentException.Usage($"{SiteHostActions.Remove} takes only the site and the host.");
        }
        if ((change.KeepEdit || change.KeepSiteUrl) && change.Action != SiteHostActions.Primary)
        {
            throw AgentException.Usage($"keepEdit and keepSiteUrl only apply to {SiteHostActions.Primary}.");
        }
    }

    /// <summary>By GUID, else by name (case-insensitive), with close names in the hint.</summary>
    private static WorkingSite Resolve(string site, IReadOnlyList<WorkingSite> work)
    {
        var input = site.Trim();
        return Find(input, work) ?? throw AgentException.NotFound(
            $"No site named '{input}'.",
            Suggestions.DidYouMean(input, work.Select(w => w.Name)) ?? $"Sites: {string.Join(", ", work.Select(w => w.Name))}. The agent takes a site's name or GUID (`opticli sites`).");
    }

    private static WorkingSite? Find(string site, IReadOnlyList<WorkingSite> work)
    {
        var input = site.Trim();
        return Guid.TryParse(input, out var guid)
            ? work.FirstOrDefault(w => w.Id == guid)
            : work.FirstOrDefault(w => w.Name.Equals(input, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="existing">The site's host by that name (as typed, or normalised), if it has it.</param>
    /// <summary>
    /// The language a pair without @lang is for (<see cref="UnqualifiedPrimary.Language"/>), from the site as it was before
    /// the batch: the earlier pairs don't change it, so their order doesn't matter.
    /// </summary>
    private static string? Unqualified(WorkingSite site, IReadOnlyCollection<string> names, IReadOnlyCollection<string> languages, List<string> warnings)
    {
        var language = UnqualifiedPrimary.Language(site.Before.Hosts, names, languages);
        if (language is null && UnqualifiedPrimary.DisabledSoleLanguage(site.Before.Hosts, languages) is { } disabled)
        {
            warnings.Add($"{site.Name}'s only primary host is for '{disabled}', which isn't an enabled language: the new primary host is for every language.");
        }
        return language;
    }

    /// <param name="language">The language the host is for: the pair's, or for one without @lang (<paramref name="unqualified"/>) <see cref="Unqualified"/>.</param>
    private static void MakePrimary(WorkingSite site, SiteHost? existing, string name, string? language, bool unqualified, (bool Given, bool? Value) https, SiteHostChange change, string label, IReadOnlyList<WorkingSite> work, List<ValidationIssue> issues, ref bool sameLanguageTwice)
    {
        if (name == HostNames.Wildcard)
        {
            issues.Add(Issue(label, "the * host answers host names no site has; it can't be a primary host. Name the host, e.g. localhost:5001."));
            return;
        }
        if (existing is null && Owner(name, site, work) is { } owner)
        {
            issues.Add(Issue(label, OwnedElsewhere(name, owner)));
            return;
        }
        name = existing?.Name ?? name;

        if (site.PrimaryLanguages.TryGetValue(language ?? "", out var earlier))
        {
            if (language is not null && (unqualified || earlier.Unqualified))
            {
                sameLanguageTwice = true;
                issues.Add(Issue(label, $"`{earlier.Label}` already makes the primary host of {site.Name} for {language}: without @lang a pair is for {language} here, the language of the site's only primary host. Give one of the two."));
            }
            else
            {
                issues.Add(Issue(label, $"an earlier change already makes a primary host of {site.Name} for {LanguageText(language)}; give one per site and language."));
            }
            return;
        }
        site.PrimaryLanguages[language ?? ""] = (label, unqualified);
        site.MadePrimary = true;
        var final = existing is null
            ? new SiteHost(name, HostTypes.Primary, language, https.Value)
            : existing with { Type = HostTypes.Primary, Language = language, Https = https.Given ? https.Value : existing.Https };
        if (existing is null)
        {
            site.Hosts.Add(final);
            site.Changes.Add($"added {name} ({DescribeHost(final)})");
        }
        else
        {
            site.Replace(existing, final);
        }
        if (unqualified && language is not null)
        {
            site.Changes.Add($"{name} is the primary host for {language}: on this site a pair without @lang replaces the primary host for {language}");
        }

        var siteUrlHost = HostNames.FromUrl(site.SiteUrl);
        var replacedSiteUrlHost = false;
        foreach (var previous in site.Hosts.Where(h => h.Type == HostTypes.Primary && SameLanguage(h.Language, language) && !HostNames.Same(h.Name, name)).ToList())
        {
            replacedSiteUrlHost |= HostNames.Same(previous.Name, siteUrlHost);
            site.Replace(previous, previous with { Type = HostTypes.Undefined });
        }
        if (!change.KeepEdit)
        {
            // The CMS only allows an Edit host for every language, so it is the edit host of this language too.
            foreach (var edit in site.Hosts.Where(h => h.Type == HostTypes.Edit && !HostNames.Same(h.Name, name)).ToList())
            {
                site.Replace(edit, edit with { Type = HostTypes.Undefined });
            }
        }
        // SiteUrl follows a pair without @lang, and any pair whose primary host it was on.
        if ((unqualified || replacedSiteUrlHost) && !change.KeepSiteUrl)
        {
            site.SetSiteUrl(HostNames.SiteUrl(final.Name, final.Https, site.SiteUrl));
        }
    }

    private static void Add(WorkingSite site, SiteHost? existing, string name, string type, string? language, (bool Given, bool? Value) https, string label, IReadOnlyList<WorkingSite> work, List<ValidationIssue> issues, List<string> warnings)
    {
        if (existing is not null)
        {
            throw AgentException.Conflict(
                $"{site.Name} already has the host {existing.Name} ({DescribeHost(existing)}).",
                $"To make it the primary host: `opticli sites primary \"{site.Name}={existing.Name}\"`. To change it otherwise, remove it and add it again.");
        }
        if (Owner(name, site, work) is { } owner)
        {
            issues.Add(Issue(label, OwnedElsewhere(name, owner)));
            return;
        }
        if (name == HostNames.Wildcard && type is HostTypes.Primary or HostTypes.Edit)
        {
            issues.Add(Issue(label, $"the * host answers host names no site has; it can't be the {type} host."));
            return;
        }
        if (type == HostTypes.Edit && language is not null)
        {
            issues.Add(Issue(label, "an Edit host is for every language; leave out the language."));
            return;
        }

        var host = new SiteHost(name, type, language, https.Value);
        site.Hosts.Add(host);
        site.Changes.Add($"added {name} ({DescribeHost(host)})");
        if (type == HostTypes.Primary)
        {
            foreach (var previous in site.Hosts.Where(h => h.Type == HostTypes.Primary && SameLanguage(h.Language, language) && h != host).ToList())
            {
                site.Replace(previous, previous with { Type = HostTypes.Undefined });
            }
            if (language is null && !HostNames.Same(HostNames.FromUrl(site.SiteUrl), name))
            {
                warnings.Add($"{site.Name}'s SiteUrl stays {site.SiteUrl}: `opticli sites primary \"{site.Name}={name}\"` points it at the new primary host too.");
            }
        }
        else if (type == HostTypes.Edit)
        {
            foreach (var previous in site.Hosts.Where(h => h.Type == HostTypes.Edit && h != host).ToList())
            {
                site.Replace(previous, previous with { Type = HostTypes.Undefined });
            }
        }
    }

    private static void Remove(WorkingSite site, SiteHost? found, string name, string label, List<ValidationIssue> issues, List<string> warnings)
    {
        var existing = found ?? throw AgentException.NotFound(
            $"{site.Name} has no host {name}.",
            Suggestions.DidYouMean(name, site.Hosts.Select(h => h.Name)) ?? $"Its hosts: {string.Join(", ", site.Hosts.Select(h => h.Name))}.");
        if (site.Hosts.Count == 1)
        {
            throw AgentException.Refused(
                $"{existing.Name} is {site.Name}'s last host; a site needs at least one.",
                $"Add the host it should have first (`opticli sites host add \"{site.Name}\" <host>`), then remove this one.");
        }
        if (HostNames.Same(HostNames.FromUrl(site.SiteUrl), existing.Name))
        {
            issues.Add(Issue(label,
                $"{existing.Name} is the host of {site.Name}'s SiteUrl ({site.SiteUrl}), which the CMS adds back as a host whenever the site is saved. " +
                $"Make another host primary first: `opticli sites primary \"{site.Name}=<host>\"`."));
            return;
        }

        site.Hosts.Remove(existing);
        site.Changes.Add($"removed {existing.Name} ({DescribeHost(existing)})");
        if (existing.Name == HostNames.Wildcard)
        {
            warnings.Add($"{site.Name} no longer has the * host: it only answers on its own hosts ({string.Join(", ", site.Hosts.Select(h => h.Name))}), and a host name no site has reaches none.");
        }
    }

    /// <summary>The CMS's checks on save (<c>DefaultSiteDefinitionRepository.ValidateDefinition</c>), on the end state.</summary>
    private static IEnumerable<ValidationIssue> Rules(WorkingSite site, IEnumerable<WorkingSite> others)
    {
        var label = site.LastChange!;
        var otherSites = others.ToList();
        foreach (var name in site.Hosts.GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            yield return Issue(label, $"{site.Name} would have the host {name} twice.");
        }
        foreach (var host in site.Hosts)
        {
            if (otherSites.FirstOrDefault(o => o.Find(host.Name) is not null) is { } owner)
            {
                yield return Issue(label, OwnedElsewhere(host.Name, owner));
            }
            if (host.Name == HostNames.Wildcard && host.Type is HostTypes.Primary or HostTypes.Edit)
            {
                yield return Issue(label, $"{site.Name}'s * host can't be its {host.Type} host.");
            }
            if (host.Type == HostTypes.Edit && host.Language is not null)
            {
                yield return Issue(label, $"{site.Name}'s Edit host {host.Name} has the language {host.Language}; an Edit host is for every language.");
            }
        }
        foreach (var primaries in site.Hosts.Where(h => h.Type == HostTypes.Primary).GroupBy(h => h.Language?.ToLowerInvariant()).Where(g => g.Count() > 1))
        {
            yield return Issue(label, $"{site.Name} would have {primaries.Count()} primary hosts for {LanguageText(primaries.First().Language)}: {string.Join(", ", primaries.Select(h => h.Name))}.");
        }
        var edits = site.Hosts.Count(h => h.Type == HostTypes.Edit);
        if (edits > 1)
        {
            yield return Issue(label, $"{site.Name} would have {edits} Edit hosts; a site has one at most.");
        }
        if (edits == 1 && site.Hosts.Count == 1)
        {
            yield return Issue(label, $"{site.Name} would only have its Edit host; it needs another one to serve the site.");
        }
        foreach (var group in site.Hosts.GroupBy(h => h.Language?.ToLowerInvariant()))
        {
            if (group.Any(h => HostTypes.IsRedirect(h.Type)) && !group.Any(h => h.Type is HostTypes.Primary or HostTypes.Undefined))
            {
                yield return Issue(label, $"{site.Name}'s redirecting hosts for {LanguageText(group.First().Language)} would have no primary or undefined host to redirect to.");
            }
            if (group.Any(h => h.Name == HostNames.Wildcard) && !group.Any(h => h.Name != HostNames.Wildcard && h.Type is HostTypes.Primary or HostTypes.Undefined))
            {
                yield return Issue(label, $"{site.Name} would have the * host but no other primary or undefined host for {LanguageText(group.First().Language)} to generate links with.");
            }
        }
    }

    /// <summary>
    /// Languages whose links still go to a production host after a <c>primary</c> change: the CMS uses a language's own
    /// primary host, else its first undefined one (<c>SiteDefinition.GetPrimaryHost</c>), before any host for every language.
    /// </summary>
    private static IEnumerable<string> ProductionLanguages(WorkingSite site, IReadOnlyCollection<string> languages)
    {
        foreach (var group in site.Hosts.Where(h => h.Language is not null && h.Name != HostNames.Wildcard).GroupBy(h => h.Language!, StringComparer.OrdinalIgnoreCase))
        {
            var used = group.FirstOrDefault(h => h.Type == HostTypes.Primary) ?? group.FirstOrDefault(h => h.Type == HostTypes.Undefined);
            if (used is not null && !HostNames.IsLocal(used.Name))
            {
                // The pair that works for that language: without @lang when the site's only primary host is for it.
                var pair = SameLanguage(UnqualifiedPrimary.SoleLanguage(site.Hosts, languages), group.Key) ? site.Name : $"{site.Name}@{group.Key}";
                yield return $"{site.Name}'s {group.Key} URLs still use {used.Name}; add `{pair}=localhost:<port>`.";
            }
        }
    }

    private static WorkingSite? Owner(string name, WorkingSite site, IEnumerable<WorkingSite> work) =>
        work.FirstOrDefault(w => w != site && w.Find(name) is not null);

    private static string OwnedElsewhere(string name, WorkingSite owner) => name == HostNames.Wildcard
        ? $"{owner.Name} has the * host already; only one site can answer the host names no site has."
        : $"{name} belongs to {owner.Name}; a host can be on one site only (remove it there first: `opticli sites host remove \"{owner.Name}\" {name}`).";

    private static ValidationIssue Issue(string label, string message) => new(null, $"{label}: {message}");

    private static bool SameLanguage(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string LanguageText(string? language) => language is null ? "every language" : language;

    /// <summary><c>primary</c>, <c>primary, nb</c>, <c>undefined, https</c>.</summary>
    internal static string DescribeHost(SiteHost host) => string.Join(", ", new[]
    {
        HostTypes.Display(host.Type),
        host.Language,
        host.Https switch { true => "https", false => "http", null => null },
    }.OfType<string>());

    /// <summary>One site while the changes are applied: its hosts, its URL and what happened to them.</summary>
    private sealed class WorkingSite(SiteState state)
    {
        public SiteState Before { get; } = state;

        public Guid Id => Before.Id;

        public string Name => Before.Name;

        public string? SiteUrl { get; private set; } = state.SiteUrl;

        public List<SiteHost> Hosts { get; } = [.. state.Hosts];

        public List<string> Changes { get; } = [];

        /// <summary>The last change made to this site, which the end-state checks name.</summary>
        public string? LastChange { get; set; }

        /// <summary>A <c>primary</c> change named this site; and which languages (null: every language) it made primary.</summary>
        public bool MadePrimary { get; set; }

        /// <summary>Language ("" for every language) to the change that makes its primary host, and whether that change had no @lang.</summary>
        public Dictionary<string, (string Label, bool Unqualified)> PrimaryLanguages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SiteHost? Find(string name) => Hosts.FirstOrDefault(h => HostNames.Same(h.Name, name));

        public void Replace(SiteHost existing, SiteHost updated)
        {
            if (existing == updated)
            {
                return;
            }
            Hosts[Hosts.IndexOf(existing)] = updated;
            Changes.Add($"{existing.Name}: {DescribeHost(existing)} → {DescribeHost(updated)}");
        }

        /// <summary>
        /// <c>DefaultSiteDefinitionRepository.Save</c>'s <c>EnsureHostsContainsSiteUrl</c>: SiteUrl's host (its
        /// <c>Uri.Authority</c>) is added when the site doesn't have it, as primary when there is no primary host for
        /// every language.
        /// </summary>
        public void AddSiteUrlHost()
        {
            if (HostNames.FromUrl(SiteUrl) is not { } authority || Find(authority) is not null)
            {
                return;
            }
            var type = Hosts.Any(h => h.Type == HostTypes.Primary && h.Language is null) ? HostTypes.Undefined : HostTypes.Primary;
            var host = new SiteHost(authority, type, null, null);
            Hosts.Add(host);
            Changes.Add($"added {authority} ({DescribeHost(host)}): SiteUrl's host, which the CMS adds when it saves the site");
        }

        public void SetSiteUrl(string url)
        {
            if (!string.Equals(SiteUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                Changes.Add($"SiteUrl: {SiteUrl ?? "(none)"} → {url}");
                SiteUrl = url;
            }
        }

        public PlannedSite ToPlanned()
        {
            var after = Before with { SiteUrl = SiteUrl, Hosts = Hosts.ToList() };
            return new PlannedSite(Before, after, SiteHostPlanner.Same(Before, after) ? [] : Changes);
        }
    }
}
