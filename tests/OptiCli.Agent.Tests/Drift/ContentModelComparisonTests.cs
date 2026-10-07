using EPiServer.DataAbstraction;
using OptiCli.Agent.Compat;
using OptiCli.Agent.Drift;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Drift;

/// <summary>The drift items the CMS's analysis of fixture models against fixture database rows turns into.</summary>
public class ContentModelComparisonTests
{
    private static readonly Guid ArticleGuid = new("a1b2c3d4-0000-4000-8000-000000000123");

    private static TypeSettings Article(string name = "ArticlePage") => new(name, "Site.Models.ArticlePage, Site", "Page", ArticleGuid);

    private static PropertySettings Heading(string type = "String", int typeId = 3, bool cultureSpecific = true) => new("Heading", type, typeId, cultureSpecific);

    private static PropertyMatch Same(PropertySettings property) => new(property, property);

    [Fact]
    public void A_type_and_a_property_only_in_the_code_are_ahead_locally()
    {
        var matches = new[]
        {
            new TypeMatch(new TypeSettings("NewsPage"), null, [], []),
            new TypeMatch(Article(), Article(), [Same(Heading()), new PropertyMatch(new PropertySettings("Subtitle", "String", 3), null)], []),
        };

        var (types, properties) = ContentModelComparison.Items(matches, []);

        Assert.Equal([new DriftItem("NewsPage", DriftAhead.Local, ContentModelComparison.OnlyInCode)], types);
        Assert.Equal([new DriftItem("ArticlePage.Subtitle", DriftAhead.Local, ContentModelComparison.OnlyInCode)], properties);
    }

    [Fact]
    public void A_type_and_a_property_only_in_the_database_are_ahead_there()
    {
        var matches = new[] { new TypeMatch(Article(), Article(), [Same(Heading())], ["Teaser"]) };

        var (types, properties) = ContentModelComparison.Items(matches, ["EventPage"]);

        Assert.Equal([new DriftItem("EventPage", DriftAhead.Database, ContentModelComparison.OnlyInDatabase)], types);
        Assert.Equal([new DriftItem("ArticlePage.Teaser", DriftAhead.Database, ContentModelComparison.OnlyInDatabase)], properties);
    }

    [Fact]
    public void A_type_of_unknown_origin_only_in_the_database_is_listed_as_such_without_saying_which_side_is_ahead()
    {
        var (types, _) = ContentModelComparison.Items([], ["EventPage"], ["AdminOrImportedPage"]);

        Assert.Equal(
            [
                new DriftItem("EventPage", DriftAhead.Database, ContentModelComparison.OnlyInDatabase),
                new DriftItem("AdminOrImportedPage", DriftAhead.Unknown, $"{ContentModelComparison.OnlyInDatabase}, {OrphanRemoval.UnknownOrigin}"),
            ],
            types);
    }

    [Fact]
    public void Everything_the_same_is_no_drift()
    {
        var (types, properties) = ContentModelComparison.Items([new TypeMatch(Article(), Article(), [Same(Heading())], [])], []);

        Assert.Empty(types);
        Assert.Empty(properties);
    }

    [Fact]
    public void A_changed_property_says_what_changed_and_no_direction()
    {
        var (_, properties) = ContentModelComparison.Items(
            [new TypeMatch(Article(), Article(), [new PropertyMatch(Heading("XhtmlString", 14), Heading("String", 3, cultureSpecific: false))], [])], []);

        Assert.Equal([new DriftItem("ArticlePage.Heading", DriftAhead.Unknown,
            "type: XhtmlString in the code, String in the database; culture-specific in the code, not in the database")], properties);
    }

    [Fact]
    public void Block_properties_of_another_block_type_differ_by_definition_type()
    {
        var code = new PropertySettings("Teaser", "TeaserBlock", 21);
        var stored = new PropertySettings("Teaser", "TeaserBlock", 22);

        Assert.Equal(["type: TeaserBlock in the code, TeaserBlock in the database"], ContentModelComparison.Of(code, stored));
    }

    [Fact]
    public void A_type_the_cms_cant_resolve_says_so()
    {
        var code = new PropertySettings("Teaser", Type: null, TypeId: null);

        Assert.Equal(["type: the CMS can't resolve the property's type in this build (its class may have moved, been renamed or be missing), TeaserBlock in the database"],
            ContentModelComparison.Of(code, new PropertySettings("Teaser", "TeaserBlock", 21)));
    }

    [Fact]
    public void A_generic_model_type_matches_whatever_its_versions()
    {
        var local = typeof(List<ContentModelComparisonTests>).AssemblyQualifiedName!.Replace("Version=", "Version=9", StringComparison.Ordinal);

        Assert.Equal(ContentModelScan.WithoutVersion(typeof(List<ContentModelComparisonTests>).AssemblyQualifiedName), ContentModelScan.WithoutVersion(local));
        Assert.DoesNotContain("Version=", ContentModelScan.WithoutVersion(local), StringComparison.Ordinal);
    }

    [Fact]
    public void Culture_specific_set_in_admin_mode_is_not_drift()
    {
        var stored = Heading(cultureSpecific: false) with { CultureSpecificByAdmin = true };

        Assert.Empty(ContentModelComparison.Of(Heading(), stored));
        Assert.Equal(["culture-specific in the code, not in the database"], ContentModelComparison.Of(Heading(), Heading(cultureSpecific: false)));
    }

    [Fact]
    public void A_moved_class_or_another_guid_is_a_changed_type()
    {
        var stored = Article() with { ModelType = "Site.Pages.ArticlePage, Site", Guid = Guid.Empty };

        var (types, _) = ContentModelComparison.Items([new TypeMatch(Article(), stored, [], [])], []);

        Assert.Equal([new DriftItem("ArticlePage", DriftAhead.Unknown,
            $"class: Site.Models.ArticlePage, Site in the code, Site.Pages.ArticlePage, Site in the database; GUID: {ArticleGuid:D} in the code, {Guid.Empty:D} in the database")], types);
    }

    [Fact]
    public void Pending_migration_step_renames_are_ahead_locally()
    {
        var renamedType = new TypeMatch(Article(), Article("NewsArticlePage"), [], [], RenamedFrom: "NewsArticlePage");
        var renamedProperty = new TypeMatch(Article("EventPage"), Article("EventPage"),
            [new PropertyMatch(new PropertySettings("Title", "String", 3), new PropertySettings("Heading", "String", 3), RenamedFrom: "Heading")], []);

        var (types, properties) = ContentModelComparison.Items([renamedType, renamedProperty], []);

        Assert.Equal([new DriftItem("ArticlePage", DriftAhead.Local, "still named NewsArticlePage in the database (a migration step renames it)")], types);
        Assert.Equal([new DriftItem("EventPage.Title", DriftAhead.Local, "still named Heading in the database (a migration step renames it)")], properties);
    }

    [Fact]
    public void A_type_synced_from_a_newer_assembly_version_is_ahead_in_the_database()
    {
        var (types, properties) = ContentModelComparison.Items([new TypeMatch(Article(), Article(), [], [], NewerVersion: "2.1")], []);

        var item = Assert.Single(types);
        Assert.Equal(DriftAhead.Database, item.Ahead);
        Assert.StartsWith("synced from version 2.1 of its assembly", item.Difference, StringComparison.Ordinal);
        Assert.Empty(properties);
    }

    [Theory]
    [InlineData("Site.Models.ArticlePage, Site, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null", "Site.Models.ArticlePage, Site")]
    [InlineData("EPiServer.Core.ContentFolder,EPiServer", "EPiServer.Core.ContentFolder, EPiServer")]
    [InlineData("Site.Models.ListPage`1[[Site.Models.ArticlePage, Site, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null]], Site, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null",
        "Site.Models.ListPage`1[[Site.Models.ArticlePage, Site]], Site")]
    [InlineData("Site.Models.ListPage`1[[Site.Models.ArticlePage,Site]],Site", "Site.Models.ListPage`1[[Site.Models.ArticlePage, Site]], Site")]
    [InlineData(null, null)]
    public void Model_types_are_compared_without_the_assembly_version(string? stored, string? expected) =>
        Assert.Equal(expected, ContentModelScan.WithoutVersion(stored));

    [Theory]
    [InlineData("Site.Models.ArticlePage, Site, Version=2.1.0.0, Culture=neutral", "2.1")]
    [InlineData("Site.Models.ArticlePage, Site", null)]
    public void The_recorded_assembly_version_is_major_and_minor(string stored, string? expected) =>
        Assert.Equal(expected, ContentModelScan.AssemblyVersion(stored));

    [Fact]
    public void Who_set_culture_specific_is_read_from_the_cms()
    {
        // LanguageSpecific's setter records the change as made outside the model, as admin mode does.
        var byAdmin = new PropertyDefinition { LanguageSpecific = true };

        Assert.True(ContentModelScan.CultureSpecificByAdmin(byAdmin));
        Assert.False(ContentModelScan.CultureSpecificByAdmin(new PropertyDefinition()));
    }
}

/// <summary>How a stored content type shows it came from code, which differs between CMS 12 and 13.</summary>
public class StoredTypeOriginTests
{
    [Fact]
    public void A_type_with_a_class_on_record_came_from_code_and_one_made_in_admin_mode_didnt()
    {
        Assert.True(AgentBuild.FromCode(new ContentType { Name = "ArticlePage", ModelTypeString = "Site.Models.ArticlePage, Site" }));
        Assert.False(AgentBuild.FromCode(new ContentType { Name = "AdminPage" }));
        Assert.Equal("1.2", AgentBuild.SyncedVersion(new ContentType { ModelTypeString = "Site.Models.ArticlePage, Site, Version=1.2.3.4, Culture=neutral, PublicKeyToken=null" }));
    }

#if CMS13
    [Fact]
    public void On_cms_13_a_type_synced_from_a_class_with_a_guid_has_only_the_assembly_version_on_record()
    {
        // CMS 13 stores no class for a model with a GUID, only the version of its assembly; admin mode sets none.
        var synced = new ContentType { Name = "ArticlePage", Version = new Version(1, 0, 0, 0) };

        Assert.True(AgentBuild.FromCode(synced));
        Assert.Equal("1.0", AgentBuild.SyncedVersion(synced));
        // A type of an external content source isn't the site's code.
        Assert.False(AgentBuild.FromCode(new ContentType { Name = "Product", Version = new Version(1, 0), Source = "commerce" }));
    }
#endif
}
