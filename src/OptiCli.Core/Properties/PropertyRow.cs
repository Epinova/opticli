namespace OptiCli.Core.Properties;

/// <summary>
/// One stored property value (<c>tblContentProperty</c> or <c>tblWorkContentProperty</c>). Which column
/// holds the value depends on the property's base type.
/// </summary>
/// <param name="LanguageId">The branch the value belongs to (for work rows: the version's branch).</param>
/// <param name="BranchSpecific">
/// <c>BranchSpecificScope</c> of a value inside a block: true when the block property is culture-specific (the
/// value is stored per branch), false when it is shared (stored once, on the master branch, even when the inner
/// property is culture-specific). Null on top-level values and on block values saved by older CMS versions,
/// where the property's own culture setting decides.
/// </param>
/// <param name="Categories">
/// For a Category property: the category ids, from <c>tblContentCategory</c>/<c>tblWorkContentCategory</c>, whose
/// <c>CategoryType</c> is the row's <see cref="Number"/> and <c>ScopeName</c> the row's.
/// </param>
public sealed record PropertyRow(
    int ContentId,
    int DefinitionId,
    int LanguageId,
    string? ScopeName = null,
    bool? BranchSpecific = null,
    bool? Boolean = null,
    int? Number = null,
    double? FloatNumber = null,
    int? ContentType = null,
    int? ContentLink = null,
    DateTime? Date = null,
    string? String = null,
    string? LongString = null,
    Guid? LinkGuid = null,
    IReadOnlyList<int>? Categories = null);

public static class PropertyRows
{
    /// <summary>
    /// The rows that make up <paramref name="languageId"/>'s view of an item: culture-specific values from
    /// that branch, shared values from the master branch (which is the only place the CMS stores them).
    /// </summary>
    public static IEnumerable<PropertyRow> Effective(
        IEnumerable<PropertyRow> rows,
        int languageId,
        int masterLanguageId,
        Func<int, bool> isCultureSpecific) =>
        rows.Where(row => row.BranchSpecific ?? isCultureSpecific(row.DefinitionId)
            ? row.LanguageId == languageId
            : row.LanguageId == masterLanguageId);

    /// <summary>
    /// <see cref="Effective"/> as a SQL condition on the <c>tblContentProperty</c> row <paramref name="row"/>: its branch is
    /// <paramref name="language"/> when the value is branch-specific, else <paramref name="masterLanguage"/>.
    /// </summary>
    /// <param name="cultureSpecific">SQL that is 1 when the row's own property is culture-specific, else 0: what
    /// decides when <c>BranchSpecificScope</c> is null.</param>
    public static string EffectiveSql(string row, string cultureSpecific, string language, string masterLanguage) =>
        $"{row}.fkLanguageBranchID = CASE WHEN ISNULL({row}.BranchSpecificScope, {cultureSpecific}) = 1 THEN {language} ELSE {masterLanguage} END";
}
