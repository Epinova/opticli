using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Seals;

namespace OptiCli.Core.Tests.Seals;

/// <summary>
/// What a seal records of production and what the run compares: every touched item's place and each branch's newest
/// version with its saved time (a restored database reuses version ids) and published version, and the model's fingerprint.
/// </summary>
public class ProductionStateTests
{
    private static readonly Guid Other = Guid.Parse("42cfb67a-6c8c-4253-8ff0-366d442a30cc");

    [Fact]
    public void The_same_state_has_no_changes()
    {
        Assert.Empty(ProductionState.Changes(SealFixtures.State(), SealFixtures.State()));
    }

    [Theory]
    [InlineData(460, null, 456, "newer version")]
    [InlineData(456, "2026-10-16T09:00:00", 456, "newer version")]
    [InlineData(456, null, null, "was unpublished")]
    public void A_new_version_or_publish_state_is_a_change(int latest, string? saved, int? published, string expected)
    {
        var changes = ProductionState.Changes(SealFixtures.State(), SealFixtures.State(latest, saved is null ? null : DateTime.Parse(saved, System.Globalization.CultureInfo.InvariantCulture), published));

        Assert.Contains(changes, c => c.Contains(expected, StringComparison.Ordinal) && c.Contains("About us", StringComparison.Ordinal));
    }

    [Fact]
    public void A_move_a_trash_a_new_branch_and_another_model_are_changes()
    {
        var before = SealFixtures.State();
        var item = before.Items.Single();

        Assert.Contains(ProductionState.Changes(before, SealFixtures.State(parent: Other)), c => c.Contains("was moved", StringComparison.Ordinal));
        Assert.Contains(ProductionState.Changes(before, before with { Items = [item with { Deleted = true }] }), c => c.Contains("recycle bin", StringComparison.Ordinal));
        Assert.Contains(ProductionState.Changes(before, before with { Items = [item with { Branches = [.. item.Branches, new SealedBranch("sv", 900, null, null)] }] }), c => c.Contains("new sv branch", StringComparison.Ordinal));
        Assert.Contains(ProductionState.Changes(before, before with { Items = [item with { Branches = [] }] }), c => c.Contains("branch is gone", StringComparison.Ordinal));
        Assert.Contains(ProductionState.Changes(before, SealFixtures.State(model: "m2")), c => c.Contains("content model changed", StringComparison.Ordinal));
    }

    [Fact]
    public void Content_that_appeared_or_went_for_good_is_a_change()
    {
        var absent = new ProductionState("m1", [new SealedItem(Other, false, false, null, null, null, [])]);
        var present = new ProductionState("m1", [new SealedItem(Other, true, false, SealFixtures.Start, "StandardPage", "Search", [new SealedBranch("en", 7, null, 7)])]);

        Assert.Contains(ProductionState.Changes(absent, present), c => c.Contains("was created since", StringComparison.Ordinal));
        Assert.Contains(ProductionState.Changes(present, absent), c => c.Contains("deleted for good", StringComparison.Ordinal));
        Assert.Contains(ProductionState.Changes(present, new ProductionState("m1", [])), c => c.Contains("wasn't read again", StringComparison.Ordinal));
    }

    [Fact]
    public void The_model_fingerprint_leaves_out_ids_and_removed_properties_and_sees_types_and_property_kinds()
    {
        var guid = Guid.NewGuid();
        CmsModel Model(int typeId, int propertyId, string typeName = "LongString", bool culture = true) => new(
            [new ContentTypeInfo(typeId, guid, "ArticlePage", null, null, ContentKind.Page, "Page", null, null)],
            [new PropertyDefinition(propertyId, typeId, "Heading", typeName, PropertyBaseType.LongString, null, culture, false),
             new PropertyDefinition(propertyId + 1, typeId, "Gone", "LongString", PropertyBaseType.LongString, null, false, false)],
            [], [], null, null);
        var removed = new HashSet<int> { 11, 101 };

        var fingerprint = ProductionState.ModelFingerprint(Model(1, 10), removed);

        Assert.Equal(fingerprint, ProductionState.ModelFingerprint(Model(5, 100), removed));
        Assert.NotEqual(fingerprint, ProductionState.ModelFingerprint(Model(1, 10), new HashSet<int>()));
        Assert.NotEqual(fingerprint, ProductionState.ModelFingerprint(Model(1, 10, "XhtmlString"), removed));
        Assert.NotEqual(fingerprint, ProductionState.ModelFingerprint(Model(1, 10, culture: false), removed));
    }
}
