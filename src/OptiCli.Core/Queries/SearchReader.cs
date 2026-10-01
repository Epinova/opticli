using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Properties;

namespace OptiCli.Core.Queries;

public enum SearchScope
{
    Names,
    Strings,
    All,
}

/// <param name="Property">The matching property (<c>Block.Property</c> inside local blocks), or <c>name</c>.</param>
/// <param name="Snippet">Text around the first occurrence, tags stripped.</param>
public sealed record SearchMatch(string Property, string Snippet);

public sealed record SearchHit(
    string Ref, Guid Guid, string Type, string? Name, string? Language, string Status, string? Url, bool? Deleted,
    IReadOnlyList<SearchMatch> Matches);

/// <summary>
/// <c>search</c>: content names and text properties containing a string, in every language. Reads only
/// content tables (<c>tblContentLanguage</c>, <c>tblContentProperty</c>), never form submissions or user data.
/// </summary>
public sealed partial class SearchReader(ContentSession session)
{
    /// <summary>Rows fetched per source; enough for any sensible page, small enough to stay fast.</summary>
    public const int MaxRows = 2000;

    private const int SnippetRadius = 60;

    private const string NamesSql = """
        SELECT TOP (@cap) cl.fkContentID, cl.fkLanguageBranchID, cl.Name
        FROM tblContentLanguage cl
        JOIN tblContent c ON c.pkID = cl.fkContentID AND c.Deleted = 0
        WHERE cl.Name LIKE @pattern ESCAPE '\' AND (@lang IS NULL OR cl.fkLanguageBranchID = @lang)
        ORDER BY cl.fkContentID
        """;

    // ContentArea markup only carries block names, which the names search already covers. Values are
    // lower-cased and compared binary, which is an order of magnitude faster than the database's
    // case-insensitive collation over tens of megabytes of text, with the same matches. With --lang, values come
    // from the rows get shows in that language: shared ones from the master branch (attributed to the language asked
    // for), culture-specific ones (LanguageSpecific 4) from its own branch.
    private static readonly string StringsSql = $"""
        SELECT TOP (@cap) p.fkContentID, ISNULL(@lang, p.fkLanguageBranchID) AS fkLanguageBranchID, p.fkPropertyDefinitionID, p.ScopeName,
               SUBSTRING(x.v, CASE WHEN y.pos > @radius THEN y.pos - @radius ELSE 1 END, 2 * @radius + LEN(@text) + 200) AS Snippet
        FROM tblContentProperty p
        JOIN tblContent c ON c.pkID = p.fkContentID AND c.Deleted = 0
        JOIN tblPropertyDefinition pd ON pd.pkID = p.fkPropertyDefinitionID
        JOIN tblPropertyDefinitionType t ON t.pkID = pd.fkPropertyDefinitionTypeID
        CROSS APPLY (SELECT COALESCE(p.String, p.LongString) AS v) x
        CROSS APPLY (SELECT CHARINDEX(@text, x.v) AS pos) y
        WHERE t.Property IN (6, 7) AND t.Name <> 'ContentArea'
          AND LOWER(COALESCE(p.String, p.LongString)) COLLATE Latin1_General_BIN2 LIKE @lowerPattern ESCAPE '\'
          AND (@lang IS NULL OR ({PropertyRows.EffectiveSql("p", "CASE WHEN pd.LanguageSpecific = 4 THEN 1 ELSE 0 END", "@lang", "c.fkMasterLanguageBranchID")}
               AND EXISTS (SELECT 1 FROM tblContentLanguage cl WHERE cl.fkContentID = c.pkID AND cl.fkLanguageBranchID = @lang)))
        ORDER BY p.fkContentID
        """;

    /// <returns>Hits grouped per content item and language, and whether a source hit <see cref="MaxRows"/>.</returns>
    public async Task<(IReadOnlyList<SearchHit> Hits, bool Capped)> SearchAsync(string text, SearchScope scope, LanguageBranch? language, CancellationToken cancellationToken)
    {
        var matches = new List<(int ContentId, int LanguageId, SearchMatch Match)>();
        var capped = false;
        SqlParameter[] Parameters() =>
        [
            new("@cap", MaxRows),
            new("@pattern", WhereClause.ContainsPattern(text)),
            new("@lowerPattern", WhereClause.ContainsPattern(text.ToLowerInvariant())),
            new("@text", text),
            new("@radius", SnippetRadius),
            new("@lang", System.Data.SqlDbType.Int) { Value = (object?)language?.Id ?? DBNull.Value },
        ];

        if (scope is SearchScope.Names or SearchScope.All)
        {
            var rows = await session.Db.QueryAsync(NamesSql,
                r => (r.GetInt32("fkContentID"), r.GetInt32("fkLanguageBranchID"), new SearchMatch("name", r.GetStringOrNull("Name") ?? "")),
                cancellationToken, Parameters().Where(p => p.ParameterName is "@cap" or "@pattern" or "@lang").ToArray());
            capped |= rows.Count >= MaxRows;
            matches.AddRange(rows);
        }

        if (scope is SearchScope.Strings or SearchScope.All)
        {
            var rows = await session.Db.QueryAsync(StringsSql, r => (
                    ContentId: r.GetInt32("fkContentID"),
                    LanguageId: r.GetInt32("fkLanguageBranchID"),
                    Definition: r.GetInt32("fkPropertyDefinitionID"),
                    Scope: r.GetStringOrNull("ScopeName"),
                    Snippet: r.GetStringOrNull("Snippet") ?? ""),
                cancellationToken, Parameters().Where(p => p.ParameterName != "@pattern").ToArray());
            capped |= rows.Count >= MaxRows;
            matches.AddRange(rows.Select(r => (r.ContentId, r.LanguageId, new SearchMatch(PropertyPaths.Describe(session.Model, r.Definition, r.Scope), Snippet(r.Snippet, text)))));
        }

        await session.Identities.LoadAsync(matches.Select(m => m.ContentId), [], cancellationToken);
        var hits = matches
            .GroupBy(m => (m.ContentId, m.LanguageId))
            .Select(group =>
            {
                var header = session.Identities.Header(group.Key.ContentId)!;
                var identity = session.Identities.Describe(header, session.Model.Language(group.Key.LanguageId));
                return new SearchHit(identity.Ref!, identity.Guid, identity.Type!, identity.Name, identity.Language, identity.Status!, identity.Url,
                    identity.Deleted, group.Select(m => m.Match).DistinctBy(m => m.Property).ToList());
            })
            .OrderBy(h => h.Matches.Any(m => m.Property == "name") ? 0 : 1)
            .ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return (hits, capped);
    }

    /// <summary>Text around the match, markup removed and whitespace collapsed.</summary>
    public static string Snippet(string raw, string text)
    {
        var plain = Whitespace().Replace(System.Net.WebUtility.HtmlDecode(Tags().Replace(raw, " ")), " ").Trim();
        var at = plain.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return plain.Length <= 2 * SnippetRadius ? plain : plain[..(2 * SnippetRadius)] + "…";
        }
        var start = Math.Max(0, at - SnippetRadius);
        var end = Math.Min(plain.Length, at + text.Length + SnippetRadius);
        return (start > 0 ? "…" : "") + plain[start..end] + (end < plain.Length ? "…" : "");
    }

    [GeneratedRegex("<[^>]*>?")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
