namespace OptiCli.Core.Content;

/// <summary>One row of <c>tblContentLanguageSetting</c>: how a language is handled on a node and the content below it.</summary>
/// <param name="ReplacementId">A branch shown instead of this language, whether or not the content has this one.</param>
/// <param name="Fallback">Language codes to show, in order, where the content has no branch in this language.</param>
/// <param name="Active">The language is available for new content there.</param>
public sealed record LanguageSetting(int ContentId, int LanguageId, int? ReplacementId, IReadOnlyList<string> Fallback, bool Active);

/// <summary>Which branch the language settings show for a requested language.</summary>
/// <param name="Branch">The branch shown; null when the settings show the content in no language for this one.</param>
/// <param name="Rule"><c>replacement</c>, <c>fallback</c>, or <c>none</c> (no branch, no fallback that has one).</param>
/// <param name="DefinedOn">The content the settings are defined on: the item, or the ancestor whose settings it inherits.</param>
public sealed record LanguageChoice(int? Branch, string Rule, int DefinedOn);

/// <summary>
/// Language settings per subtree (edit mode's Language Settings): the nearest node with settings, the item itself or an
/// ancestor, decides replacement and fallback languages for everything below it.
/// </summary>
public sealed class LanguageSettings
{
    public static readonly LanguageSettings None = new([]);

    private readonly ILookup<int, LanguageSetting> _byContent;

    public LanguageSettings(IEnumerable<LanguageSetting> rows) => _byContent = rows.ToLookup(r => r.ContentId);

    /// <summary>The settings of the nearest node that has any: <paramref name="id"/>, then its ancestors from the parent up.</summary>
    /// <param name="ancestorIds">Root first, as <see cref="ContentHeader.AncestorIds"/>.</param>
    public (int DefinedOn, IReadOnlyList<LanguageSetting> Rows)? Nearest(int id, IReadOnlyList<int> ancestorIds)
    {
        foreach (var candidate in ancestorIds.Reverse().Prepend(id))
        {
            if (_byContent[candidate].ToList() is { Count: > 0 } rows)
            {
                return (candidate, rows);
            }
        }
        return null;
    }

    /// <summary>
    /// The branch shown for <paramref name="requested"/> when the settings decide it: a replacement language, or (the
    /// content lacking the branch) the first fallback it has. Null when no settings apply, or the branch is shown as is.
    /// </summary>
    /// <param name="branches">The content's branches.</param>
    public LanguageChoice? Choose(int id, IReadOnlyList<int> ancestorIds, IReadOnlyCollection<int> branches, int requested, Func<string, LanguageBranch?> byCode)
    {
        if (Nearest(id, ancestorIds) is not { } settings)
        {
            return null;
        }
        var row = settings.Rows.FirstOrDefault(r => r.LanguageId == requested);
        if (row?.ReplacementId is { } replacement && replacement != requested && branches.Contains(replacement))
        {
            return new LanguageChoice(replacement, "replacement", settings.DefinedOn);
        }
        if (branches.Contains(requested))
        {
            return null;
        }
        foreach (var code in row?.Fallback ?? [])
        {
            if (byCode(code) is { } fallback && branches.Contains(fallback.Id))
            {
                return new LanguageChoice(fallback.Id, "fallback", settings.DefinedOn);
            }
        }
        return new LanguageChoice(null, "none", settings.DefinedOn);
    }

    /// <summary>The languages active on <paramref name="startPageId"/>, when it has settings: a site with them only uses those.</summary>
    /// <returns>Null when the start page has no settings of its own.</returns>
    public IReadOnlySet<int>? ActiveOn(int startPageId) =>
        _byContent[startPageId].ToList() is { Count: > 0 } rows ? rows.Where(r => r.Active).Select(r => r.LanguageId).ToHashSet() : null;

    /// <summary><c>LanguageBranchFallback</c>: comma-separated codes, in order.</summary>
    public static IReadOnlyList<string> ParseFallback(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? [] : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
