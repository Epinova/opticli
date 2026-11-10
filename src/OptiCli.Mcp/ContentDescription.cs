using System.Text.Json.Serialization;

namespace OptiCli.Mcp;

/// <summary>
/// One content item as a site's tool returns it to the assistant (<see cref="IOptiCliEditor.Describe"/>): the fields the
/// module's own tools give for content, and its links, so the assistant can read it further with the module's tools
/// (<c>get_content</c> takes <see cref="Ref"/>) and hand the editor a link.
/// </summary>
/// <remarks>
/// Serialized with the MCP SDK's JSON settings, as a tool method's result is: camelCase (<c>ref</c>, <c>name</c>,
/// <c>url</c>, <c>editUrl</c>, …, the names the module's own results use), and a field without a value left out.
/// </remarks>
public sealed record ContentDescription
{
    /// <summary>The version's reference (<c>123_456</c>), which the module's tools take wherever they take content.</summary>
    public required string Ref { get; init; }

    /// <summary>The content's id.</summary>
    public required int Id { get; init; }

    /// <summary>The version's id; none for content without versions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Version { get; init; }

    /// <summary>The content's GUID, the same in every version and language; the module's tools take it as a reference too.</summary>
    public required Guid Guid { get; init; }

    /// <summary>The content's name in <see cref="Language"/>, as the edit UI shows it.</summary>
    public required string Name { get; init; }

    /// <summary>The content type's name, e.g. <c>ArticlePage</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>The language branch, e.g. <c>en</c>; none for content that isn't localizable.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }

    /// <summary>The version's status, camelCase: <c>published</c>, <c>checkedOut</c> (a draft), <c>previouslyPublished</c>, …</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    /// <summary>The parent's id.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Parent { get; init; }

    /// <summary>Its public URL, with the site's host; none for content without one (a block, a folder).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }

    /// <summary>Where the editor opens this version in the CMS edit UI; it needs their sign-in there.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EditUrl { get; init; }
}
