using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Refs;
using OptiCli.Core.Text;

namespace OptiCli.Core.Queries;

/// <summary>A Visual Builder blueprint (CMS 13): a template for new content, stored as content of its own.</summary>
/// <param name="Guid">Its content GUID, which is the blueprint's id in the CMS's API.</param>
public sealed record BlueprintInfo(int Id, Guid Guid, int TypeId, string Name);

/// <summary>Finds a blueprint by ref (id, GUID) or by name, for <c>create --blueprint</c> and <c>composition add --blueprint</c>.</summary>
public static class BlueprintReader
{
    /// <exception cref="UsageException">Not CMS 13, a ref to content that isn't a blueprint, or a name more than one blueprint has.</exception>
    /// <exception cref="NotFoundException">No blueprint has that id, GUID or name.</exception>
    public static async Task<BlueprintInfo> FindAsync(ContentSession session, string reference, CancellationToken cancellationToken)
    {
        if (!session.Model.Schema.Blueprints)
        {
            throw new UsageException("Visual Builder blueprints are CMS 13's; this site's database has none.");
        }
        var all = await session.Db.QueryAsync("""
            SELECT c.pkID, c.ContentGUID, c.fkContentTypeID, ISNULL(cl.Name, '') AS Name
            FROM tblContent c
            LEFT JOIN tblContentLanguage cl ON cl.fkContentID = c.pkID
            WHERE c.Blueprint = 1 AND c.Deleted = 0
            ORDER BY c.pkID
            """, r => new BlueprintInfo(r.GetInt32("pkID"), r.GetGuid("ContentGUID"), r.GetInt32("fkContentTypeID"), r.GetString("Name")), cancellationToken);
        var text = reference.Trim();
        if (ContentRefParser.TryParse(text, out var parsed, out _) && parsed.Kind is ContentRefKind.Id or ContentRefKind.Guid)
        {
            var match = all.FirstOrDefault(b => parsed.Kind == ContentRefKind.Guid ? b.Guid == parsed.Guid : b.Id == parsed.Id);
            if (match is not null)
            {
                return match;
            }
            if (parsed.Kind == ContentRefKind.Id || (await ContentHeaderReader.IdsByGuidsAsync(session.Db, [parsed.Guid], cancellationToken)).Count > 0)
            {
                throw new UsageException($"{text} is not a Visual Builder blueprint.", List(session, all));
            }
        }
        var named = all.Where(b => b.Name.Equals(text, StringComparison.OrdinalIgnoreCase)).GroupBy(b => b.Id).Select(g => g.First()).ToList();
        return named.Count switch
        {
            1 => named[0],
            0 => throw new NotFoundException($"No blueprint '{text}'.",
                (Suggestions.DidYouMean(text, all.Select(b => b.Name).Distinct()) is { } suggestion ? suggestion + " " : "") + List(session, all)),
            _ => throw new UsageException($"{named.Count} blueprints are named '{text}': {string.Join(", ", named.Select(b => b.Id))}.", "Give its id or GUID."),
        };
    }

    private static string List(ContentSession session, IReadOnlyList<BlueprintInfo> all) => all.Count == 0
        ? "The site has no blueprints (they are made in the Visual Builder)."
        : $"Blueprints: {string.Join(", ", all.GroupBy(b => b.Id).Select(g => $"{g.Key} \"{g.First().Name}\" ({session.Model.TypeName(g.First().TypeId)})"))}.";
}
