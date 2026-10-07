using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Queries;

/// <summary>One instance of a type and where it is used.</summary>
/// <param name="Count">How many usages it has (0: unused).</param>
public sealed record InstanceUsage(string Ref, Guid Guid, string? Name, string? Language, string? Status, int Count, IReadOnlyList<Usage> Usages)
{
    /// <summary>CMS 13: true for a Visual Builder blueprint of the type, a template for new content rather than an instance.</summary>
    public bool? Blueprint { get; init; }
}

/// <summary>
/// CMS 13, <c>where-used --type</c> of a block type: its inline blocks, which aren't content of their own: every place a
/// branch's primary version holds one (the CMS's index <c>tblInlineBlockUsage</c>), Visual Builder sections and elements
/// included.
/// </summary>
/// <param name="Count">How many inline blocks of the type there are.</param>
public sealed record InlineUsages(int Count, IReadOnlyList<Usage> Usages)
{
    /// <summary>Marks the row of inline blocks among the instances.</summary>
    public bool Inline => true;
}

/// <summary>
/// <c>where-used --type</c>: <see cref="WhereUsedReader"/> for every instance of a content type (outside the recycle bin),
/// with the stored values read once (<see cref="PropertyReferenceIndex"/>) instead of scanned per instance; on CMS 13 also
/// the type's inline blocks (<see cref="InlineAsync"/>).
/// </summary>
public sealed class TypeUsageReader(ContentSession session)
{
    /// <summary>
    /// CMS 13: the type's inline blocks in each branch's primary version (published, else the common draft), from the
    /// CMS's index of them (<c>tblInlineBlockUsage</c>, which has no rows for content variations' versions). Null on CMS 12,
    /// whose output stays as it was.
    /// </summary>
    public async Task<InlineUsages?> InlineAsync(int typeId, CancellationToken cancellationToken)
    {
        var schema = session.Model.Schema;
        if (!schema.Compositions)
        {
            return null;
        }
        var rows = await session.Db.QueryAsync($"""
            SELECT DISTINCT u.fkContentID, w.fkLanguageBranchID, u.ScopeName
            FROM tblInlineBlockUsage u
            JOIN tblWorkContent w ON w.pkID = u.fkWorkContentID
            JOIN tblContent c ON c.pkID = u.fkContentID AND c.Deleted = 0
            JOIN tblContentLanguage cl ON cl.fkContentID = w.fkContentID AND cl.fkLanguageBranchID = w.fkLanguageBranchID
            {ContentHeaderReader.CommonDraftApply(schema)}
            WHERE u.fkContentTypeID = @type AND w.pkID = CASE WHEN cl.Status = {(int)VersionStatus.Published} THEN cl.Version ELSE cd.CommonDraftId END
            """, r => (Content: r.GetInt32("fkContentID"), Language: r.GetInt32("fkLanguageBranchID"), Scope: r.GetStringOrNull("ScopeName") ?? ""),
            cancellationToken, new SqlParameter("@type", typeId));
        if (rows.Count == 0)
        {
            return new InlineUsages(0, []);
        }
        await session.Identities.LoadAsync(rows.Select(r => r.Content), [], cancellationToken);
        var compositions = new Properties.CompositionPaths(session);
        await compositions.PrepareAsync(rows.Select(r => (r.Content, r.Language)), cancellationToken);
        var usages = rows.Select(r =>
            {
                var header = session.Identities.Header(r.Content)!;
                var identity = session.Identities.Describe(header, session.Model.Language(r.Language));
                var branch = header.LanguageRow(r.Language);
                var place = compositions.DescribeItem(r.Content, r.Language, r.Scope);
                return new Usage(
                    identity.Ref!, identity.Guid, identity.Type ?? "", identity.Name, identity.Language, identity.Status ?? "", identity.Url, identity.Deleted,
                    place?.Property ?? ItemPath(r.Scope), "inline", ["inlineBlockUsage"], branch?.Saved, string.IsNullOrEmpty(branch?.ChangedBy) ? null : branch.ChangedBy)
                {
                    Blueprint = identity.Blueprint,
                    Section = place?.Section,
                    Element = place?.Element,
                };
            })
            .OrderBy(u => u.Blueprint == true ? 1 : 0)
            .ThenBy(u => u.Type, StringComparer.Ordinal)
            .ThenBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(u => u.Language, StringComparer.Ordinal)
            .ToList();
        return new InlineUsages(usages.Count, usages);
    }

    /// <summary><c>MainArea[2]</c> for an inline block's scope (<c>.103:20(2)</c>), outside a composition.</summary>
    private string ItemPath(string scope) =>
        string.Join(".", scope.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(segment =>
        {
            var property = segment.Split(':', '(')[0];
            var index = segment.Contains('(') ? segment[segment.IndexOf('(')..] : "";
            return int.TryParse(property, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                ? (session.Model.Properties.GetValueOrDefault(id)?.Name ?? $"#{property}") + index.Replace("(", "[").Replace(")", "]")
                : segment;
        }));

    /// <returns>Every instance, the most used first; unused ones last, by id.</returns>
    public async Task<IReadOnlyList<InstanceUsage>> FindAsync(int typeId, CancellationToken cancellationToken)
    {
        var ids = await session.Db.QueryAsync("SELECT pkID FROM tblContent WHERE fkContentTypeID = @type AND Deleted = 0 ORDER BY pkID",
            r => r.GetInt32(0), cancellationToken, new SqlParameter("@type", typeId));
        if (ids.Count == 0)
        {
            return [];
        }
        var headers = await ContentHeaderReader.ByIdsAsync(session.Db, ids, cancellationToken);
        var reader = new WhereUsedReader(session, ids.Count > PageUsageReader.ScansBeforeIndex ? await PropertyReferenceIndex.LoadAsync(session.Db, cancellationToken) : null);
        var result = new List<InstanceUsage>();
        foreach (var id in ids)
        {
            if (!headers.TryGetValue(id, out var header))
            {
                continue;
            }
            var usages = await reader.FindAsync(header, cancellationToken);
            var identity = session.Identities.Describe(header, null);
            result.Add(new InstanceUsage(identity.Ref!, header.Guid, identity.Name, identity.Language, identity.Status, usages.Count, usages) { Blueprint = identity.Blueprint });
        }
        // Blueprints after the instances.
        return result.OrderBy(r => r.Blueprint == true ? 1 : 0).ThenByDescending(r => r.Count).ThenBy(r => int.Parse(r.Ref, System.Globalization.CultureInfo.InvariantCulture)).ToList();
    }
}
