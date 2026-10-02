using System.Globalization;
using EPiServer.Core;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>One page of content items, as list-style operations return them.</summary>
/// <param name="Items">Only what the caller may read: content an editor can't read is left out without a trace.</param>
/// <param name="Next">The cursor to pass for the next page; null when there is nothing more.</param>
internal sealed record ContentList(IReadOnlyList<ContentSummary> Items, int? Next)
{
    /// <summary>The content the list is about (the parent, the search root), when there is one.</summary>
    public ContentSummary? Of { get; init; }

    /// <summary>
    /// For a search: true when it stopped before it had looked everywhere below its root, at the result limit or at the
    /// most items it looks at, so there may be more matches. Null otherwise.
    /// </summary>
    public bool? Truncated { get; init; }
}

/// <summary>Cursor and page size checks the list-style operations share.</summary>
internal static class Paging
{
    /// <summary>The page size: the default when not given, at most <paramref name="max"/> (larger asks get the most).</summary>
    /// <exception cref="AgentException"><c>usage</c> for a size below 1.</exception>
    public static int Limit(int? requested, int defaultSize, int max) => requested switch
    {
        null => defaultSize,
        < 1 => throw AgentException.Usage($"limit must be at least 1 (and at most {max})."),
        _ => Math.Min(requested.Value, max),
    };

    /// <summary>Where a page starts: a cursor from the previous page's <see cref="ContentList.Next"/>, or 0.</summary>
    /// <exception cref="AgentException"><c>usage</c> for a negative cursor.</exception>
    public static int Cursor(int? cursor) => cursor switch
    {
        null => 0,
        < 0 => throw AgentException.Usage("cursor must be 0 or a next value from an earlier page."),
        _ => cursor.Value,
    };

    /// <summary>
    /// How to load content listed below other content: in <paramref name="lang"/> (falling back to the master language,
    /// as the edit UI's tree does for content not translated yet), else each item's master language.
    /// </summary>
    public static LoaderOptions Language(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return new LoaderOptions { LanguageLoaderOption.MasterLanguage() };
        }
        try
        {
            return new LoaderOptions { LanguageLoaderOption.FallbackWithMaster(CultureInfo.GetCultureInfo(lang.Trim())) };
        }
        catch (CultureNotFoundException)
        {
            throw AgentException.Usage($"'{lang}' is not a language code.");
        }
    }
}
