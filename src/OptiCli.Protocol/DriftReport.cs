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

    /// <summary>
    /// The database has it and the local build doesn't: the environment runs newer code than the checkout, or (content
    /// types and properties) their classes were removed from code and the CMS kept them for their content.
    /// </summary>
    public const string Database = "database";

    /// <summary>Some differences one way, some the other.</summary>
    public const string Both = "both";

    /// <summary>They differ, and the database doesn't say which side changed (a property's settings, a store's mapping).</summary>
    public const string Unknown = "unknown";

    /// <summary>What it means, for messages: "local is ahead (...)".</summary>
    /// <param name="contentModel">
    /// A content type or property is on the database's side, which may be a class removed from code; false when only EF
    /// Core migrations or the CMS schema version are, which only newer code makes.
    /// </param>
    public static string Describe(string? ahead, bool contentModel = true) => ahead switch
    {
        Local => "local is ahead: this build has changes the database's environment doesn't",
        Database when contentModel => "the database is ahead: it has what this build lacks, from newer code in its environment or from classes or properties removed from code that the CMS kept for their content",
        Database => "the database is ahead: its environment runs newer code than this checkout",
        Both when contentModel => "both are ahead: this build has changes the database's environment doesn't, and the database has what this build lacks",
        Both => "both are ahead: this build and the database's environment each have changes the other lacks",
        _ => "they differ, and the database doesn't say which side changed",
    };
}

/// <summary>One difference between the local build and the database.</summary>
/// <param name="Name">What differs: <c>ArticlePage</c>, <c>ArticlePage.Heading</c>, a migration id, a store name, <c>CMS</c>.</param>
/// <param name="Ahead"><see cref="DriftAhead.Local"/>, <see cref="DriftAhead.Database"/> or <see cref="DriftAhead.Unknown"/>.</param>
/// <param name="Difference">How, e.g. "only in the code", "type: XhtmlString in the code, String in the database".</param>
public sealed record DriftItem(string Name, string Ahead, string Difference)
{
    /// <summary>
    /// The <see cref="Difference"/> of a content type or property only the database has (the agent's), which the CLI
    /// looks up in the git history.
    /// </summary>
    public const string OnlyInDatabase = "only in the database";

    /// <summary>
    /// Listed, but not a difference that stops writes: it counts toward neither <see cref="DriftReport.Differences"/> nor the
    /// fingerprint (CMS 13 content types of unknown origin, which admin mode makes too).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Informational { get; init; }

    /// <summary>
    /// For a content type or property only in the database: the commit in the checkout's git history that removed its
    /// class (or the property) from code. The CLI adds it, not the agent, so the fingerprint doesn't cover it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CodeRemoval? RemovedFromCode { get; init; }
}

/// <summary>A commit that removed something from code.</summary>
/// <param name="Commit">The abbreviated commit hash.</param>
/// <param name="Date">Its author date, <c>yyyy-MM-dd</c>.</param>
/// <param name="Subject">The first line of its message.</param>
public sealed record CodeRemoval(string Commit, string Date, string Subject);

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

    /// <summary>The differences that stop writes (every item but the <see cref="DriftItem.Informational"/> ones).</summary>
    public int Differences => Sections().Count(s => !s.Item.Informational);

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
        var blocking = report.Sections().Where(s => !s.Item.Informational).ToList();
        return blocking.Count == 0
            ? report
            : report with { Fingerprint = FingerprintOf(blocking), Ahead = AheadOf(blocking.Select(s => s.Item)) };
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
        var items = Sections().Where(s => !s.Item.Informational).ToList();
        var shown = items.Take(max).Select(i => $"{Label(i.Section)} {i.Item.Name} ({Shown(i.Section, i.Item)})");
        var more = items.Count > max ? $"; and {items.Count - max} more" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{Differences} difference{(Differences == 1 ? "" : "s")} ({DescribeAhead()}): {string.Join("; ", shown)}{more}");
    }

    /// <summary>Whether a content type or property that stops writes is on the database's side (not only migrations or the schema version).</summary>
    [JsonIgnore]
    public bool ContentModelInDatabase =>
        ContentTypes.Concat(Properties).Any(i => !i.Informational && i.Ahead == DriftAhead.Database);

    /// <summary><see cref="DriftAhead.Describe"/> for this report's direction and items.</summary>
    public string DescribeAhead() => DriftAhead.Describe(Ahead, ContentModelInDatabase);

    /// <summary>
    /// The item's difference, with the commit that removed it from code when the CLI found one: "only in the database;
    /// its class was removed from code in abc1234 (2026-09-14)".
    /// </summary>
    public static string Shown(string section, DriftItem item) => item.RemovedFromCode is { } removed
        ? $"{item.Difference}; {(section == "properties" ? "removed" : "its class was removed")} from code in {removed.Commit} ({removed.Date})"
        : item.Difference;

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
