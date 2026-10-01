using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Content;
using OptiCli.Core.Properties;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// The site agent's pending-draft rule, read from the database for the publish dry run, which opticli checks without
/// the site: changes saved after the published version by someone other than <see cref="AgentProtocol.PrincipalName"/>.
/// </summary>
/// <remarks>Values are compared as <c>get</c> shows them, not in the request shape the agent reports.</remarks>
internal static class PendingDraftReader
{
    /// <param name="version">The version a publish would put live.</param>
    /// <returns>Null when nobody else saved a version since the published one.</returns>
    public static async Task<PendingDraft?> FindAsync(ContentSession session, VersionInfo version, CancellationToken cancellationToken)
    {
        var newest = await VersionReader.NewestSavedByOtherAsync(
            session.Db, session.Model, version.ContentId, version.LanguageId, version.Id, AgentProtocol.PrincipalName, cancellationToken);
        if (newest is null)
        {
            return null;
        }
        var published = await VersionReader.PublishedAsync(session.Db, session.Model, version.ContentId, version.LanguageId, cancellationToken);
        var loader = new ContentLoader(session.Db, session.Identities);
        var options = new DecodeOptions(Full: true);
        var after = await loader.GetAsync(version.ContentId, new VersionSelector(VersionKind.Specific, version.Id), null, options, cancellationToken);
        var before = published is null
            ? null
            : await loader.GetAsync(version.ContentId, new VersionSelector(VersionKind.Specific, published.Id), null, options, cancellationToken);
        return new PendingDraft(newest.Ref, newest.ChangedBy, DateTime.SpecifyKind(newest.Saved ?? default, DateTimeKind.Utc), Diff(before, after));
    }

    /// <summary>The name, publish dates and every property value that differ.</summary>
    internal static List<PropertyChange> Diff(ContentDocument? before, ContentDocument after)
    {
        var changes = new List<PropertyChange>();
        void Compare(string name, JsonNode? from, JsonNode? to)
        {
            if (!JsonNode.DeepEquals(from, to))
            {
                changes.Add(new PropertyChange(name, Element(from), Element(to)));
            }
        }

        Compare("Name", Node(before?.Name), Node(after.Name));
        Compare("StartPublish", Node(before?.StartPublish), Node(after.StartPublish));
        Compare("StopPublish", Node(before?.StopPublish), Node(after.StopPublish));
        foreach (var (name, property) in after.Properties)
        {
            Compare(name, before?.Properties[name]?["value"], property?["value"]);
        }
        foreach (var (name, property) in before?.Properties ?? new JsonObject())
        {
            if (!after.Properties.ContainsKey(name))
            {
                Compare(name, property?["value"], null);
            }
        }
        return changes;
    }

    private static JsonNode? Node<T>(T? value) => value is null ? null : JsonSerializer.SerializeToNode(value, AgentJson.Options);

    private static JsonElement? Element(JsonNode? node) => node is null ? null : JsonSerializer.SerializeToElement(node);
}
