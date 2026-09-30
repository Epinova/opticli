using System.Text.Json;

namespace OptiCli.Protocol;

/// <summary>Identity and state of one content version.</summary>
public sealed record ContentSummary
{
    /// <summary>Version-specific ref (<c>id_version</c>) that <c>get</c> accepts.</summary>
    public required string Ref { get; init; }

    public required int Id { get; init; }

    /// <summary>Version id (<c>tblWorkContent.pkID</c>).</summary>
    public int? Version { get; init; }

    public required Guid Guid { get; init; }

    public required string Name { get; init; }

    public string? Type { get; init; }

    public string? Language { get; init; }

    /// <summary>CMS version status, camelCase: <c>checkedOut</c> (draft), <c>published</c>, <c>previouslyPublished</c>, ...</summary>
    public string? Status { get; init; }

    public string? Parent { get; init; }
}

/// <summary>Response of create, draft, languages and publish.</summary>
public sealed record WriteResult
{
    /// <summary>The saved version; for a dry run the version the change was applied to (null for a dry-run create).</summary>
    public ContentSummary? Content { get; init; }

    /// <summary>True when a version was written.</summary>
    public bool Saved { get; init; }

    public bool Published { get; init; }

    public bool DryRun { get; init; }

    /// <summary>
    /// False only for a dry run that would fail validation (see <see cref="Validation"/>). A real
    /// write that fails validation returns a <c>validation</c> error (422) instead.
    /// </summary>
    public bool Valid { get; init; } = true;

    /// <summary>Version the change was based on (draft) or the version that was published (publish).</summary>
    public int? BaseVersion { get; init; }

    /// <summary>Every property whose value differs from the base version (or from the type's defaults on create).</summary>
    public IReadOnlyList<PropertyChange> Changes { get; init; } = [];

    /// <summary>Warnings, or for an invalid dry run the errors that would block the save.</summary>
    public IReadOnlyList<ValidationIssue>? Validation { get; init; }

    /// <summary>A create or upload with <see cref="CreateRequest.UpdateExisting"/> found the content and updated it.</summary>
    public bool Existing { get; init; }

    /// <summary>That existing content was in the recycle bin and was moved back under the parent (not for a dry run).</summary>
    public bool Restored { get; init; }

    /// <summary>For an upload: the media type the file is created as (also for a dry run, which has no <see cref="Content"/>).</summary>
    public string? MediaType { get; init; }
}

/// <summary>A property value before and after, in the same JSON shape the draft endpoint accepts.</summary>
/// <param name="Before">Omitted when the property was empty.</param>
/// <param name="After">Omitted when the property is now empty.</param>
public sealed record PropertyChange(string Property, JsonElement? Before, JsonElement? After);

/// <summary>Response of move and delete (delete is a move to the recycle bin).</summary>
public sealed record MoveResult
{
    public required ContentSummary Content { get; init; }

    public required string PreviousParent { get; init; }

    public required string Parent { get; init; }
}
