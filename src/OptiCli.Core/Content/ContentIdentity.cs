using System.Globalization;

namespace OptiCli.Core.Content;

/// <summary>
/// How every command names a content item. <see cref="Ref"/> can be passed back to any command.
/// </summary>
/// <param name="Ref"><c>123</c>, or <c>123_456</c> when a specific version is meant; null for a missing target.</param>
/// <param name="Language">The branch shown; null for language-invariant content (media, folders).</param>
/// <param name="Status">Status of that branch or version (<c>published</c>, <c>checkedOut</c>, ...).</param>
/// <param name="Url">Site-relative URL for pages and media, when the item is routable.</param>
public sealed record ContentIdentity(string? Ref, Guid Guid, string? Type, string? Name, string? Language, string? Status, string? Url)
{
    /// <summary>
    /// CMS 13: <c>experience</c>, <c>section</c> or <c>element</c> for Visual Builder content (an experience's
    /// composition is in <c>get</c>); left out for every other kind, which <see cref="Type"/> and <c>opticli types</c> tell.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>True for items in the recycle bin.</summary>
    public bool? Deleted { get; init; }

    /// <summary>CMS 13: true for a Visual Builder blueprint, a template for new content rather than content itself.</summary>
    public bool? Blueprint { get; init; }

    /// <summary>Content served by a content provider (e.g. a DAM), referenced as <c>id__provider</c>; not stored in this database.</summary>
    public string? Provider { get; init; }

    /// <summary>True when something references a GUID or id that no content item has.</summary>
    public bool? Missing { get; init; }

    public static string RefFor(int id, int? version = null) =>
        version is { } v
            ? $"{id.ToString(CultureInfo.InvariantCulture)}_{v.ToString(CultureInfo.InvariantCulture)}"
            : id.ToString(CultureInfo.InvariantCulture);

    public static ContentIdentity MissingGuid(Guid guid) => new(null, guid, null, null, null, null, null) { Missing = true };

    public static ContentIdentity FromProvider(Guid guid, int id, string provider) =>
        new($"{id.ToString(CultureInfo.InvariantCulture)}__{provider}", guid, null, null, null, null, null) { Provider = provider };

    public static ContentIdentity MissingId(int id) => new(RefFor(id), Guid.Empty, null, null, null, null, null) { Missing = true };
}
