using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;
using OptiCli.Core.Properties;
using OptiCli.Core.Urls;
using OptiCli.Integration.Sampling;
using OptiCli.Protocol;

namespace OptiCli.Integration.Comparison;

/// <param name="DbTime">Time the DB path (<c>get --full --all-properties</c>) took for this item.</param>
internal sealed record ItemResult(SampleItem Item, string Label, IReadOnlyList<Mismatch> Mismatches, TimeSpan DbTime);

/// <summary>Loads one item both ways, the CLI's DB path and the CMS through the agent, and lists every difference.</summary>
internal sealed class ItemComparer(SiteUnderTest site)
{
    private static readonly DecodeOptions Everything = new(Full: true, AllProperties: true, Composition: true);

    private readonly ContentLoader _loader = new(site.Session.Db, site.Session.Identities);

    public async Task<ItemResult> CompareAsync(SampleItem item, CancellationToken cancellationToken)
    {
        var language = site.Session.Model.Language(item.LanguageId);
        var code = language?.DisplayCode;
        var reference = ContentIdentity.RefFor(item.ContentId, item.VersionId);
        var label = $"{reference} [{code ?? "invariant"}]";
        var selector = item.VersionId is { } version ? new VersionSelector(VersionKind.Specific, version) : VersionSelector.Published;
        var mismatches = new List<Mismatch>();

        ContentDocument? db = null;
        var watch = Stopwatch.StartNew();
        try
        {
            db = await _loader.GetAsync(item.ContentId, selector, language, Everything, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mismatches.Add(Error(label, item, "db", ex));
        }
        var dbTime = watch.Elapsed;

        ContentItem? agent = null;
        try
        {
            var route = AgentRoutes.Read(item.ContentId.ToString(CultureInfo.InvariantCulture), code, item.VersionId?.ToString(CultureInfo.InvariantCulture));
            agent = await site.Agent.SendAsync<ContentItem>(HttpMethod.Get, route, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mismatches.Add(Error(label, item, "agent", ex));
        }

        if (db is not null && agent is not null)
        {
            var context = new Context(label, db.Type, db.Kind);
            CompareIdentity(context, db, agent, language, mismatches);
            CompareProperties(context, Canonical.FromDb(db.Properties), Canonical.FromAgent(agent.Properties), mismatches);
            CompareComposition(context, Canonical.CompositionFromDb(db.Composition), Canonical.CompositionFromAgent(agent.Composition), mismatches);
            var facts = Facts(item, db, agent);
            return new ItemResult(item, label, mismatches.Select(m => m with { Facts = facts }).ToList(), dbTime);
        }
        return new ItemResult(item, label, mismatches, dbTime);
    }

    private sealed record Context(string Label, string Type, string Kind);

    private ItemFacts Facts(SampleItem item, ContentDocument db, ContentItem agent)
    {
        var header = site.Session.Identities.Header(item.ContentId);
        var sites = site.Session.Model.Sites;
        var roots = sites.All.SelectMany(s => new[] { SiteMap.StartPageId(s), SiteMap.AssetsRootId(s) })
            .Append(sites.GlobalAssetsRoot)
            .Append(sites.ContentAssetsRoot)
            .OfType<int>()
            .ToHashSet();
        var path = header?.AncestorIds.Append(item.ContentId) ?? [];
        return new ItemFacts(
            CmsLocalizable: agent.Languages is not null,
            CmsVersionable: agent.Status is not null,
            CmsChangeTracked: agent.Saved is not null,
            DbMasterBranch: db.Language is null || db.Language == db.MasterLanguage,
            UnderSiteOrAssets: path.Any(roots.Contains),
            ExternalShortcutUrl: db.Shortcut is { Type: "external", Url: { } external } ? external : null,
            FetchData: db.Shortcut is { Type: "fetchData" });
    }

    private void CompareIdentity(Context context, ContentDocument db, ContentItem agent, LanguageBranch? language, List<Mismatch> mismatches)
    {
        void Check(string field, object? dbValue, object? agentValue)
        {
            var left = Canonical.Text(Canonical.Prune(JsonOutput.ToNode(dbValue)));
            var right = Canonical.Text(Canonical.Prune(JsonOutput.ToNode(agentValue)));
            if (left != right)
            {
                mismatches.Add(new Mismatch(context.Label, context.Type, context.Kind, MismatchKind.Identity, field, null, "", left, right));
            }
        }

        Check("guid", db.Guid, agent.Guid);
        Check("name", db.Name, agent.Name);
        Check("type", db.Type, agent.Type);
        Check("kind", db.Kind, agent.Kind);
        Check("language", db.Language, agent.Language);
        Check("masterLanguage", db.MasterLanguage, agent.MasterLanguage);
        Check("languages", db.Languages, agent.Languages);
        Check("status", db.Status, agent.Status);
        Check("parent", db.Parent, agent.Parent);
        Check("version", db.Version?.Split('_').ElementAtOrDefault(1), agent.Version?.ToString(CultureInfo.InvariantCulture));
        Check("deleted", db.Deleted ?? false, agent.Deleted ?? false);
        Check("changedBy", db.ChangedBy, agent.ChangedBy);
        Check("startPublish", DbDate(db.StartPublish), AgentDate(agent.StartPublish));
        Check("stopPublish", DbDate(db.StopPublish), AgentDate(agent.StopPublish));
        Check("saved", DbDate(db.Saved), AgentDate(agent.Saved));
        var (dbUrl, agentUrl) = ComparableUrls(db, language, agent.Url);
        Check("url", dbUrl, agentUrl);
    }

    /// <summary>As the CLI prints it.</summary>
    private static string? DbDate(DateTime? value) =>
        value is null ? null : Canonical.Date(JsonOutput.ToNode(value))?.GetValue<string>();

    private static string? AgentDate(DateTime? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The CMS answers host-relative URLs for the site the request came in on and absolute ones for the rest (the
    /// agent is reached on 127.0.0.1, which usually belongs to no site). Absolute URLs are compared with opticli's
    /// absolute ones; for shared assets, which belong to no one site, only the paths are compared.
    /// </summary>
    private (string? Db, string? Agent) ComparableUrls(ContentDocument db, LanguageBranch? language, string? agentUrl)
    {
        if (agentUrl is null || !Uri.TryCreate(agentUrl, UriKind.Absolute, out var absolute))
        {
            return (db.Url, agentUrl);
        }
        var header = site.Session.Identities.Header(int.Parse(db.Ref, CultureInfo.InvariantCulture));
        var url = header is null ? null : site.Session.Identities.Urls.UrlOf(header, db.Language is null ? null : language);
        return url?.Absolute is { } dbAbsolute ? (dbAbsolute, agentUrl) : (db.Url, Uri.UnescapeDataString(absolute.AbsolutePath));
    }

    private static void CompareProperties(Context context, JsonObject db, JsonObject agent, List<Mismatch> mismatches)
    {
        foreach (var name in db.Select(p => p.Key).Union(agent.Select(p => p.Key)).Order(StringComparer.Ordinal))
        {
            var left = db[name] as JsonObject;
            var right = agent[name] as JsonObject;
            var leftType = left?["type"]?.GetValue<string>();
            var rightType = right?["type"]?.GetValue<string>();

            Mismatch Make(MismatchKind kind, string? type, string path) => new(
                context.Label, context.Type, context.Kind, kind, name, type, path,
                Canonical.Text(left?["value"]), Canonical.Text(right?["value"]));

            if (left is null)
            {
                mismatches.Add(Make(MismatchKind.AgentOnly, rightType, ""));
            }
            else if (right is null)
            {
                mismatches.Add(Make(MismatchKind.DbOnly, leftType, ""));
            }
            else if (leftType != rightType)
            {
                mismatches.Add(Make(MismatchKind.Type, $"{leftType}->{rightType}", ""));
            }
            else if (FirstDifference(left["value"], right["value"], "") is { } path)
            {
                mismatches.Add(Make(MismatchKind.Value, leftType, path));
            }
        }
    }

    /// <summary>CMS 13: the Visual Builder composition, node by node (<c>get</c>'s <c>composition</c> against the CMS's mapper).</summary>
    private static void CompareComposition(Context context, JsonNode? db, JsonNode? agent, List<Mismatch> mismatches)
    {
        if (db is null && agent is null)
        {
            return;
        }
        Mismatch Make(MismatchKind kind, string path) => new(context.Label, context.Type, context.Kind, kind, "composition", "Composition", path, Canonical.Text(db), Canonical.Text(agent));
        if (db is null)
        {
            mismatches.Add(Make(MismatchKind.AgentOnly, ""));
        }
        else if (agent is null)
        {
            mismatches.Add(Make(MismatchKind.DbOnly, ""));
        }
        else if (FirstDifference(db, agent, "") is { } path)
        {
            mismatches.Add(Make(MismatchKind.Value, path));
        }
    }

    /// <summary>Path of the first place two canonical values differ; null when equal.</summary>
    internal static string? FirstDifference(JsonNode? left, JsonNode? right, string path)
    {
        switch (left, right)
        {
            case (JsonObject a, JsonObject b):
                foreach (var key in a.Select(p => p.Key).Union(b.Select(p => p.Key)).Order(StringComparer.Ordinal))
                {
                    if (FirstDifference(a[key], b[key], $"{path}.{key}") is { } found)
                    {
                        return found;
                    }
                }
                return null;
            case (JsonArray a, JsonArray b):
                for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
                {
                    if (i >= a.Count || i >= b.Count)
                    {
                        return $"{path}[{i}]";
                    }
                    if (FirstDifference(a[i], b[i], $"{path}[{i}]") is { } found)
                    {
                        return found;
                    }
                }
                return a.Count == b.Count ? null : $"{path}[{Math.Min(a.Count, b.Count)}]";
            default:
                return Canonical.Text(left) == Canonical.Text(right) ? null : path;
        }
    }

    /// <summary>A failure on one side; an exception that isn't an opticli error is a crash, reported by its type.</summary>
    private static Mismatch Error(string label, SampleItem item, string side, Exception ex) =>
        new(label, $"#{item.TypeId}", item.Kind.ToString().ToLowerInvariant(), MismatchKind.Error,
            $"{side}:{(ex is OptiCliException known ? known.Code.ToString() : ex.GetType().Name)}", null, "",
            side == "db" ? ex.Message : "", side == "agent" ? ex.Message : "");
}
