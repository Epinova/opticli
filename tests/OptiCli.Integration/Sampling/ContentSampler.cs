using OptiCli.Core.Content;
using OptiCli.Core.Queries;

namespace OptiCli.Integration.Sampling;

/// <summary>Reads the candidates (every language branch of every item) and the most recent drafts.</summary>
internal static class ContentSampler
{
    private const string BranchesSql = """
        SELECT c.pkID, c.fkContentTypeID, cl.fkLanguageBranchID
        FROM tblContent c
        JOIN tblContentLanguage cl ON cl.fkContentID = c.pkID
        """;

    public static async Task<IReadOnlyList<SampleItem>> BranchesAsync(ContentSession session, int count, int seed, CancellationToken cancellationToken)
    {
        var candidates = await session.Db.QueryAsync(BranchesSql, r =>
        {
            var typeId = r.GetInt32(1);
            return new SampleItem(r.GetInt32(0), typeId, session.Model.Kind(typeId), r.GetInt32(2));
        }, cancellationToken);
        return Stratified.Take(candidates, count, seed);
    }

    /// <summary>The newest unpublished version of the most recently changed branches, as <c>drafts</c> lists them.</summary>
    public static async Task<IReadOnlyList<SampleItem>> RecentDraftsAsync(ContentSession session, int count, CancellationToken cancellationToken)
    {
        var drafts = await new DraftReader(session).ListAsync(null, null, null, null, 0, count, cancellationToken);
        var items = new List<SampleItem>();
        foreach (var draft in drafts.Take(count))
        {
            var parts = draft.Version.Split('_');
            var id = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var header = await session.HeaderAsync(id, cancellationToken);
            var language = draft.Language is { } code ? session.Model.LanguageByCode(code) : null;
            items.Add(new SampleItem(
                id,
                header.TypeId,
                session.Model.Kind(header.TypeId),
                language?.Id ?? header.MasterLanguageId,
                int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
        }
        return items;
    }
}
