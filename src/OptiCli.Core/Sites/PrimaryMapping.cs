using OptiCli.Core.Cms;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Core.Sites;

/// <summary>One entry of the saved <c>sites primary</c> mapping, compared with the database.</summary>
/// <param name="Key"><c>Site A</c> or <c>Site A@nb</c>.</param>
/// <param name="Saved">The host the mapping says, as saved.</param>
/// <param name="Primary">The site's primary host for that language now; null when it has none (or the site is gone).</param>
/// <param name="Status">One of <see cref="PrimaryMapping"/>'s statuses.</param>
public sealed record SavedPrimary(string Key, string Saved, string? Primary, string Status);

/// <summary>The saved <c>sites primary</c> mapping against the database's sites: what <c>doctor</c> and <c>--from-config</c> read.</summary>
public static class PrimaryMapping
{
    public const string Matches = "matches";
    public const string Differs = "differs";
    public const string NoSite = "noSite";
    public const string Invalid = "invalid";

    public const string FromConfigCommand = "opticli sites primary --from-config";

    /// <summary>The <c>--forget</c> command that drops a saved entry (and the site's other entries).</summary>
    public static string ForgetCommand(string key) => $"opticli sites primary --forget \"{key}\"";

    /// <summary>
    /// The mapping as pairs, for <c>--from-config</c>, with the options saved for each. Entries for a site the database
    /// doesn't have (deleted, renamed, another database) are left out and named in <paramref name="warnings"/>.
    /// </summary>
    /// <param name="keepEdit">The command's <c>--keep-edit</c>, on top of each entry's own.</param>
    /// <exception cref="NotFoundException">No saved site exists in this database.</exception>
    /// <exception cref="UsageException">An entry isn't a host name.</exception>
    public static IReadOnlyList<PrimaryPair> Pairs(IReadOnlyDictionary<string, SavedPrimaryHost> saved, IReadOnlyList<SiteInfo> sites, IReadOnlyCollection<string> languages, string? https, bool keepEdit, bool keepSiteUrl, List<string> warnings)
    {
        var pairs = new List<PrimaryPair>();
        var missing = new List<string>();
        foreach (var (key, value) in saved)
        {
            try
            {
                pairs.Add(PrimaryPairs.Pair(key, value.Host, sites, languages, https) with
                {
                    KeepEdit = keepEdit || value.KeepEdit,
                    KeepSiteUrl = keepSiteUrl || value.KeepSiteUrl,
                });
            }
            catch (NotFoundException)
            {
                missing.Add(key);
            }
            catch (UsageException ex)
            {
                throw new UsageException($"The saved sites mapping has an entry that isn't a site and host: {ex.Message}", $"Drop it with `{ForgetCommand(key)}`, then save the pair again with --save.");
            }
        }
        if (pairs.Count == 0)
        {
            throw new NotFoundException(
                $"None of the sites in the saved mapping ({string.Join(", ", missing)}) is in this database.",
                $"`opticli sites` lists its sites. Drop an entry with `{ForgetCommand(missing[0])}`, and save the pairs for these sites with `opticli sites primary \"<site>=<host>\" --save`.");
        }
        warnings.AddRange(missing.Select(key => $"Skipped '{key}' from the saved mapping: this database has no such site. Drop it with `{ForgetCommand(key)}`."));
        // The options are the site's: one entry's keepEdit keeps the Edit host that the site's other entries would demote.
        return pairs.Select(p =>
        {
            var site = pairs.Where(o => o.Site.Id == p.Site.Id).ToList();
            return p with { KeepEdit = site.Any(o => o.KeepEdit), KeepSiteUrl = site.Any(o => o.KeepSiteUrl) };
        }).ToList();
    }

    /// <summary>Each saved entry against the site's primary host for its language now.</summary>
    public static IReadOnlyList<SavedPrimary> Compare(IReadOnlyDictionary<string, SavedPrimaryHost> saved, IReadOnlyList<SiteInfo> sites, IReadOnlyCollection<string> languages)
    {
        var result = new List<SavedPrimary>();
        foreach (var (key, entry) in saved.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            var value = entry.Host;
            PrimaryPair pair;
            try
            {
                pair = PrimaryPairs.Pair(key, value, sites, languages);
            }
            catch (NotFoundException)
            {
                result.Add(new SavedPrimary(key, value, null, NoSite));
                continue;
            }
            catch (UsageException)
            {
                result.Add(new SavedPrimary(key, value, null, Invalid));
                continue;
            }
            // A saved entry without a language is for the language the agent plans it for: the same rule, so doctor and
            // --from-config agree.
            var language = pair.Language ?? UnqualifiedPrimary.Language(pair.Site.Hosts.Select(SiteHostsRunner.ToSiteHost).ToList(), [pair.Host, HostNames.Literal(pair.Typed)], languages);
            var primary = pair.Site.Hosts.FirstOrDefault(h => h.Type == HostType.Primary && string.Equals(h.Language, language, StringComparison.OrdinalIgnoreCase));
            var same = primary is not null
                && (HostNames.Same(primary.Name, pair.Host) || HostNames.Same(primary.Name, HostNames.Literal(pair.Typed)))
                && (pair.Https is null || primary.Https == (pair.Https == HostHttps.True ? true : pair.Https == HostHttps.False ? false : null));
            result.Add(new SavedPrimary(key, value, primary?.Name, same ? Matches : Differs));
        }
        return result;
    }

    /// <summary>What <c>doctor</c> warns about: the entries that don't match, and what to run.</summary>
    public static IEnumerable<string> Warnings(IEnumerable<SavedPrimary> entries) =>
        entries.Select(Warning).OfType<string>();

    private static string? Warning(SavedPrimary entry) => entry.Status switch
    {
        NoSite => $"The saved sites mapping names '{entry.Key}', and this database has no such site (`opticli sites` lists them); --from-config skips it. Drop it with `{ForgetCommand(entry.Key)}`, and save the site under its name now with --save.",
        Invalid => $"The saved sites mapping has '{entry.Key}': '{entry.Saved}', which isn't a host name. Drop it with `{ForgetCommand(entry.Key)}`, then save the pair again with --save.",
        Differs when entry.Primary is null => $"{entry.Key} has no primary host; the saved mapping says {entry.Saved}. Run `{FromConfigCommand}`.",
        Differs when HostNames.Same(entry.Primary, HostOf(entry.Saved)) => $"{entry.Key}'s primary is {entry.Primary}, with another https setting than the saved mapping's {entry.Saved}. Run `{FromConfigCommand}`.",
        Differs => $"{entry.Key}'s primary is {entry.Primary}; the saved mapping says {entry.Saved}. Run `{FromConfigCommand}`.",
        _ => null,
    };

    private static string HostOf(string saved) => HostNames.TryNormalize(saved, out var name, out _, out _) ? name : saved;
}
