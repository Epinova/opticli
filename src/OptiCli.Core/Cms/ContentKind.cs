namespace OptiCli.Core.Cms;

/// <summary>What a content type is, as users think of it (the DB spreads this over two columns).</summary>
public enum ContentKind
{
    Page,
    Block,
    Media,
    Folder,
    Other,
}

public static class ContentKinds
{
    /// <summary>
    /// Derives the kind from <c>tblContentType.Base</c> (Page, Block, Folder, Media, Image, Video,
    /// Setting, ...) and falls back to <c>tblContentType.ContentType</c> (0 page, 1 block) when
    /// Base is missing.
    /// </summary>
    public static ContentKind From(int contentType, string? typeBase) => typeBase?.ToLowerInvariant() switch
    {
        "page" => ContentKind.Page,
        "block" => ContentKind.Block,
        "folder" => ContentKind.Folder,
        "media" or "image" or "video" => ContentKind.Media,
        null or "" => contentType switch
        {
            0 => ContentKind.Page,
            1 => ContentKind.Block,
            _ => ContentKind.Other,
        },
        _ => ContentKind.Other,
    };
}
