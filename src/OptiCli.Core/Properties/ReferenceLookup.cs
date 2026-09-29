using System.Text.Json.Nodes;
using OptiCli.Core.Content;

namespace OptiCli.Core.Properties;

/// <summary>What the decoder needs to know about content that a value points at.</summary>
public interface IReferenceLookup
{
    ContentIdentity Content(int id);

    ContentIdentity Content(Guid guid);

    /// <summary>The target's own properties when <c>--expand</c> is on; null otherwise.</summary>
    JsonObject? Expanded(Guid guid);
}

/// <summary>
/// First decoding pass: records every id and GUID the values reference, so they can be resolved in one
/// batch before the real pass.
/// </summary>
public sealed class ReferenceCollector : IReferenceLookup
{
    public HashSet<int> Ids { get; } = [];

    public HashSet<Guid> Guids { get; } = [];

    public ContentIdentity Content(int id)
    {
        Ids.Add(id);
        return ContentIdentity.MissingId(id);
    }

    public ContentIdentity Content(Guid guid)
    {
        Guids.Add(guid);
        return ContentIdentity.MissingGuid(guid);
    }

    public JsonObject? Expanded(Guid guid) => null;
}

/// <summary>Second pass: identities from a loaded <see cref="IdentityResolver"/>, in the item's language.</summary>
public sealed class ResolvedReferences(IdentityResolver identities, LanguageBranch? language, IReadOnlyDictionary<Guid, JsonObject>? expanded = null)
    : IReferenceLookup
{
    public ContentIdentity Content(int id) => identities.ById(id, language);

    public ContentIdentity Content(Guid guid) => identities.ByGuid(guid, language);

    public JsonObject? Expanded(Guid guid) => expanded?.GetValueOrDefault(guid);
}
