namespace OptiCli.Core.Cms;

/// <summary>What a content type is, as users think of it (the DB spreads this over several columns).</summary>
/// <remarks>
/// The last four are CMS 13's Visual Builder: an experience is a page whose content is a composition of sections; a section
/// is a block that holds rows, columns and elements; an element is a block type that can be placed in a section's columns
/// (composition behaviour <c>ElementEnabled</c>); a contract is an interface content types implement, never content itself.
/// They are appended so the older values keep their numbers.
/// </remarks>
public enum ContentKind
{
    Page,
    Block,
    Media,
    Folder,
    Other,
    Experience,
    Section,
    Element,
    Contract,
}

public static class ContentKinds
{
    /// <summary>The composition behaviour that lets a block type be placed in a Visual Builder section's columns.</summary>
    public const string ElementEnabled = "ElementEnabled";

    /// <summary>The composition behaviour that lets a type stand in a Visual Builder experience's outline as a section.</summary>
    public const string SectionEnabled = "SectionEnabled";

    /// <summary>
    /// Derives the kind from <c>tblContentType.Base</c> (Page, Block, Folder, Media, Image, Video, Setting, and on CMS 13
    /// Experience and Section) and falls back to <c>tblContentType.ContentType</c> (0 page, 1 block) when Base is missing.
    /// CMS 13: a contract (<c>IsContract</c>) is a contract whatever its base, and a block type with the
    /// <see cref="ElementEnabled"/> composition behaviour is an element. CMS 12 has none of these, so its kinds are the first
    /// five only.
    /// </summary>
    public static ContentKind From(int contentType, string? typeBase, IReadOnlyCollection<string>? compositionBehaviors = null, bool isContract = false)
    {
        if (isContract)
        {
            return ContentKind.Contract;
        }
        var kind = typeBase?.ToLowerInvariant() switch
        {
            "page" => ContentKind.Page,
            "experience" => ContentKind.Experience,
            "block" => ContentKind.Block,
            "section" => ContentKind.Section,
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
        return kind == ContentKind.Block && compositionBehaviors?.Contains(ElementEnabled, StringComparer.OrdinalIgnoreCase) == true ? ContentKind.Element : kind;
    }

    /// <summary>The behaviours in <c>tblContentType.CompositionBehavior</c> (comma-separated names); empty for none.</summary>
    public static IReadOnlyList<string> Behaviors(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? []
            : stored.Split([',', ';', ' ', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A page, or an experience (a page whose content is a Visual Builder composition): routable, with children and URLs.</summary>
    public static bool IsPage(this ContentKind kind) => kind is ContentKind.Page or ContentKind.Experience;

    /// <summary>A block, or a Visual Builder section or element (blocks too, in the CMS's classes).</summary>
    public static bool IsBlock(this ContentKind kind) => kind is ContentKind.Block or ContentKind.Section or ContentKind.Element;

    /// <summary>One of CMS 13's Visual Builder kinds that content can have: experience, section or element.</summary>
    public static bool IsComposition(this ContentKind kind) => kind is ContentKind.Experience or ContentKind.Section or ContentKind.Element;

    /// <summary>
    /// Whether <paramref name="kind"/> is what a <c>--kind</c> filter of <paramref name="wanted"/> asks for: <c>page</c> also
    /// matches experiences and <c>block</c> sections and elements (what they are in the CMS's classes); every other kind,
    /// <c>experience</c>, <c>section</c>, <c>element</c> and <c>contract</c> included, matches only itself.
    /// </summary>
    public static bool Matches(this ContentKind kind, ContentKind wanted) => wanted switch
    {
        ContentKind.Page => kind.IsPage(),
        ContentKind.Block => kind.IsBlock(),
        _ => kind == wanted,
    };

    /// <summary>The kind as the output names it (<c>page</c>, <c>experience</c>, ...).</summary>
    public static string Name(this ContentKind kind) => kind.ToString().ToLowerInvariant();
}
