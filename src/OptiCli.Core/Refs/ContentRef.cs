namespace OptiCli.Core.Refs;

public enum ContentRefKind
{
    /// <summary><c>123</c> or <c>123_456</c> (content id, optionally a specific version).</summary>
    Id,

    /// <summary>A content GUID, also accepted as a permanent link <c>~/link/{guid}.aspx</c>.</summary>
    Guid,

    /// <summary>An absolute URL or a site-relative path; resolved against the site's hosts.</summary>
    Url,

    /// <summary>
    /// <c>63__provider</c>: content a content provider (e.g. a DAM) maps into the CMS. The id is local to this
    /// database (<c>tblMappedIdentity.pkID</c>); the content's GUID is what stays the same across environments.
    /// </summary>
    Provider,
}

/// <summary>
/// A reference to a content item as a user or agent typed it. Parsing is purely syntactic; whether
/// the item exists (and what a URL points at) is decided by the readers.
/// </summary>
public sealed record ContentRef
{
    private ContentRef(ContentRefKind kind) => Kind = kind;

    public ContentRefKind Kind { get; }

    /// <summary>Content id (<c>tblContent.pkID</c>) for <see cref="ContentRefKind.Id"/>.</summary>
    public int Id { get; private init; }

    /// <summary>Version id (<c>tblWorkContent.pkID</c>) when the ref was <c>id_version</c>.</summary>
    public int? VersionId { get; private init; }

    public Guid Guid { get; private init; }

    /// <summary>The URL or path, as given, for <see cref="ContentRefKind.Url"/>.</summary>
    public string? Url { get; private init; }

    /// <summary>The provider name for <see cref="ContentRefKind.Provider"/> (<see cref="Id"/> is its mapped id).</summary>
    public string? Provider { get; private init; }

    public static ContentRef ForId(int id, int? versionId = null) => new(ContentRefKind.Id) { Id = id, VersionId = versionId };

    public static ContentRef ForGuid(Guid guid) => new(ContentRefKind.Guid) { Guid = guid };

    public static ContentRef ForUrl(string url) => new(ContentRefKind.Url) { Url = url };

    public static ContentRef ForProvider(int id, string provider) => new(ContentRefKind.Provider) { Id = id, Provider = provider };

    /// <summary>The canonical form, which <see cref="ContentRefParser"/> parses back to an equal ref.</summary>
    public override string ToString() => Kind switch
    {
        ContentRefKind.Id => VersionId is { } version ? $"{Id}_{version}" : Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ContentRefKind.Guid => Guid.ToString("D"),
        ContentRefKind.Provider => $"{Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}__{Provider}",
        _ => Url!,
    };
}
