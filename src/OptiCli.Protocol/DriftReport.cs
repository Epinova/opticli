using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace OptiCli.Protocol;

/// <summary>Values of <see cref="DriftItem.Ahead"/> and <see cref="DriftReport.Ahead"/>: which side has what the other lacks.</summary>
public static class DriftAhead
{
    /// <summary>The local build has it and the database doesn't: the branch has changes that aren't deployed there.</summary>
    public const string Local = "local";

    /// <summary>The database has it and the local build doesn't: the environment runs newer code than the checkout.</summary>
    public const string Database = "database";

    /// <summary>Some differences one way, some the other.</summary>
    public const string Both = "both";

    /// <summary>They differ, and the database doesn't say which side changed (a property's settings, a store's mapping).</summary>
    public const string Unknown = "unknown";

    /// <summary>What it means, for messages: "local is ahead (...)".</summary>
    public static string Describe(string? ahead) => ahead switch
    {
        Local => "local is ahead: this build has changes the database's environment doesn't",
        Database => "the database is ahead: its environment runs newer code than this checkout",
        Both => "both are ahead: this build and the database's environment each have changes the other lacks",
        _ => "they differ, and the database doesn't say which side changed",
    };
}

/// <summary>One difference between the local build and the database.</summary>
/// <param name="Name">What differs: <c>ArticlePage</c>, <c>ArticlePage.Heading</c>, a migration id, a store name, <c>CMS</c>.</param>
/// <param name="Ahead"><see cref="DriftAhead.Local"/>, <see cref="DriftAhead.Database"/> or <see cref="DriftAhead.Unknown"/>.</param>
/// <param name="Difference">How, e.g. "only in the code", "type: XhtmlString in the code, String in the database".</param>
public sealed record DriftItem(string Name, string Ahead, string Difference);

/// <summary>
/// What the CLI found before the site started (<c>serve</c>), handed to the site agent in the file
/// <see cref="AgentProtocol.DriftFileVariable"/> names, so the agent's report and fingerprint cover it too.
/// </summary>
public sealed record StartupDrift
{
    public IReadOnlyList<DriftItem> Migrations { get; init; } = [];

    public IReadOnlyList<DriftItem> Schema { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Response of <see cref="AgentRoutes.Drift"/>: what differs between the local build and a shared database, which the
/// site's content type sync doesn't commit to (shared mode). Writes stop while there is any, until the caller confirms
/// with <see cref="Fingerprint"/>.
/// </summary>
public sealed record DriftReport
{
    /// <summary>False against a local database: the content type sync commits there, so there is nothing to compare.</summary>
    public bool Checked { get; init; }

    /// <summary>Only some of it was compared (<c>doctor</c> before <c>serve</c>): no fingerprint, nothing can be confirmed with it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Partial { get; init; }

    /// <summary>A hash of every difference; null when nothing differs. It changes when the differences do.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>One of <see cref="DriftAhead"/>; null when nothing differs.</summary>
    public string? Ahead { get; init; }

    public int Differences => ContentTypes.Count + Properties.Count + Migrations.Count + Stores.Count + Schema.Count;

    public IReadOnlyList<DriftItem> ContentTypes { get; init; } = [];

    public IReadOnlyList<DriftItem> Properties { get; init; } = [];

    /// <summary>EF Core migrations: in the build but not in <c>__EFMigrationsHistory</c>, or the reverse.</summary>
    public IReadOnlyList<DriftItem> Migrations { get; init; } = [];

    /// <summary>Dynamic Data Store types whose mapping differs from the stored one.</summary>
    public IReadOnlyList<DriftItem> Stores { get; init; } = [];

    /// <summary>The CMS database schema version, when it differs from what the local packages need.</summary>
    public IReadOnlyList<DriftItem> Schema { get; init; } = [];

    /// <summary>What wasn't compared, and why.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>A report with <see cref="Fingerprint"/> and <see cref="Ahead"/> worked out from the items.</summary>
    public static DriftReport Create(
        IEnumerable<DriftItem> contentTypes,
        IEnumerable<DriftItem> properties,
        IEnumerable<DriftItem> migrations,
        IEnumerable<DriftItem> stores,
        IEnumerable<DriftItem> schema,
        IEnumerable<string> notes)
    {
        var report = new DriftReport
        {
            Checked = true,
            ContentTypes = Sorted(contentTypes),
            Properties = Sorted(properties),
            Migrations = Sorted(migrations),
            Stores = Sorted(stores),
            Schema = Sorted(schema),
            Notes = notes.Distinct(StringComparer.Ordinal).ToList(),
        };
        return report.Differences == 0
            ? report
            : report with { Fingerprint = FingerprintOf(report.Sections()), Ahead = AheadOf(report.Sections().Select(s => s.Item)) };
    }

    /// <summary>Every item with the name of its list, in a fixed order.</summary>
    public IEnumerable<(string Section, DriftItem Item)> Sections() =>
        ContentTypes.Select(i => ("contentTypes", i))
            .Concat(Properties.Select(i => ("properties", i)))
            .Concat(Migrations.Select(i => ("migrations", i)))
            .Concat(Stores.Select(i => ("stores", i)))
            .Concat(Schema.Select(i => ("schema", i)));

    /// <summary>12 hex characters of a SHA-256 over every difference, in an order that doesn't depend on the input's.</summary>
    public static string FingerprintOf(IEnumerable<(string Section, DriftItem Item)> items)
    {
        var lines = items
            .Select(i => string.Join('\u001f', i.Section, i.Item.Name, i.Item.Ahead, i.Item.Difference))
            .Order(StringComparer.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary><see cref="DriftAhead.Local"/> or <see cref="DriftAhead.Database"/> when every item that says agrees, else <see cref="DriftAhead.Both"/>.</summary>
    public static string? AheadOf(IEnumerable<DriftItem> items)
    {
        var known = items.Select(i => i.Ahead).Where(a => a is DriftAhead.Local or DriftAhead.Database).Distinct().ToList();
        return known.Count switch
        {
            0 => items.Any() ? DriftAhead.Unknown : null,
            1 => known[0],
            _ => DriftAhead.Both,
        };
    }

    /// <summary>
    /// "3 differences (local is ahead: ...): content type NewsPage (only in the code); ...", the first
    /// <paramref name="max"/> items and how many more.
    /// </summary>
    public string Describe(int max = 5)
    {
        if (Differences == 0)
        {
            return "nothing differs";
        }
        var items = Sections().ToList();
        var shown = items.Take(max).Select(i => $"{Label(i.Section)} {i.Item.Name} ({i.Item.Difference})");
        var more = items.Count > max ? $"; and {items.Count - max} more" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{Differences} difference{(Differences == 1 ? "" : "s")} ({DriftAhead.Describe(Ahead)}): {string.Join("; ", shown)}{more}");
    }

    private static string Label(string section) => section switch
    {
        "contentTypes" => "content type",
        "properties" => "property",
        "migrations" => "EF Core migration",
        "stores" => "DDS store",
        _ => "schema",
    };

    private static List<DriftItem> Sorted(IEnumerable<DriftItem> items) =>
        items.Distinct().OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Difference, StringComparer.Ordinal).ToList();
}
