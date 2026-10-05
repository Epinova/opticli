namespace OptiCli.Protocol;

/// <summary>
/// Body of <see cref="AgentRoutes.SiteHosts"/>: change the host names of site definitions, typically to point a restored
/// copy of a production database at the ports the site listens on locally.
/// </summary>
/// <remarks>
/// <para>The changes are one batch: they are applied in order to a copy of every site, the result is validated as the CMS
/// would validate it on save (and a little stricter), and only then is any site saved. A site the batch leaves as it was
/// is reported as <see cref="SiteHostStatus.Unchanged"/> and not saved, so the same request can be sent again.</para>
/// <para>Site definitions aren't versioned: <see cref="SiteHostsResult"/> is the only record of what changed.</para>
/// </remarks>
public sealed record SiteHostsRequest
{
    public required IReadOnlyList<SiteHostChange> Changes { get; init; }

    /// <summary>Plan and validate without saving; the response shows each site's end state.</summary>
    public bool DryRun { get; init; }

    /// <summary>Why a change is refused against a shared database (<see cref="AllowedOnSharedDatabase"/>).</summary>
    public const string SharedRefusal =
        "opticli serve runs against a shared database here, whose site definitions the deployed development site uses too: changing its primary hosts or removing hosts would change its URLs for everyone.";

    public const string SharedHint =
        "Change a shared database's sites in its own admin UI (Settings > Websites). `opticli sites host add <site> localhost:<port>` makes a site reachable locally without changing it for anyone else.";

    /// <summary>
    /// Whether <paramref name="change"/> may be made in shared mode: only adding an extra host of type undefined, which
    /// changes no URL the deployed site generates.
    /// </summary>
    public static bool AllowedOnSharedDatabase(SiteHostChange change) =>
        change.Action == SiteHostActions.Add && HostTypes.Parse(change.Type ?? HostTypes.Undefined) == HostTypes.Undefined;
}

/// <summary>One change to one site's hosts.</summary>
public sealed record SiteHostChange
{
    /// <summary>The site's GUID, or its name (case-insensitive).</summary>
    public required string Site { get; init; }

    /// <summary><c>name[:port]</c>, or <c>*</c>; a URL with nothing after the host is read as its host.</summary>
    public required string Host { get; init; }

    /// <summary>One of <see cref="SiteHostActions"/>.</summary>
    public required string Action { get; init; }

    /// <summary>For <see cref="SiteHostActions.Add"/>: one of <see cref="HostTypes"/>; default undefined.</summary>
    public string? Type { get; init; }

    /// <summary>The language the host is for (an enabled language branch); null for every language, as most hosts are.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// One of <see cref="HostHttps"/>: whether the CMS generates <c>https://</c> links to the host. Null leaves an existing
    /// host's setting as it is, and gives a new host <see cref="HostHttps.Unset"/>.
    /// </summary>
    public string? Https { get; init; }

    /// <summary>For <see cref="SiteHostActions.Primary"/>: leave the site's Edit host as it is instead of making it undefined.</summary>
    public bool KeepEdit { get; init; }

    /// <summary>For <see cref="SiteHostActions.Primary"/> without a language: leave the site's URL (SiteUrl) as it is.</summary>
    public bool KeepSiteUrl { get; init; }
}

/// <summary>Values of <see cref="SiteHostChange.Action"/>.</summary>
public static class SiteHostActions
{
    /// <summary>
    /// Make the host the site's primary host for its language, adding it when the site doesn't have it. The previous
    /// primary for that language and the site's Edit host become undefined; without a language, SiteUrl follows.
    /// </summary>
    public const string Primary = "primary";

    /// <summary>Add a host the site doesn't have yet (<c>conflict</c> when it has), of <see cref="SiteHostChange.Type"/>.</summary>
    public const string Add = "add";

    /// <summary>Remove one host; never the site's last one.</summary>
    public const string Remove = "remove";

    public static readonly IReadOnlyList<string> All = [Primary, Add, Remove];
}

/// <summary>Values of <see cref="SiteHostChange.Https"/>.</summary>
public static class HostHttps
{
    public const string True = "true";

    public const string False = "false";

    /// <summary>No setting: links to the host use the scheme of the site's URL.</summary>
    public const string Unset = "unset";

    public static readonly IReadOnlyList<string> All = [True, False, Unset];

    /// <summary>The <c>UseSecureConnection</c> value for <paramref name="value"/>; false when it isn't one of <see cref="All"/>.</summary>
    public static bool TryParse(string value, out bool? https)
    {
        https = null;
        switch (value.Trim().ToLowerInvariant())
        {
            case True:
                https = true;
                return true;
            case False:
                https = false;
                return true;
            case Unset:
                return true;
            default:
                return false;
        }
    }

    public static string Format(bool? https) => https switch { true => True, false => False, null => Unset };
}

/// <summary>
/// Host types (<c>EPiServer.Web.HostDefinitionType</c>), named as <c>opticli sites</c> prints them; <see cref="Parse"/>
/// also takes <c>redirect-permanent</c>.
/// </summary>
public static class HostTypes
{
    public const string Undefined = "undefined";
    public const string Primary = "primary";
    public const string RedirectPermanent = "redirectPermanent";
    public const string RedirectTemporary = "redirectTemporary";
    public const string Edit = "edit";

    /// <summary>The names, in the order of their values (0 to 4).</summary>
    private static readonly string[] ByValue = [Undefined, Primary, RedirectPermanent, RedirectTemporary, Edit];

    public const string Syntax = "undefined, primary, edit, redirect-permanent or redirect-temporary";

    public static string FromValue(int value) => value >= 0 && value < ByValue.Length ? ByValue[value] : $"type{value}";

    public static int ToValue(string type) => Array.IndexOf(ByValue, type);

    /// <summary>The type <paramref name="text"/> names, case-insensitively and with or without dashes; null when none.</summary>
    public static string? Parse(string text)
    {
        var compact = text.Replace("-", "", StringComparison.Ordinal).Trim();
        return ByValue.FirstOrDefault(t => t.Equals(compact, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsRedirect(string type) => type is RedirectPermanent or RedirectTemporary;

    /// <summary>How the CLI spells a type (<c>--type redirect-permanent</c>), for messages.</summary>
    public static string Display(string type) => type switch
    {
        RedirectPermanent => "redirect-permanent",
        RedirectTemporary => "redirect-temporary",
        _ => type,
    };
}

/// <summary>
/// The language a <see cref="SiteHostActions.Primary"/> change without one is for: it replaces the site's primary host.
/// The agent plans with this on each site as it was before the batch, and <c>doctor</c> compares the saved mapping with
/// it on the sites as they are, so the two can't disagree, and the same pairs give the same plan whatever their order.
/// </summary>
/// <remarks>
/// In order: a host that is a primary host already keeps its language, so running the pairs again changes nothing;
/// else the primary host for every language, when the site has one; else the site's only primary host, when it is
/// bound to an enabled language (a site whose hosts are all for one language); else every language, beside the
/// primary hosts of the languages (several, or one that isn't enabled).
/// </remarks>
public static class UnqualifiedPrimary
{
    /// <param name="hosts">The site's hosts.</param>
    /// <param name="names">The pair's host, as the site may have it (as typed and normalised).</param>
    /// <param name="enabled">The enabled language branches' codes.</param>
    /// <returns>Null for every language; else the language.</returns>
    public static string? Language(IReadOnlyCollection<SiteHost> hosts, IReadOnlyCollection<string> names, IReadOnlyCollection<string> enabled)
    {
        var primaries = hosts.Where(h => h.Type == HostTypes.Primary).ToList();
        if (primaries.FirstOrDefault(h => names.Contains(h.Name, StringComparer.OrdinalIgnoreCase)) is { } already)
        {
            return already.Language;
        }
        return SoleLanguage(primaries, enabled);
    }

    /// <summary>The language of the site's only primary host when it is bound to an enabled language and no primary host is for every language.</summary>
    public static string? SoleLanguage(IReadOnlyCollection<SiteHost> hosts, IReadOnlyCollection<string> enabled)
    {
        var primaries = hosts.Where(h => h.Type == HostTypes.Primary).ToList();
        if (primaries.Count != 1 || primaries[0].Language is not { } language)
        {
            return null;
        }
        return enabled.FirstOrDefault(l => l.Equals(language, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The language of the site's only primary host when that language isn't enabled (so a pair without @lang is for every language).</summary>
    public static string? DisabledSoleLanguage(IReadOnlyCollection<SiteHost> hosts, IReadOnlyCollection<string> enabled)
    {
        var primaries = hosts.Where(h => h.Type == HostTypes.Primary).ToList();
        return primaries.Count == 1 && primaries[0].Language is { } language && !enabled.Contains(language, StringComparer.OrdinalIgnoreCase) ? language : null;
    }
}

/// <summary>Response of <see cref="AgentRoutes.SiteHosts"/>: every site the request named, once, in the order it named them.</summary>
public sealed record SiteHostsResult
{
    public required IReadOnlyList<SiteHostsSite> Sites { get; init; }

    public bool DryRun { get; init; }

    /// <summary>True when at least one site was saved.</summary>
    public bool Saved { get; init; }

    /// <summary>Things to act on: a language whose URLs still use a production host, the restart a save needs, ...</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
}

/// <param name="Id">The site's GUID (<c>SiteDefinition.Id</c>).</param>
/// <param name="Status">One of <see cref="SiteHostStatus"/>.</param>
/// <param name="Changes">One line per change: added, made primary, demoted, removed, SiteUrl old → new. Empty when unchanged.</param>
/// <param name="Hosts">The site's hosts after the change (for a dry run: what they would be).</param>
/// <param name="SiteUrl">The site's URL after the change.</param>
public sealed record SiteHostsSite(Guid Id, string Name, string Status, IReadOnlyList<string> Changes, IReadOnlyList<SiteHost> Hosts, string? SiteUrl);

/// <param name="Type">One of <see cref="HostTypes"/>.</param>
/// <param name="Language">The language code, null for every language.</param>
/// <param name="Https"><c>UseSecureConnection</c>: null uses the scheme of the site's URL.</param>
public sealed record SiteHost(string Name, string Type, string? Language, bool? Https);

/// <summary>Values of <see cref="SiteHostsSite.Status"/>.</summary>
public static class SiteHostStatus
{
    public const string Changed = "changed";

    public const string Unchanged = "unchanged";
}
