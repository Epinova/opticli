using OptiCli.Core.Content;
using OptiCli.Core.Data;

namespace OptiCli.Core.Queries;

/// <summary>A primary property value that references an item: by its ContentLink/LinkGuid column, or by GUID in its text.</summary>
/// <param name="AsFragment">The GUID is a block inlined in rich text (<c>data-contentguid="…"</c>).</param>
internal sealed record PropertyHit(int Owner, int Language, int Definition, string? Scope, bool ByLink, bool AsFragment);

/// <summary>
/// Every reference stored in primary property values (<c>tblContentProperty</c>), read in one pass: what
/// <see cref="WhereUsedReader"/>'s scan finds for one item, for any number of items. <c>where-used --pages</c> looks up
/// each block on the way to the pages here. Its scan per block is bound by the text comparisons, so one query that
/// matches several GUIDs takes as long as one query per GUID; this reads the text once instead.
/// </summary>
public sealed class PropertyReferenceIndex
{
    private const string RowsSql = """
        SELECT p.fkContentID, p.fkLanguageBranchID, p.fkPropertyDefinitionID, p.ScopeName, p.ContentLink, p.LinkGuid, p.String, p.LongString
        FROM tblContentProperty p
        WHERE p.ContentLink IS NOT NULL OR p.LinkGuid IS NOT NULL OR p.String IS NOT NULL OR p.LongString IS NOT NULL
        """;

    private readonly Dictionary<int, List<PropertyHit>> _byContentLink = [];
    private readonly Dictionary<Guid, List<PropertyHit>> _byLinkGuid = [];
    private readonly Dictionary<Guid, List<PropertyHit>> _byText = [];

    public static async Task<PropertyReferenceIndex> LoadAsync(CmsDatabase db, CancellationToken cancellationToken)
    {
        var index = new PropertyReferenceIndex();
        // The mapper indexes each row as it is read, so the text itself is never kept.
        await db.QueryAsync(RowsSql, r =>
        {
            index.Add(
                r.GetInt32("fkContentID"), r.GetInt32("fkLanguageBranchID"), r.GetInt32("fkPropertyDefinitionID"), r.GetStringOrNull("ScopeName"),
                r.GetInt32OrNull("ContentLink"), r.GetGuidOrNull("LinkGuid"), r.GetStringOrNull("String"), r.GetStringOrNull("LongString"));
            return 0;
        }, cancellationToken);
        return index;
    }

    internal void Add(int owner, int language, int definition, string? scope, int? contentLink, Guid? linkGuid, string? text, string? longText)
    {
        if (contentLink is not null || linkGuid is not null)
        {
            var hit = new PropertyHit(owner, language, definition, scope, ByLink: true, AsFragment: false);
            if (contentLink is { } id)
            {
                Append(_byContentLink, id, hit);
            }
            if (linkGuid is { } guid)
            {
                Append(_byLinkGuid, guid, hit);
            }
        }

        var found = new Dictionary<Guid, bool>();
        foreach (var (guid, fragment) in GuidText.Find(longText).Concat(GuidText.Find(text).Select(g => (g.Guid, Fragment: false))))
        {
            found[guid] = fragment || found.GetValueOrDefault(guid);
        }
        foreach (var (guid, fragment) in found)
        {
            Append(_byText, guid, new PropertyHit(owner, language, definition, scope, ByLink: false, fragment));
        }
    }

    /// <summary>The values referencing <paramref name="target"/>, as <see cref="WhereUsedReader"/>'s scan returns them.</summary>
    internal IEnumerable<PropertyHit> For(ContentHeader target) =>
        _byContentLink.GetValueOrDefault(target.Id, [])
            .Concat(_byLinkGuid.GetValueOrDefault(target.Guid, []))
            .Distinct<PropertyHit>(ReferenceEqualityComparer.Instance)
            .Concat(_byText.GetValueOrDefault(target.Guid, []));

    private static void Append<TKey>(Dictionary<TKey, List<PropertyHit>> map, TKey key, PropertyHit hit) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }
        list.Add(hit);
    }
}

/// <summary>
/// GUIDs written in text, as <c>LIKE '%guid%'</c> finds them: <c>D</c> form (with dashes) anywhere, and <c>N</c> form
/// (32 hex digits) anywhere, including inside a longer run of hex digits. Either case.
/// </summary>
internal static class GuidText
{
    private const string FragmentPrefix = "data-contentguid=\"";

    /// <returns>Each GUID once; <c>Fragment</c> when one of its occurrences is <c>data-contentguid="…"</c>.</returns>
    public static IEnumerable<(Guid Guid, bool Fragment)> Find(string? text)
    {
        if (text is null || text.Length < 32)
        {
            return [];
        }
        var found = new Dictionary<Guid, bool>();
        for (var i = 0; i + 36 <= text.Length; i++)
        {
            if (text[i + 8] == '-' && text[i + 13] == '-' && text[i + 18] == '-' && text[i + 23] == '-' && IsDashedHex(text.AsSpan(i, 36)))
            {
                var guid = Guid.ParseExact(text.AsSpan(i, 36), "D");
                var fragment = i >= FragmentPrefix.Length
                    && text.AsSpan(i - FragmentPrefix.Length, FragmentPrefix.Length).Equals(FragmentPrefix, StringComparison.OrdinalIgnoreCase)
                    && i + 36 < text.Length && text[i + 36] == '"';
                found[guid] = fragment || found.GetValueOrDefault(guid);
            }
        }
        for (var start = 0; start < text.Length;)
        {
            var end = start;
            while (end < text.Length && char.IsAsciiHexDigit(text[end]))
            {
                end++;
            }
            for (var i = start; i + 32 <= end; i++)
            {
                found.TryAdd(Guid.ParseExact(text.AsSpan(i, 32), "N"), false);
            }
            start = end + 1;
        }
        return found.Select(f => (f.Key, f.Value));
    }

    /// <summary>Hex digits with the dashes already checked by the caller (Guid parsing alone would also take whitespace).</summary>
    private static bool IsDashedHex(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c) && c != '-')
            {
                return false;
            }
        }
        return true;
    }
}
