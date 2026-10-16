using System.Text.Json.Nodes;
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

    private static readonly Guid Image = Guid.Parse("d657fc6e-1c8a-400e-8117-7aa063f001a6");
    private static readonly Guid Folder = Guid.Parse("0e2b5a3c-7d61-4f0e-9a43-5be1c1e6f7a2");
    private const string File = "epi.fx.blob://default/421f8f60ecdb4ab7a01f0c5454bdd521/0b8e1c2d3f4a5b6c7d8e9f0a1b2c3d4e.png";

    private static ExportCandidate MediaThere(Guid? parent = null) =>
        new(new ContentHeader(63, Image, 1, 62, ".1.62.", 1, false, 0, 0, new Dictionary<int, ContentLanguageRow>()),
            ContentKind.Media, "ImageFile", parent ?? Folder, null, 1, [new CandidateBranch(1, null, 64, true)]);

    private static ExportItem MediaItem(string description = "The meeting screen") =>
        new(63, Image, ContentKind.Media, "ImageFile", Folder, null, null,
            [new ExportBranch(null, 64, true, new JsonObject
            {
                ["ref"] = "63", ["guid"] = Image.ToString("D"), ["name"] = "AlloyMeetScreen.png", ["status"] = "published",
                ["properties"] = new JsonObject { ["Description"] = new JsonObject { ["type"] = "LongString", ["value"] = description } },
            })],
            new HashSet<string>(), 1);

    [Fact]
    public void Media_production_has_as_it_is_locally_is_not_listed_as_left_out()
    {
        Assert.Null(SyncPush.MediaLeftOut(MediaThere(), MediaItem(), File, MediaThere(), MediaItem(), File));
    }

    [Fact]
    public void Media_production_lacks_or_has_otherwise_is_listed_as_left_out()
    {
        Assert.Contains("doesn't have", SyncPush.MediaLeftOut(MediaThere(), MediaItem(), File, null, null, null), StringComparison.Ordinal);
        // Another stored file (it may hold other bytes; production's blobs aren't read), other values, another place.
        Assert.Contains("has otherwise", SyncPush.MediaLeftOut(MediaThere(), MediaItem(), File, MediaThere(), MediaItem(), File.Replace(".png", "-2.png", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("has otherwise", SyncPush.MediaLeftOut(MediaThere(), MediaItem(), null, MediaThere(), MediaItem(), null), StringComparison.Ordinal);
        Assert.Contains("has otherwise", SyncPush.MediaLeftOut(MediaThere(), MediaItem("A new caption"), File, MediaThere(), MediaItem(), File), StringComparison.Ordinal);
        Assert.Contains("has otherwise", SyncPush.MediaLeftOut(MediaThere(), MediaItem(), File, MediaThere(parent: Start), MediaItem(), File), StringComparison.Ordinal);
    }
}
