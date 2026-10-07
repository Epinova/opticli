using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Content;
using OptiCli.Core.Properties;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// The site agent's pending-draft rule, read from the database for the publish dry run, which opticli checks without
/// the site: changes saved after the published version by someone other than <see cref="AgentProtocol.PrincipalName"/>
/// that the version carries (the agent's <c>PendingDrafts.Carries</c>).
/// </summary>
/// <remarks>Values are compared as <c>get</c> shows them, not in the request shape the agent reports.</remarks>
internal static class PendingDraftReader
{
    /// <param name="version">The version a publish would put live.</param>
    /// <returns>Null when nobody else saved a version since the published one whose changes it has.</returns>
    public static async Task<PendingDraft?> FindAsync(ContentSession session, VersionInfo version, CancellationToken cancellationToken)
    {
        var candidates = await VersionReader.SavedByOthersAsync(
            session.Db, session.Model, version.ContentId, version.LanguageId, version.Id, AgentProtocol.PrincipalName, cancellationToken);
        if (candidates.Count == 0)
        {
            return null;
        }
        var published = await VersionReader.PublishedAsync(session.Db, session.Model, version.ContentId, version.LanguageId, cancellationToken);
        var loader = new ContentLoader(session.Db, session.Identities);
        // CMS 13: a composition is compared as one value, as the agent compares it.
        var options = new DecodeOptions(Full: true, Composition: true);
        Task<ContentDocument> Load(int id) => loader.GetAsync(version.ContentId, new VersionSelector(VersionKind.Specific, id), null, options, cancellationToken);
        var after = await Load(version.Id);
        var before = published is null ? null : await Load(published.Id);
        foreach (var candidate in candidates)
        {
            var draft = candidate.Id == version.Id ? after : await Load(candidate.Id);
            if (Carries(Diff(before, draft), Diff(draft, after)))
            {
                return new PendingDraft(candidate.Ref, candidate.ChangedBy, DateTime.SpecifyKind(candidate.Saved ?? default, DateTimeKind.Utc), Diff(before, after));
            }
        }
        return null;
    }

    /// <summary>The base has the draft's value for at least one property the draft changed (as the agent decides it).</summary>
    internal static bool Carries(IReadOnlyList<PropertyChange> draftChanges, IReadOnlyList<PropertyChange> baseDifferences) =>
        draftChanges.Any(c => !baseDifferences.Any(d => d.Property.Equals(c.Property, StringComparison.OrdinalIgnoreCase)));

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
        if (after.Composition is not null || before?.Composition is not null)
        {
            Compare(CompositionInput.Field, before?.Composition, after.Composition);
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
