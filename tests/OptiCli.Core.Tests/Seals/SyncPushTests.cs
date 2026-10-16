using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Export;
using OptiCli.Core.Sync;

namespace OptiCli.Core.Tests.Seals;

/// <summary>
/// A push writes over production's content only where the last pull read it as production has it now: content production
/// changed (or moved) since, or that was never pulled from it, is a conflict.
/// </summary>
public class SyncPushTests
{
    private static readonly Guid Item = Guid.Parse("8d07bab1-0fce-56de-b107-7d6a72432ba2");
    private static readonly Guid Start = Guid.Parse("7b08c7d5-7585-47e5-add7-5822da68c3ce");

    private static ExportCandidate There(int version = 156, bool published = true, Guid? parent = null) =>
        new(new ContentHeader(128, Item, 1, 5, ".1.5.", 1, false, 0, 0, new Dictionary<int, ContentLanguageRow>()),
            ContentKind.Page, "StandardPage", parent ?? Start, null, 1, [new CandidateBranch(1, "en", version, published)]);

    private static SyncedItem Pulled(int version = 156, bool published = true) =>
        new(Start.ToString("D"), "irrelevant", new Dictionary<string, SyncedBranch> { ["EN"] = new(version, published, 9000) });

    [Fact]
    public void Content_production_still_has_as_the_last_pull_read_it_can_be_written()
    {
        Assert.Null(SyncPush.ProductionChanged(There(), Pulled()));
    }

    [Fact]
    public void A_change_in_production_since_the_pull_or_no_pull_at_all_is_a_conflict()
    {
        Assert.Contains("never pulled", SyncPush.ProductionChanged(There(), null), StringComparison.Ordinal);
        Assert.Contains("en branch changed", SyncPush.ProductionChanged(There(version: 160), Pulled()), StringComparison.Ordinal);
        Assert.Contains("en branch changed", SyncPush.ProductionChanged(There(published: false), Pulled()), StringComparison.Ordinal);
        Assert.Contains("moved in production", SyncPush.ProductionChanged(There(parent: Guid.NewGuid()), Pulled()), StringComparison.Ordinal);
        Assert.Contains("en branch changed", SyncPush.ProductionChanged(There(), new SyncedItem(Start.ToString("D"), null, new Dictionary<string, SyncedBranch>())), StringComparison.Ordinal);
    }
}
