using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Sync;

namespace OptiCli.Core.Tests.Sync;

/// <summary>
/// The source database's content model against the target's: types by GUID then name, properties by name, never by id
/// (ids differ even between copies), and a property the target's code no longer has (ExistsOnModel = 0) isn't in its build.
/// </summary>
public class ModelCompatibilityTests
{
    private static readonly Guid Article = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Teaser = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Hero = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Other = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static ContentTypeInfo Type(int id, Guid guid, string name, ContentKind kind = ContentKind.Page) =>
        new(id, guid, name, null, null, kind, kind == ContentKind.Page ? "Page" : "Block", null, null);

    private static CmsModel Model(IReadOnlyList<ContentTypeInfo> types, IReadOnlyList<PropertyDefinition> properties) =>
        new(types, properties, [], [], null, null);

    /// <summary>The source: ids in the 100s; an article page, a teaser and a hero block, and a news page the target lacks.</summary>
    private static CmsModel Source() => Model(
        [Type(101, Article, "ArticlePage"), Type(102, Teaser, "TeaserBlock", ContentKind.Block), Type(103, Hero, "HeroBlock", ContentKind.Block), Type(104, Other, "NewsPage")],
        [
            new(1001, 101, "Heading", "LongString", PropertyBaseType.LongString, null, true, false),
            new(1002, 101, "Body", "XhtmlString", PropertyBaseType.LongString, null, true, false),
            new(1003, 101, "Related", "PageReference", PropertyBaseType.PageReference, null, false, false),
            new(1004, 101, "Hero", "HeroBlock", PropertyBaseType.Block, 103, false, false),
            new(1005, 101, "NewInSource", "LongString", PropertyBaseType.LongString, null, false, false),
            new(1006, 101, "Removed", "LongString", PropertyBaseType.LongString, null, false, false),
            new(1007, 101, "Retyped", "Number", PropertyBaseType.Number, null, false, false),
            new(1008, 101, "Tags", "String", PropertyBaseType.String, null, false, true),
            new(1009, 101, "AdminOnly", "LongString", PropertyBaseType.LongString, null, false, false),
        ]);

    /// <summary>The target: other ids, the teaser renamed (same GUID), the article matched by name with another GUID.</summary>
    private static CmsModel Target() => Model(
        [Type(7, Guid.NewGuid(), "ArticlePage"), Type(8, Teaser, "PromoBlock", ContentKind.Block), Type(9, Hero, "Hero", ContentKind.Block)],
        [
            new(71, 7, "heading", "LongString", PropertyBaseType.LongString, null, true, false),
            new(72, 7, "Body", "XhtmlString", PropertyBaseType.LongString, null, true, false),
            // CMS 13 stores a page reference as a content reference, and names a block property's type after the block.
            new(73, 7, "Related", "ContentReference", PropertyBaseType.ContentReference, null, false, false),
            new(74, 7, "Hero", "Hero", PropertyBaseType.Block, 9, false, false),
            new(76, 7, "Retyped", "LongString", PropertyBaseType.LongString, null, false, false),
            new(77, 7, "Tags", "String", PropertyBaseType.String, null, false, false),
            new(78, 7, "AdminOnly", "LongString", PropertyBaseType.LongString, null, false, false),
        ]);

    [Fact]
    public void Types_match_by_guid_then_name_and_a_type_the_target_lacks_is_missing()
    {
        var compared = ModelCompatibility.Compare(Source(), new HashSet<int>(), Target(), new HashSet<int>());

        Assert.Equal("PromoBlock", compared.TargetType(102)!.Name);
        Assert.Equal(7, compared.TargetType(101)!.Id);
        Assert.Null(compared.TargetType(104));
        Assert.Contains("not in the target's build", compared.MissingType(104), StringComparison.Ordinal);
        Assert.Null(compared.MissingType(101));
    }

    [Fact]
    public void Properties_the_target_lacks_or_has_as_another_type_are_dropped_by_name_case_aside()
    {
        var compared = ModelCompatibility.Compare(Source(), new HashSet<int> { 1006 }, Target(), new HashSet<int> { 78 });

        var dropped = compared.DroppedProperties(101);
        // Removed is the source's own orphan (an export leaves it out anyway); Heading matches heading.
        Assert.Equal(["AdminOnly", "NewInSource", "Retyped", "Tags"], dropped.Keys.Order());
        Assert.Contains("only in its database", dropped["AdminOnly"], StringComparison.Ordinal);
        Assert.Equal("Number in the source, LongString in the target", dropped["Retyped"]);
        Assert.Equal("String[] in the source, String in the target", dropped["Tags"]);
        Assert.Empty(compared.DroppedProperties(102));
    }

    [Fact]
    public void When_the_sources_build_writes_a_definition_the_targets_code_marks_removed_is_present_but_types_still_count()
    {
        // As a sealed run compares (CompareAsync with sourceBuildWrites): the target's ExistsOnModel = 0 isn't asked.
        var compared = ModelCompatibility.Compare(Source(), new HashSet<int> { 1006 }, Target(), new HashSet<int>());

        var dropped = compared.DroppedProperties(101);
        Assert.DoesNotContain("AdminOnly", dropped.Keys);
        Assert.Equal(["NewInSource", "Retyped", "Tags"], dropped.Keys.Order());
    }

    [Fact]
    public void The_differences_of_the_types_synced_list_missing_types_first()
    {
        var source = Source();
        var compared = ModelCompatibility.Compare(source, new HashSet<int>(), Target(), new HashSet<int>());

        var differences = compared.Differences(source, [101, 104, 102]);

        Assert.Equal(new ModelDifference("NewsPage", null, compared.MissingType(104)!), differences[0]);
        Assert.All(differences.Skip(1), d => Assert.Equal("ArticlePage", d.Type));
        Assert.Empty(compared.Differences(source, [102]));
    }

    [Fact]
    public void A_page_type_isnt_a_block_type_of_the_same_guid()
    {
        var target = Model([Type(7, Article, "ArticlePage", ContentKind.Block)], []);

        var compared = ModelCompatibility.Compare(Source(), new HashSet<int>(), target, new HashSet<int>());

        Assert.Equal("a page type in the source, a block type in the target", compared.MissingType(101));
    }
}
