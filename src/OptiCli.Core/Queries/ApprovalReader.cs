using System.Globalization;
using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Queries;

/// <summary>
/// A content approval sequence that applies to an item: publishing goes through its reviewers, step by step.
/// </summary>
/// <param name="DefinedOn">The item the sequence is defined on: the item itself, or the ancestor it inherits it from.</param>
/// <param name="Inherited">Defined on an ancestor.</param>
public sealed record ApprovalSequence(string DefinedOn, bool Inherited, IReadOnlyList<ApprovalStep> Steps)
{
    /// <summary>"defined on 123, 2 steps: Legal (role Lawyers), Final (user anna)".</summary>
    public string Describe() =>
        $"{(Inherited ? $"inherited from {DefinedOn}" : $"defined on {DefinedOn}")}, {Steps.Count} step(s): {string.Join(", ", Steps.Select(s => $"{s.Name} ({string.Join(", ", s.Reviewers.Select(r => $"{r.Kind} {r.Name}"))})"))}";
}

public sealed record ApprovalStep(string Name, IReadOnlyList<ApprovalReviewer> Reviewers);

/// <param name="Kind"><c>role</c> or <c>user</c>.</param>
/// <param name="Languages">The language branches the reviewer reviews.</param>
public sealed record ApprovalReviewer(string Name, string Kind, IReadOnlyList<string> Languages);

/// <summary>
/// Reads content approval sequences from <c>tblApprovalDefinition*</c>. A definition is keyed <c>content:/&lt;id&gt;/</c>; an
/// item without one inherits the nearest ancestor's. A disabled definition turns approvals off for its subtree.
/// </summary>
public static class ApprovalReader
{
    private const string Sql = """
        SELECT d.ApprovalDefinitionKey, v.IsEnabled, s.StepIndex, s.StepName, r.Username, r.ReviewerType, r.fkLanguageBranchID
        FROM tblApprovalDefinition d
        JOIN tblApprovalDefinitionVersion v ON v.pkID = d.fkCurrentApprovalDefinitionVersionID
        LEFT JOIN tblApprovalDefinitionStep s ON s.fkApprovalDefinitionVersionID = v.pkID
        LEFT JOIN tblApprovalDefinitionReviewer r ON r.fkApprovalDefinitionStepID = s.pkID
        WHERE d.ApprovalDefinitionKey IN ({0})
        """;

    /// <summary>The sequence that applies to <paramref name="header"/> (and to new content below it, when it is the parent).</summary>
    /// <returns>Null when none applies (none defined, or the nearest one is disabled).</returns>
    public static async Task<ApprovalSequence?> ResolveAsync(CmsDatabase db, CmsModel model, ContentHeader header, CancellationToken cancellationToken)
    {
        // The item first, then its ancestors from the parent up to the root.
        var chain = header.AncestorIds.Reverse().Prepend(header.Id).ToList();
        var keys = chain.Select(Key).ToList();
        var sql = string.Format(CultureInfo.InvariantCulture, Sql, string.Join(',', keys.Select((_, i) => $"@k{i}")));
        var rows = await db.QueryAsync(sql, r => new Row(
            r.GetString(0),
            r.GetBoolean(1),
            r.IsDBNull(2) ? null : r.GetInt32(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? 0 : r.GetInt32(5),
            r.IsDBNull(6) ? null : r.GetInt32(6)), cancellationToken,
            keys.Select((k, i) => new Microsoft.Data.SqlClient.SqlParameter($"@k{i}", k)).ToArray());
        return Resolve(header.Id, chain, rows, id => model.Language(id)?.Code);
    }

    /// <summary>The definition key of content <paramref name="id"/>: <c>content:/123/</c>.</summary>
    public static string Key(int id) => $"content:/{id.ToString(CultureInfo.InvariantCulture)}/";

    /// <param name="chain">The item, then its ancestors nearest first.</param>
    internal static ApprovalSequence? Resolve(int id, IReadOnlyList<int> chain, IReadOnlyList<Row> rows, Func<int, string?> languageCode)
    {
        var byKey = rows.ToLookup(r => r.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in chain)
        {
            var definition = byKey[Key(candidate)].ToList();
            if (definition.Count == 0)
            {
                continue;
            }
            if (!definition[0].Enabled)
            {
                return null;
            }
            var steps = definition
                .Where(r => r.StepIndex is not null)
                .GroupBy(r => (Index: r.StepIndex!.Value, r.StepName))
                .OrderBy(g => g.Key.Index)
                .Select(step => new ApprovalStep(step.Key.StepName ?? $"Step {step.Key.Index + 1}", step
                    .Where(r => r.Reviewer is not null)
                    .GroupBy(r => (Name: r.Reviewer!, Kind: r.ReviewerType == 1 ? "role" : "user"))
                    .OrderBy(g => g.Key.Kind, StringComparer.Ordinal).ThenBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(reviewer => new ApprovalReviewer(reviewer.Key.Name, reviewer.Key.Kind,
                        reviewer.Select(r => r.LanguageId is { } language ? languageCode(language) : null).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList()))
                    .ToList()))
                .ToList();
            return new ApprovalSequence(candidate.ToString(CultureInfo.InvariantCulture), candidate != id, steps);
        }
        return null;
    }

    /// <summary>One reviewer of one step of a definition, or a definition without steps (nulls).</summary>
    /// <param name="ReviewerType">1 role, 0 user (<c>ApprovalDefinitionReviewerType</c>).</param>
    internal sealed record Row(string Key, bool Enabled, int? StepIndex, string? StepName, string? Reviewer, int ReviewerType, int? LanguageId);
}
