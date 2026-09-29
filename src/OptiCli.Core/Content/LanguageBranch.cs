namespace OptiCli.Core.Content;

/// <summary>One row of <c>tblLanguageBranch</c>.</summary>
/// <param name="Code">Language code (<c>en</c>, <c>sv</c>); empty for the invariant branch media and folders use.</param>
/// <param name="UrlSegment">Segment used in URLs when it differs from the code.</param>
public sealed record LanguageBranch(int Id, string Code, string? Name, string? UrlSegment, bool Enabled)
{
    public bool IsInvariant => Code.Length == 0;

    /// <summary>The first URL segment that selects this language (<c>/en/</c>).</summary>
    public string UrlPrefix => string.IsNullOrWhiteSpace(UrlSegment) ? Code : UrlSegment.Trim();

    /// <summary>The code as shown in output; null for the invariant branch.</summary>
    public string? DisplayCode => IsInvariant ? null : Code;
}
