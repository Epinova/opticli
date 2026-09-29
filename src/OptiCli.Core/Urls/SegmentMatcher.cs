namespace OptiCli.Core.Urls;

/// <summary>A child's URL segment in one language branch.</summary>
/// <param name="Published">Whether that branch is published; the CMS routes a never-published branch by the master branch's segment.</param>
public sealed record ChildSegment(int ContentId, int LanguageId, string Segment, bool Published = true);

public static class SegmentMatcher
{
    /// <summary>
    /// The child a URL segment selects: one whose segment in <paramref name="languageId"/> matches, else
    /// (as the CMS falls back for missing and never-published branches) one without a published branch in
    /// that language that matches in another language. Case-insensitive. A null language (assets) matches any branch.
    /// </summary>
    public static ChildSegment? Match(IEnumerable<ChildSegment> children, string segment, int? languageId)
    {
        var list = children.ToList();
        var matching = list.Where(c => string.Equals(c.Segment, segment, StringComparison.OrdinalIgnoreCase)).ToList();
        if (languageId is not { } language)
        {
            return matching.OrderBy(c => c.ContentId).FirstOrDefault();
        }

        var inLanguage = matching.Where(c => c.LanguageId == language).OrderBy(c => c.ContentId).FirstOrDefault();
        if (inLanguage is not null)
        {
            return inLanguage;
        }

        var withBranch = list.Where(c => c.LanguageId == language && c.Published).Select(c => c.ContentId).ToHashSet();
        return matching.Where(c => !withBranch.Contains(c.ContentId)).OrderBy(c => c.ContentId).FirstOrDefault();
    }
}
