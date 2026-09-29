using System.Text.Json;
using System.Text.Json.Nodes;

namespace OptiCli.Integration.Comparison;

/// <param name="Why">Why the DB cannot (or should not) reproduce what the CMS shows.</param>
internal sealed record KnownDifference(string Name, Func<Mismatch, bool> Matches, string Why);

/// <summary>
/// Differences between the DB read path and the CMS that are irreducible: the CMS decides them in code the
/// DB never sees. Everything else is a bug in the decoder or in the agent's rendering, and fails the run.
/// Keep every entry as narrow as the evidence allows.
/// </summary>
internal static class KnownDifferences
{
    public static readonly IReadOnlyList<KnownDifference> All =
    [
        new(
            "no-version-metadata",
            m => m is { Difference: MismatchKind.Identity, Agent: "null" }
                && (m is { Field: "status" or "startPublish" or "stopPublish", Facts.CmsVersionable: false }
                    || m is { Field: "saved" or "changedBy", Facts.CmsChangeTracked: false }),
            "Folders and other content whose model class isn't IVersionable (IChangeTrackable) have no status or publish " +
            "dates (saved date, editor) in the CMS; the DB still keeps them on the branch row, and opticli shows what is stored."),

        new(
            "not-localizable",
            m => !m.Facts.CmsLocalizable
                && (m is { Difference: MismatchKind.Identity, Field: "language" or "languages" or "masterLanguage" } || !m.Facts.DbMasterBranch),
            "Content whose model class isn't ILocalizable (settings base classes, for instance) can still have several " +
            "language branches in the DB (from when it was localizable, or created by an add-on). The CMS then reports no " +
            "languages and loads the master branch whatever is asked; the DB shows the requested branch as stored."),

        new(
            "list-item-defaults",
            m => m.Difference == MismatchKind.Value && OnlyAddsDefaults(JsonNode.Parse(m.Db), JsonNode.Parse(m.Agent)),
            "Custom list properties store their items as JSON written when they were saved. The CMS deserialises them into " +
            "the site's classes, so members added to a class since then come back at their default value; the DB shows " +
            "the JSON as stored."),

        new(
            "dangling-references",
            m => m is { Difference: MismatchKind.Value, PropertyType: "ContentReferenceList" } && OnlyDanglingDiffer(m.Db, m.Agent),
            "References to content that no longer exists: the DB still has the GUIDs (opticli marks them missing), the CMS " +
            "turns them into empty references when it loads the list."),

        new(
            "system-folder-names",
            m => m is { Difference: MismatchKind.Identity, Field: "name", Kind: "folder" } && m.Db.StartsWith("\"Sys", StringComparison.Ordinal),
            "The asset root folders (SysGlobalAssets, SysSiteAssets, SysContentAssets) are stored under their system names; " +
            "the CMS shows them under display names computed in code (\"For all sites\", in the editor's language)."),

        new(
            "url-outside-sites",
            m => m is { Difference: MismatchKind.Identity, Field: "url", Db: "null", Facts.UnderSiteOrAssets: false },
            "Content outside every start page and asset root (the root, the recycle bin, containers directly under the root, " +
            "add-on content roots such as search or sitemap files) has no URL opticli can derive: the CMS composes one against " +
            "whichever site the request came in on, or add-on routing code serves it."),

        new(
            "url-segments-left-out",
            m => m is { Difference: MismatchKind.Identity, Field: "url" } && LeavesOutSegments(m.Db, m.Agent),
            "Sites can leave ancestors out of URLs in code (custom URL segment generation or partial routing, e.g. grouping " +
            "pages that never show up in the path). The DB only has each item's own segment, so opticli lists them all."),
    ];

    public static KnownDifference? Find(Mismatch mismatch) => All.FirstOrDefault(k => k.Matches(mismatch));

    /// <summary>
    /// The CMS value is the stored one plus object members the stored JSON doesn't have, all at their default
    /// (<c>false</c>, <c>0</c>, empty): the CMS deserialised the items into the site's classes and wrote them back out.
    /// </summary>
    private static bool OnlyAddsDefaults(JsonNode? stored, JsonNode? cms) => (stored, cms) switch
    {
        (JsonObject a, JsonObject b) => a.All(p => b.ContainsKey(p.Key) && OnlyAddsDefaults(p.Value, b[p.Key]))
            && b.Where(p => !a.ContainsKey(p.Key)).All(p => IsDefault(p.Value)),
        (JsonArray a, JsonArray b) => a.Count == b.Count && a.Zip(b).All(pair => OnlyAddsDefaults(pair.First, pair.Second)),
        _ => Canonical.Text(stored) == Canonical.Text(cms),
    };

    /// <summary>Same list, except that some references the DB still names (<c>guid:...</c>, missing) are null in the CMS.</summary>
    private static bool OnlyDanglingDiffer(string db, string cms) =>
        JsonNode.Parse(db) is JsonArray a && JsonNode.Parse(cms) is JsonArray b && a.Count == b.Count
        && a.Zip(b).All(p => Canonical.Text(p.First) == Canonical.Text(p.Second)
            || (p.Second is null && p.First is JsonValue v && v.TryGetValue<string>(out var reference) && reference.StartsWith("guid:", StringComparison.Ordinal)));

    private static bool IsDefault(JsonNode? value) => value switch
    {
        null => true,
        JsonValue v when v.TryGetValue<bool>(out var flag) => !flag,
        JsonValue v when v.TryGetValue<double>(out var number) => number == 0,
        JsonValue v when v.TryGetValue<string>(out var text) => text.Length == 0,
        JsonArray { Count: 0 } or JsonObject { Count: 0 } => true,
        _ => false,
    };

    /// <summary>The CMS URL is the DB one with one or more whole middle segments removed.</summary>
    private static bool LeavesOutSegments(string db, string cms)
    {
        if (JsonSerializer.Deserialize<string?>(db) is not { } dbUrl || JsonSerializer.Deserialize<string?>(cms) is not { } cmsUrl)
        {
            return false;
        }
        var full = dbUrl.Split('/');
        var shortened = cmsUrl.Split('/');
        if (shortened.Length >= full.Length || shortened[^1] != full[^1] || shortened[^2] != full[^2])
        {
            return false;
        }
        var at = 0;
        foreach (var segment in full)
        {
            if (at < shortened.Length && segment == shortened[at])
            {
                at++;
            }
        }
        return at == shortened.Length;
    }
}
