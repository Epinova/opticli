using Microsoft.Data.SqlClient;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Properties;

namespace OptiCli.Core.Queries;

/// <summary>One place that references the target.</summary>
/// <param name="Property">Where in the owner: <c>MainArea</c>, <c>Hero.Link</c>, <c>MainArea[2].Text</c> (inline block).</param>
/// <param name="Kind">contentArea, contentReference, contentReferenceList, richTextLink, richTextBlock, link, linkCollection, url, text or softlink.</param>
/// <param name="Sources"><c>softlink</c> (the CMS's own link index) and/or <c>property</c> (a scan of stored values).</param>
/// <param name="Saved">When the owner's branch was last saved (its primary version), so owners can be sorted by recency.</param>
public sealed record Usage(
    string Ref, Guid Guid, string Type, string? Name, string? Language, string Status, string? Url, bool? Deleted,
    string Property, string Kind, IReadOnlyList<string> Sources, DateTime? Saved, string? ChangedBy);

/// <summary>
/// <c>where-used</c>: combines the CMS's soft link index (<c>tblContentSoftlink</c>, which covers rich text,
/// link and reference properties of saved versions) with a scan of primary property values
/// (ContentLink columns and GUIDs inside ContentArea, rich text and list markup).
/// </summary>
public sealed class WhereUsedReader(ContentSession session)
{
    private const string SoftlinksSql = """
        SELECT s.fkOwnerContentID, s.OwnerLanguageID, s.fkOwnerPropertyDefinitionID, s.LinkType
        FROM tblContentSoftlink s
        WHERE s.fkReferencedContentGUID = @guid
        """;

    private const string PropertiesSql = """
        SELECT p.fkContentID, p.fkLanguageBranchID, p.fkPropertyDefinitionID, p.ScopeName, 1 AS ByLink, 0 AS AsFragment
        FROM tblContentProperty p
        WHERE p.ContentLink = @id OR p.LinkGuid = @guid
        UNION ALL
        SELECT p.fkContentID, p.fkLanguageBranchID, p.fkPropertyDefinitionID, p.ScopeName, 0 AS ByLink,
               CASE WHEN p.LongString LIKE @fragment THEN 1 ELSE 0 END AS AsFragment
        FROM tblContentProperty p
        WHERE p.LongString COLLATE Latin1_General_BIN2 LIKE @d OR p.LongString COLLATE Latin1_General_BIN2 LIKE @n
           OR p.LongString COLLATE Latin1_General_BIN2 LIKE @upperD OR p.LongString COLLATE Latin1_General_BIN2 LIKE @upperN
           OR p.String LIKE @d OR p.String LIKE @n
        """;

    // The GUID scan compares binary: an order of magnitude faster than the database's case-insensitive
    // collation on tens of megabytes of markup. The CMS writes GUIDs in lower case; upper case is checked
    // too for hand-edited or imported values.
    private sealed record Found(int OwnerId, int LanguageId, int DefinitionId, string? Scope, string Kind, string Source);

    public async Task<IReadOnlyList<Usage>> FindAsync(ContentHeader target, CancellationToken cancellationToken)
    {
        var guidD = target.Guid.ToString("D");
        var guidN = target.Guid.ToString("N");
        var softlinks = await session.Db.QueryAsync(SoftlinksSql, r => (
                Owner: r.GetInt32("fkOwnerContentID"),
                Language: r.GetInt32OrNull("OwnerLanguageID"),
                Definition: r.GetInt32OrNull("fkOwnerPropertyDefinitionID"),
                LinkType: r.GetInt32OrNull("LinkType")),
            cancellationToken, new SqlParameter("@guid", target.Guid));
        var rows = await session.Db.QueryAsync(PropertiesSql, r => (
                Owner: r.GetInt32("fkContentID"),
                Language: r.GetInt32("fkLanguageBranchID"),
                Definition: r.GetInt32("fkPropertyDefinitionID"),
                Scope: r.GetStringOrNull("ScopeName"),
                ByLink: r.GetInt32("ByLink") == 1,
                AsFragment: r.GetInt32("AsFragment") == 1),
            cancellationToken,
            new SqlParameter("@id", target.Id),
            new SqlParameter("@guid", target.Guid),
            new SqlParameter("@d", WhereClause.ContainsPattern(guidD)),
            new SqlParameter("@n", WhereClause.ContainsPattern(guidN)),
            new SqlParameter("@upperD", WhereClause.ContainsPattern(guidD.ToUpperInvariant())),
            new SqlParameter("@upperN", WhereClause.ContainsPattern(guidN.ToUpperInvariant())),
            new SqlParameter("@fragment", WhereClause.ContainsPattern($"data-contentguid=\"{guidD}\"")));

        var found = rows
            .Where(r => r.Owner != target.Id)
            .Select(r => new Found(r.Owner, r.Language, r.Definition, r.Scope, Kind(r.Definition, r.ByLink, r.AsFragment), "property"))
            .ToList();

        // The soft link index names the property but not the position inside blocks; merge it into a
        // scanned usage of the same owner, branch and property when there is one.
        foreach (var link in softlinks.Where(s => s.Owner != target.Id))
        {
            var language = link.Language ?? 0;
            var definition = link.Definition ?? 0;
            var match = found.FirstOrDefault(f => f.OwnerId == link.Owner && f.LanguageId == language
                && (f.DefinitionId == definition || PropertyPaths.TopLevel(f.DefinitionId, f.Scope) == definition
                    || (ScopePath.Parse(f.Scope)?.Steps.Any(s => s.PropertyId == definition) ?? false)));
            found.Add(match is not null
                ? match with { Source = "softlink" }
                : new Found(link.Owner, language, definition, null, definition == 0 ? "softlink" : Kind(definition, false, false), "softlink"));
        }

        await session.Identities.LoadAsync(found.Select(f => f.OwnerId), [], cancellationToken);
        return found
            .GroupBy(f => (f.OwnerId, f.LanguageId, Path: PropertyPaths.Describe(session.Model, f.DefinitionId, f.Scope)))
            .Select(group =>
            {
                var first = group.OrderBy(f => f.Source == "property" ? 0 : 1).First();
                var header = session.Identities.Header(first.OwnerId);
                var identity = header is null
                    ? ContentIdentity.MissingId(first.OwnerId)
                    : session.Identities.Describe(header, session.Model.Language(first.LanguageId));
                var branch = header?.LanguageRow(first.LanguageId);
                return new Usage(
                    identity.Ref!, identity.Guid, identity.Type ?? "", identity.Name, identity.Language, identity.Status ?? "", identity.Url, identity.Deleted,
                    first.DefinitionId == 0 ? "(unknown)" : group.Key.Path,
                    first.Kind,
                    group.Select(f => f.Source).Distinct().Order().ToList(),
                    branch?.Saved,
                    string.IsNullOrEmpty(branch?.ChangedBy) ? null : branch.ChangedBy);
            })
            .OrderBy(u => u.Deleted == true ? 1 : 0)
            .ThenBy(u => u.Type, StringComparer.Ordinal)
            .ThenBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(u => u.Language, StringComparer.Ordinal)
            .ToList();
    }

    private string Kind(int definitionId, bool byLink, bool asFragment)
    {
        var definition = session.Model.Properties.GetValueOrDefault(definitionId);
        return (definition?.BaseType, definition?.TypeName) switch
        {
            (PropertyBaseType.ContentReference or PropertyBaseType.PageReference, _) => "contentReference",
            (_, "ContentArea") => "contentArea",
            (_, "ContentReferenceList") => "contentReferenceList",
            (_, "XhtmlString") => asFragment ? "richTextBlock" : "richTextLink",
            (_, "LinkItem") => "link",
            (PropertyBaseType.LinkCollection, _) => "linkCollection",
            (_, "Url") => "url",
            _ when byLink => "contentReference",
            (PropertyBaseType.Json, _) => "json",
            _ => "text",
        };
    }
}

/// <summary>A usage reached through blocks: <see cref="Via"/> is the chain of block refs from the owner down to the target.</summary>
public sealed record PageUsage(Usage Usage, IReadOnlyList<string> Via);

/// <summary>
/// <c>where-used --pages</c>: follows owners that are blocks up to the content that uses them (pages, media,
/// anything but blocks), so nested blocks (a block in a list block on a page) end at the page.
/// </summary>
public sealed class PageUsageReader(ContentSession session)
{
    /// <summary>How many blocks deep to follow; nesting deeper than this is almost always a reference cycle.</summary>
    public const int MaxDepth = 6;

    public async Task<IReadOnlyList<PageUsage>> FindAsync(ContentHeader target, CancellationToken cancellationToken)
    {
        var reader = new WhereUsedReader(session);
        var result = new List<PageUsage>();
        var visited = new HashSet<int> { target.Id };
        var level = new List<(ContentHeader Block, IReadOnlyList<string> Via)> { (target, []) };

        for (var depth = 0; depth < MaxDepth && level.Count > 0; depth++)
        {
            var next = new List<(ContentHeader, IReadOnlyList<string>)>();
            foreach (var (block, via) in level)
            {
                foreach (var usage in await reader.FindAsync(block, cancellationToken))
                {
                    var owner = int.TryParse(usage.Ref, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                        ? session.Identities.Header(id)
                        : null;
                    if (owner is not null && session.Model.Kind(owner.TypeId) == Cms.ContentKind.Block)
                    {
                        if (visited.Add(owner.Id))
                        {
                            next.Add((owner, [owner.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), .. via]));
                        }
                    }
                    else
                    {
                        result.Add(new PageUsage(usage, via));
                    }
                }
            }
            level = next;
        }
        return result
            .DistinctBy(u => (u.Usage.Ref, u.Usage.Language, u.Usage.Property, string.Join(",", u.Via)))
            .OrderByDescending(u => u.Usage.Saved)
            .ToList();
    }
}
