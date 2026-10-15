using System.Text.Json.Nodes;
using OptiCli.Core.Cms;

namespace OptiCli.Core.Export;

/// <summary>One content item an export writes, as the source database has it.</summary>
/// <param name="Id">The source's content id (only for messages: ids differ between databases).</param>
/// <param name="Type">The content type's name, as the plan names it.</param>
/// <param name="Parent">The parent's GUID; for content in a "For this page" folder, the folder's (see <paramref name="Owner"/>).</param>
/// <param name="Owner">
/// For a block or media item in a "For this page" folder: the content that folder belongs to. The plan places it with
/// <c>for</c>: a create makes the folder, with a GUID of its own in every database.
/// </param>
/// <param name="MasterLanguage">The master branch's language; null for content without languages (media, folders).</param>
/// <param name="Branches">The branches to write, master first.</param>
/// <param name="CultureSpecific">The properties whose values are per branch (written by <c>translate</c>).</param>
/// <param name="Order">The item's place in the source's tree (parents before children), which the plan keeps where it can.</param>
public sealed record ExportItem(
    int Id,
    Guid Guid,
    ContentKind Kind,
    string Type,
    Guid? Parent,
    Guid? Owner,
    string? MasterLanguage,
    IReadOnlyList<ExportBranch> Branches,
    IReadOnlySet<string> CultureSpecific,
    int Order)
{
    /// <summary>Media: the file, relative to the export folder (<c>files/…</c>); null when its bytes weren't found.</summary>
    public string? File { get; init; }
}

/// <summary>One language branch of an <see cref="ExportItem"/>: the version written, as <c>get</c> shows it.</summary>
/// <param name="Language">The branch's language; null for content without languages.</param>
/// <param name="VersionId">The source's version written: the published one, or the newest draft of a branch never published.</param>
/// <param name="Published">The branch is published in the source, so the plan publishes it too.</param>
/// <param name="Document"><c>get</c>'s output for that version, with <c>--full --all-properties</c>.</param>
public sealed record ExportBranch(string? Language, int VersionId, bool Published, JsonObject Document);
