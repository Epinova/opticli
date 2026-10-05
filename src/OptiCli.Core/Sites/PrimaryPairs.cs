using System.Globalization;
using OptiCli.Core.Cms;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Core.Sites;

/// <summary>One <c>sites primary</c> pair: the host a site (in one language, or every language) should have as its primary host.</summary>
/// <param name="Language">The enabled language the host is primary for; null for every language.</param>
/// <param name="Host"><c>name[:port]</c>, normalised (<see cref="HostNames.TryNormalize"/>).</param>
/// <param name="Https">One of <see cref="HostHttps"/>, from the pair's scheme or <c>--https</c>; null leaves an existing host's setting (a port alone, as in <c>localhost:443</c>, gives none).</param>
public sealed record PrimaryPair(SiteInfo Site, string? Language, string Host, string? Https)
{
    /// <summary><c>--keep-edit</c>: leave the site's Edit host as it is.</summary>
    public bool KeepEdit { get; init; }

    /// <summary><c>--keep-site-url</c>: leave the site's URL as it is.</summary>
    public bool KeepSiteUrl { get; init; }

    /// <summary>
    /// The host as given, for the site: it finds a host the site has under a name opticli would write otherwise
    /// (<c>www.site.example:443</c>), and is normalised there the same way. Default: <see cref="Host"/>.
    /// </summary>
    public string Typed { get; init; } = Host;

    /// <summary>The saved mapping's key: the site's name, with <c>@lang</c> for a language.</summary>
    public string Key => Language is null ? Site.Name : $"{Site.Name}@{Language}";

    /// <summary>
    /// The scheme came from the port alone (<c>localhost:443</c>): <see cref="Host"/> left the port out, and
    /// <see cref="Https"/> is null.
    /// </summary>
    public bool Inferred { get; init; }

    /// <summary>
    /// The saved mapping's value: the host, as <c>https://host</c> (or <c>http://</c>) when the scheme was given, and as
    /// typed when only its port says the scheme, so <c>--from-config</c> sends what the command line did.
    /// </summary>
    public string Value => Https switch
    {
        HostHttps.True => $"https://{Host}",
        HostHttps.False => $"http://{Host}",
        _ => Inferred ? HostNames.Literal(Typed) : Host,
    };

    /// <summary>The saved mapping's entry, with the options given for it.</summary>
    public SavedPrimaryHost Saved => new(Value, KeepEdit, KeepSiteUrl);

    public override string ToString() => $"{Key}={Value}";
}

/// <summary>Reads <c>sites primary</c> pairs and the sites and languages they name, against the database's sites.</summary>
public static class PrimaryPairs
{
    public const string Syntax = "<site>[@<lang>]=<host>, e.g. \"Site A=localhost:5001\" or \"Site A@nb=https://localhost:5004\"";

    /// <param name="languages">The codes of the enabled language branches.</param>
    /// <param name="https">The <c>--https</c> option (one of <see cref="HostHttps"/>), which overrides the pair's scheme.</param>
    /// <exception cref="UsageException">Not a pair, or not a host name.</exception>
    /// <exception cref="NotFoundException">No such site.</exception>
    public static PrimaryPair Parse(string text, IReadOnlyList<SiteInfo> sites, IReadOnlyCollection<string> languages, string? https = null)
    {
        // Host names never contain '=', site names may.
        var equals = text.LastIndexOf('=');
        if (equals <= 0 || equals == text.Length - 1)
        {
            throw new UsageException($"'{text}' is not <site>=<host>.", $"Pairs are {Syntax}.");
        }
        return Pair(text[..equals].Trim(), text[(equals + 1)..].Trim(), sites, languages, https);
    }

    /// <summary>A saved mapping entry (<see cref="PrimaryPair.Key"/>, <see cref="PrimaryPair.Value"/>).</summary>
    public static PrimaryPair Pair(string key, string value, IReadOnlyList<SiteInfo> sites, IReadOnlyCollection<string> languages, string? https = null)
    {
        var (site, language) = SiteAndLanguage(key, sites, languages);
        // --https is the scheme the host is reached by, so it decides which port is the scheme's default one.
        bool? flag = null;
        var overridden = https is not null && HostHttps.TryParse(https, out flag);
        if (!HostNames.TryNormalize(value, out var host, out var scheme, out var error, flag))
        {
            throw new UsageException($"{key}={value}: {error}", $"Pairs are {Syntax}.");
        }
        // Only a scheme or --https is sent as the https setting: a port alone (localhost:443) says the scheme of a new host
        // only, so the site decides, and an existing host keeps its setting. The mapping saves such a host as typed.
        var explicitScheme = overridden || value.Contains("://", StringComparison.Ordinal);
        return new PrimaryPair(site, language, host, overridden ? https : explicitScheme && scheme is not null ? HostHttps.Format(scheme) : null)
        {
            Typed = value.Trim(),
            Inferred = !explicitScheme && scheme is not null,
        };
    }

    /// <summary>
    /// <c>Site A</c> or <c>Site A@nb</c>. A trailing <c>@code</c> is a language only when the whole text isn't a site's name and
    /// <c>code</c> is an enabled language; otherwise it is part of the site name.
    /// </summary>
    /// <exception cref="NotFoundException">No such site.</exception>
    public static (SiteInfo Site, string? Language) SiteAndLanguage(string text, IReadOnlyList<SiteInfo> sites, IReadOnlyCollection<string> languages)
    {
        if (Find(text, sites) is { } whole)
        {
            return (whole, null);
        }
        var at = text.LastIndexOf('@');
        if (at > 0 && languages.FirstOrDefault(l => l.Equals(text[(at + 1)..].Trim(), StringComparison.OrdinalIgnoreCase)) is { } language)
        {
            return (RequireSite(text[..at], sites), language);
        }
        if (at > 0 && Find(text[..at], sites) is { } site)
        {
            throw new NotFoundException(
                $"No site '{text.Trim()}'.",
                $"'{text[(at + 1)..].Trim()}' isn't an enabled language (enabled: {string.Join(", ", languages)}), so the whole of '{text.Trim()}' was read as a site name; the site is '{site.Name}'.");
        }
        return (RequireSite(text, sites), null);
    }

    /// <summary>A site by name (case-insensitive), id or GUID, as <c>opticli sites</c> prints them.</summary>
    /// <exception cref="NotFoundException">No such site; the hint has close names.</exception>
    public static SiteInfo RequireSite(string text, IReadOnlyList<SiteInfo> sites) =>
        Find(text, sites) ?? throw new NotFoundException(
            $"No site '{text.Trim()}'.",
            Suggestions.DidYouMean(text.Trim(), sites.Select(s => s.Name)) is { } close
                ? $"{close} Sites are named by name, id or GUID, as `opticli sites` lists them."
                : $"Sites: {string.Join(", ", sites.Select(s => s.Name))} (`opticli sites` lists them).");

    private static SiteInfo? Find(string text, IReadOnlyList<SiteInfo> sites)
    {
        var input = text.Trim();
        if (sites.FirstOrDefault(s => s.Name.Equals(input, StringComparison.OrdinalIgnoreCase)) is { } byName)
        {
            return byName;
        }
        if (int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return sites.FirstOrDefault(s => s.Id == id);
        }
        return Guid.TryParse(input, out var guid) ? sites.FirstOrDefault(s => s.Guid == guid) : null;
    }
}
