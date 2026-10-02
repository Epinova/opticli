namespace OptiCli.Cms.Content;

/// <summary>What a content type is, for where its content may be placed in the tree.</summary>
internal enum PlacementKind
{
    Page,
    Block,
    Media,
    Folder,
    Other,
}

/// <summary>
/// Where new or moved content may go: pages in the page tree, blocks, media and folders in asset folders (where the edit
/// UI puts them), and only where the parent type's availability (<c>[AvailableContentTypes]</c>, admin mode's settings)
/// allows the type. EPiServer-free: the CMS's own <c>ContentTypeAvailabilityService</c> answers the last part.
/// </summary>
internal static class ContentPlacement
{
    /// <param name="parentType">The parent's type name; with <paramref name="parentId"/>, for the message.</param>
    /// <param name="allowedByParentType">The CMS's availability answer for the child's type below the parent's.</param>
    /// <returns>Why the content can't go there; null when it can.</returns>
    /// <remarks>
    /// The CMS keeps a site's "For this site" folder below its start page, but opticli doesn't create or move folders
    /// there: a type's availability can't tell such a folder apart (<c>ExcludeOn</c> elsewhere turns a type's
    /// availability into a list of every type).
    /// </remarks>
    public static string? Problem(PlacementKind childKind, string childType, PlacementKind parentKind, string parentType, int parentId, bool allowedByParentType)
    {
        var below = $"below {parentType} ({parentId})";
        switch (childKind, parentKind)
        {
            case (_, PlacementKind.Block):
                return $"{parentType} ({parentId}) is a block, which can't have children.";
            case (_, PlacementKind.Media):
                return $"{parentType} ({parentId}) is a media item, which can't have children.";
            case (PlacementKind.Page, PlacementKind.Folder):
                return $"{childType} is a page type; pages go in the page tree, not {below}, an asset folder.";
            case (PlacementKind.Block or PlacementKind.Media or PlacementKind.Folder, PlacementKind.Page):
                return $"{childType} is a {Name(childKind)} type; {Plural(childKind)} go in asset folders, not {below}, a page. "
                    + "A page's own blocks and files go in its \"For this page\" folder (forContent; --for <page> on the command line).";
        }
        return allowedByParentType ? null : $"{childType} is not allowed {below}.";
    }

    private static string Name(PlacementKind kind) => kind switch
    {
        PlacementKind.Block => "block",
        PlacementKind.Media => "media",
        _ => "folder",
    };

    private static string Plural(PlacementKind kind) => kind == PlacementKind.Media ? "media" : $"{Name(kind)}s";
}
