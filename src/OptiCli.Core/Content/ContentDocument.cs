using System.Text.Json.Nodes;

namespace OptiCli.Core.Content;

/// <summary>The result of <c>get</c>: identity, version facts and decoded properties.</summary>
/// <param name="Version">The version shown, as a ref (<c>123_456</c>).</param>
/// <param name="Languages">All language branches the item has.</param>
/// <param name="LatestDraft">A newer unpublished version of this branch, when one exists.</param>
/// <param name="RequestedLanguage">Set when the item has no branch in the requested language and another is shown.</param>
public sealed record ContentDocument(
    string Ref,
    Guid Guid,
    string Type,
    string? Name,
    string? Language,
    string Status,
    string? Url,
    string Kind,
    string? Version,
    string? MasterLanguage,
    IReadOnlyList<string> Languages,
    string? Parent,
    DateTime? Saved,
    string? ChangedBy,
    DateTime? StartPublish,
    DateTime? StopPublish,
    string? LatestDraft,
    bool? Deleted,
    string? RequestedLanguage,
    IReadOnlyList<string>? Notes,
    JsonObject Properties);
