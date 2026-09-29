namespace OptiCli.Core.Cms;

/// <param name="MasterOf">Sites whose start page has this as its master language.</param>
/// <param name="ContentItems">Content items that have a version in this language.</param>
public sealed record LanguageInfo(
    int Id,
    string Code,
    string? Name,
    bool Enabled,
    int SortIndex,
    string? UrlSegment,
    int ContentItems,
    IReadOnlyList<string> MasterOf);
