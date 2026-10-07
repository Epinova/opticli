using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Data;
using OptiCli.Core.Discovery;
using OptiCli.Core.Drift;
using OptiCli.Core.Errors;
using OptiCli.Core.Jobs;
using OptiCli.Core.Properties;
using OptiCli.Core.Queries;
using OptiCli.Core.SourceScan;
using OptiCli.Core.Urls;
using OptiCli.Protocol;
using static OptiCli.Core.Tests.Content.ModelFixture;

namespace OptiCli.Core.Tests.Cms;

/// <summary>
/// What reads do differently on a CMS 13 schema (<see cref="CmsSchema"/>), next to what they keep doing on CMS 12. The
/// rows are shaped like the CMS 13.3 test databases' (a new install and one upgraded from CMS 12).
/// </summary>
public class Cms13SchemaTests
{
    [Theory]
    [InlineData(8023, 12)]
    [InlineData(20999, 12)]
    [InlineData(21000, 13)]
    [InlineData(21005, 13)]
    public void The_schema_version_says_the_major(int version, int major)
    {
        Assert.Equal(major, CmsSchema.MajorOf(version));
        Assert.Equal(major, (CmsSchema.Cms12 with { Version = version }).Major);
    }

    [Fact]
    public void Without_a_version_the_tables_say_the_major()
    {
        Assert.Equal(12, CmsSchema.Cms12.Major);
        Assert.Equal(13, (CmsSchema.Cms13 with { Version = null }).Major);
    }

    [Fact]
    public void Block_property_types_come_from_the_property_type_on_12_and_the_definition_on_13()
    {
        var cms12 = CmsModel.PropertiesSql(CmsSchema.Cms12);
        var cms13 = CmsModel.PropertiesSql(CmsSchema.Cms13);

        Assert.Contains("bt.ContentTypeGUID = pdt.fkContentTypeGUID", cms12, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemTypeID", cms12, StringComparison.Ordinal);
        Assert.Contains("pdt.Name AS TypeName", cms12, StringComparison.Ordinal);

        // The generic Block type is matched by its Property value, never its row id (12 on a new database, 38 upgraded).
        Assert.Contains("pdt.Property = 12 AND bt.ContentTypeGUID = pd.ItemTypeID", cms13, StringComparison.Ordinal);
        Assert.DoesNotContain("fkContentTypeGUID", cms13, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN bt.pkID IS NOT NULL THEN bt.Name ELSE pdt.Name END AS TypeName", cms13, StringComparison.Ordinal);

        Assert.Contains("pdt.fkContentTypeGUID", ContentTypeReader.PropertiesSql(CmsSchema.Cms12), StringComparison.Ordinal);
        Assert.Contains("pd.ItemTypeID", ContentTypeReader.PropertiesSql(CmsSchema.Cms13), StringComparison.Ordinal);
        Assert.DoesNotContain("fkContentTypeGUID", ContentTypeReader.PropertiesSql(CmsSchema.Cms13), StringComparison.Ordinal);
    }

    [Fact]
    public void Variation_versions_are_left_out_only_where_the_schema_has_them()
    {
        Assert.Equal("", CmsSchema.Cms12.DefaultVariationOnly("wc"));
        Assert.Equal(" AND wc.fkVariationID IS NULL", CmsSchema.Cms13.DefaultVariationOnly("wc"));
        Assert.DoesNotContain("fkVariationID", ContentHeaderReader.CommonDraftApply(CmsSchema.Cms12), StringComparison.Ordinal);
        Assert.Contains("d.fkVariationID IS NULL", ContentHeaderReader.CommonDraftApply(CmsSchema.Cms13), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "Experience", null, false, ContentKind.Experience)]
    [InlineData(2, "Section", "SectionEnabled", false, ContentKind.Section)]
    [InlineData(2, null, null, true, ContentKind.Contract)]
    [InlineData(2, null, null, false, ContentKind.Other)]
    [InlineData(1, "Block", null, false, ContentKind.Block)]
    [InlineData(1, "Block", "ElementEnabled", false, ContentKind.Element)]
    [InlineData(1, "Block", "SectionEnabled, ElementEnabled", false, ContentKind.Element)]
    [InlineData(1, "Block", "SectionEnabled", false, ContentKind.Block)]
    [InlineData(1, null, "ElementEnabled", false, ContentKind.Element)]
    public void Visual_builder_types_have_the_kind_of_what_they_are(int contentType, string? typeBase, string? behaviors, bool contract, ContentKind kind) =>
        Assert.Equal(kind, ContentKinds.From(contentType, typeBase, ContentKinds.Behaviors(behaviors), contract));

    [Fact]
    public void Experiences_count_as_pages_and_sections_and_elements_as_blocks()
    {
        Assert.True(ContentKind.Experience.IsPage());
        Assert.True(ContentKind.Section.IsBlock());
        Assert.True(ContentKind.Element.IsBlock());
        Assert.False(ContentKind.Contract.IsPage() || ContentKind.Contract.IsBlock());
        Assert.Equal(["Page", "Block", "Media", "Folder", "Other"], Enum.GetNames<ContentKind>().Take(5));
    }

    private static SiteReader.ApplicationRow Application(int id, string name, string display, bool isDefault = false, string? entry = "6", int? assets = 3) =>
        new(id, name, display, ApplicationTypes.InProcessWebsite, entry, assets, isDefault, "Start", "en");

    [Fact]
    public void A_new_install_has_one_default_application_with_the_first_requests_host()
    {
        var sites = SiteReader.FromApplications(
            [Application(1, "alloy13", "alloy13", isDefault: true)],
            [new SiteReader.ApplicationHostRow(1, "localhost:5130", ApplicationHostTypes.Default, null, false)]);

        var site = Assert.Single(sites);
        Assert.Equal(1, site.Id);
        Assert.Null(site.Guid);
        Assert.Equal("alloy13", site.Name);
        Assert.Equal("alloy13", site.Application);
        Assert.Equal("inProcessWebsite", site.ApplicationType);
        Assert.True(site.IsDefault);
        Assert.Equal("http://localhost:5130/", site.Url);
        Assert.Equal("6", site.StartPage);
        Assert.Equal("3", site.AssetsRoot);
        Assert.Equal([new HostInfo("localhost:5130", HostType.Undefined, null, false)], site.Hosts);
        Assert.Equal("alloy13", site.Key);
    }

    [Fact]
    public void An_upgraded_site_is_known_by_its_application_name_and_shown_by_its_display_name()
    {
        var site = Assert.Single(SiteReader.FromApplications(
            [Application(1, "Site_CB857AE6_E591_41BA_B9C0_C7C3E35ED428", "Alloy", isDefault: true, entry: "5")],
            [new SiteReader.ApplicationHostRow(1, "127.0.0.1:5010", ApplicationHostTypes.Default, null, true)]));

        Assert.Equal("Alloy", site.Name);
        Assert.Equal("Site_CB857AE6_E591_41BA_B9C0_C7C3E35ED428", site.Key);
        // The upgrade makes every host without an https setting https, and so does the CMS's URL.
        Assert.Equal("https://127.0.0.1:5010/", site.Url);
    }

    [Fact]
    public void Host_types_map_to_cms_12s_and_the_url_is_the_first_primary_host()
    {
        var site = Assert.Single(SiteReader.FromApplications(
            [Application(4, "multi", "Multi")],
            [
                new SiteReader.ApplicationHostRow(4, "old.example.com", ApplicationHostTypes.RedirectPermanent, null, true),
                new SiteReader.ApplicationHostRow(4, "www.example.com", ApplicationHostTypes.Default, null, true),
                new SiteReader.ApplicationHostRow(4, "www.example.se", ApplicationHostTypes.Primary, "sv", true),
                new SiteReader.ApplicationHostRow(4, "edit.example.com", ApplicationHostTypes.Edit, null, true),
                new SiteReader.ApplicationHostRow(4, "preview.example.com", ApplicationHostTypes.Preview, null, true),
                new SiteReader.ApplicationHostRow(4, "media.example.com", ApplicationHostTypes.Media, null, true),
                new SiteReader.ApplicationHostRow(4, "temp.example.com", ApplicationHostTypes.RedirectTemporary, null, false),
            ]));

        Assert.Equal(
            [HostType.RedirectPermanent, HostType.Undefined, HostType.Primary, HostType.Edit, HostType.Preview, HostType.Media, HostType.RedirectTemporary],
            site.Hosts.Select(h => h.Type));
        Assert.Equal("sv", site.Hosts[2].Language);
        Assert.Equal("https://www.example.se/", site.Url);
        Assert.False(site.IsDefault);
    }

    [Fact]
    public void An_application_without_a_display_name_or_hosts_still_lists()
    {
        var site = Assert.Single(SiteReader.FromApplications([Application(2, "headless", "", entry: "")], []));

        Assert.Equal("headless", site.Name);
        Assert.Null(site.Url);
        Assert.Null(site.StartPage);
        Assert.Empty(site.Hosts);
    }

    private static readonly SiteInfo Default = SiteReader.FromApplications(
        [Application(1, "main", "Main", isDefault: true, entry: "5")],
        [
            new SiteReader.ApplicationHostRow(1, "www.example.com", ApplicationHostTypes.Primary, null, true),
            new SiteReader.ApplicationHostRow(1, "www.example.se", ApplicationHostTypes.Default, "sv", true),
        ])[0];

    private static readonly SiteInfo Second = SiteReader.FromApplications(
        [Application(2, "second", "Second", entry: "7")],
        [new SiteReader.ApplicationHostRow(2, "localhost:5002", ApplicationHostTypes.Primary, null, false)])[0];

    private static SiteMap Map(params SiteInfo[] sites) => Create(sites).Sites;

    [Fact]
    public void The_default_application_answers_bare_paths_and_unknown_hosts()
    {
        var map = Map(Second, Default);

        Assert.Equal(5, map.Parse("/about/", null).RootId);
        var unknown = map.Parse("http://unknown.example/about/", null);
        Assert.Equal("Main", unknown.Site!.Name);
        Assert.Null(unknown.Host);
        Assert.Equal("site", unknown.LanguageSource);
        Assert.True(SiteMap.AnswersUnknownHosts(Default));
        Assert.False(SiteMap.AnswersUnknownHosts(Second));
    }

    [Fact]
    public void A_host_matches_with_its_port_only_and_its_locale_is_its_language()
    {
        var map = Map(Second, Default);

        Assert.Equal(7, map.Parse("http://localhost:5002/about/", null).RootId);
        // CMS 13 doesn't match the host without the port: the default application answers.
        Assert.Equal(5, map.Parse("http://localhost:5003/about/", null).RootId);
        var swedish = map.Parse("https://www.example.se/om-oss/", null);
        Assert.Equal(("sv", "host"), (swedish.Language!.Code, swedish.LanguageSource));
    }

    [Fact]
    public void Applications_are_found_by_display_name_application_name_host_or_id()
    {
        var map = Map(Second, Default);

        Assert.Same(Second, map.RequireSite("Second"));
        Assert.Same(Second, map.RequireSite("second"));
        Assert.Same(Default, map.RequireSite("main"));
        Assert.Same(Second, map.RequireSite("localhost:5002"));
        Assert.Same(Second, map.RequireSite("2"));
        Assert.Same(Default, OptiCli.Core.Sites.PrimaryPairs.RequireSite("main", [Second, Default]));
    }

    [Fact]
    public void Without_a_default_application_a_path_needs_site()
    {
        var error = Assert.Throws<UsageException>(() => Map(Second, Second with { Id = 3, Name = "Third", Application = "third", StartPage = "8" }).Parse("/about/", null));

        Assert.Contains("none is the default one", error.Message, StringComparison.Ordinal);
        Assert.Throws<NotFoundException>(() => Map(Second, Second with { Id = 3, Application = "third" }).Parse("http://unknown.example/", null));
    }

    [Fact]
    public void Page_urls_use_the_primary_host_and_its_scheme()
    {
        var map = Map(Default);
        string? Segment(int id) => id == 100 ? "about" : "home";

        Assert.Equal("https://www.example.com/en/about/", map.Compose([1, 5, 100], Segment, En, ContentKind.Page)!.Absolute);
        Assert.Equal("https://www.example.se/about/", map.Compose([1, 5, 100], Segment, Sv, ContentKind.Page)!.Absolute);
    }

    [Fact]
    public void A_variation_version_is_its_changes_over_the_published_version()
    {
        PropertyRow Row(int definition, string? scope, string value) => new(104, definition, 1, scope, LongString: value);
        var published = new[]
        {
            Row(148, null, "published summary"),
            Row(149, null, "{\"type\":\"outline\"}"),
            Row(150, null, "<div data-epi-block-id=\"a\" />"),
            Row(141, ".150:26(0).141.", "{\"type\":\"grid\"}"),
            Row(146, ".150:26(1).142:28(0).146.", "published heading"),
        };
        var patch = new[]
        {
            Row(148, null, "variation summary"),
            Row(146, ".150:26(1).142:28(0).146.", "variation heading"),
        };

        var merged = PropertyRows.Variation(patch, published).ToList();

        // Summary (148) and everything under UnstructuredData (150) come from the variation; Layout (149) from the published version.
        Assert.Equal(["variation summary", "variation heading", "{\"type\":\"outline\"}"], merged.Select(r => r.LongString));
        Assert.Equal(150, PropertyRows.TopLevelProperty(published[3]));
        Assert.Equal(148, PropertyRows.TopLevelProperty(published[0]));
    }

    [Fact]
    public void Cms_13_job_names_are_the_ones_admin_mode_shows()
    {
        var archive = Guid.Parse("63c7f148-12b1-4cdf-a2ca-8458208c6c26");
        var own = Guid.Parse("5e0b1d2c-7a3f-4b6e-9d10-3c4b5a6f7e81");
        ScheduledJobSource[] sources = [new("Site.Jobs.ImportJob", own, "Import products", null, 0, 0, false, "Jobs/ImportJob.cs", 9)];

        Assert.Equal("Archive Function", JobNames.Readable(archive, "PageArchiveJob", "EPiServer.Util.PageArchiveJob", null));
        Assert.Equal("Remove Unused Content Variations", JobNames.Readable(Guid.Parse("7f422978-49cf-4bc4-9757-105fb252a1c1"), "VariationCleanupJob", null, null));
        Assert.Equal("Import products", JobNames.Readable(own, "ImportJob", "Site.Jobs.ImportJob", sources));
        Assert.Equal("Import products", JobNames.Readable(Guid.NewGuid(), "ImportJob", "Site.Jobs.ImportJob", sources));
        Assert.Equal("OtherJob", JobNames.Readable(Guid.NewGuid(), "OtherJob", "Vendor.OtherJob", sources));
    }

    [Fact]
    public void A_job_is_found_by_its_shown_name_or_its_class()
    {
        var archive = new JobRow(Guid.Parse("63c7f148-12b1-4cdf-a2ca-8458208c6c26"), "Archive Function", true, null, null, null, null, null, 0,
            "EPiServer.Util.PageArchiveJob", "EPiServer", false, null, null, false, false, false);

        Assert.Same(archive, JobReferences.Resolve("Archive Function", [archive]));
        Assert.Same(archive, JobReferences.Resolve("PageArchiveJob", [archive]));
        Assert.Same(archive, JobReferences.Resolve("EPiServer.Util.PageArchiveJob", [archive]));
    }

    [Fact]
    public void Cms_13s_job_attribute_is_scanned_like_cms_12s()
    {
        using var root = new TempDirectory();
        root.Write("Jobs/Cleanup.cs", """
            namespace Site.Jobs;

            [ScheduledJob(GUID = "11111111-2222-3333-4444-555555555555", DisplayName = "Clean up", IntervalType = ScheduledIntervalType.Hours, IntervalLength = 2)]
            public class CleanupJob : ScheduledJobBase
            {
                public override string Execute() => "";
            }

            [EPiServer.PlugIn.ScheduledPlugIn(DisplayName = "Old style")]
            public class OldJob : ScheduledJobBase
            {
                public override string Execute() => "";
            }
            """);

        var sources = ScheduledJobSources.Find(CSharpSourceIndex.Build(root.Path));

        Assert.Equal(["Clean up", "Old style"], sources.Select(s => s.DisplayName).Order());
        Assert.Equal((5, 2), sources.Single(s => s.TypeName == "Site.Jobs.CleanupJob") is var job ? (job.IntervalType, job.IntervalLength) : default);
    }

    [Theory]
    [InlineData(21005, 21005, "13.3.0.0", null, false)]
    [InlineData(21006, 21005, "13.3.0.0", DriftAhead.Database, true)]
    [InlineData(21004, 21005, "13.3.0.0", DriftAhead.Local, true)]
    [InlineData(21005, 8023, "12.29.0.0", DriftAhead.Database, true)]
    [InlineData(8023, 21005, "13.3.0.0", DriftAhead.Local, true)]
    public void Cms_13_starts_only_against_its_own_schema_version(int database, int required, string framework, string? ahead, bool refused)
    {
        var (items, refusal, _) = StartupDriftCheck.Schema(database, required, Version.Parse(framework), []);

        Assert.Equal(ahead, items.SingleOrDefault()?.Ahead);
        Assert.Equal(refused, refusal is not null);
        Assert.False(StartupDriftCheck.AcceptsOneNewer(new Version(13, 3, 0, 0)));
        Assert.True(StartupDriftCheck.AcceptsOneNewer(new Version(12, 17, 0, 0)));
    }

    [Theory]
    [InlineData("13.3.0", 13)]
    [InlineData("12.29.0-preview1", 12)]
    [InlineData("[13.0.0, )", null)]
    [InlineData(null, null)]
    public void The_cms_major_comes_from_the_package_version(string? version, int? major) =>
        Assert.Equal(major, PackageVersions.Major(version));

    [Fact]
    public void The_restore_store_is_read_by_cms_13s_property_names_first()
    {
        var cms12 = new DynamicDataStore(RestoreParents.StoreName, "tblSystemBigTable", new Dictionary<string, string> { ["SourceLink"] = "String02", ["ParentLink"] = "String01" });
        var cms13 = new DynamicDataStore(RestoreParents.StoreName, "tblSystemBigTable", new Dictionary<string, string>
        {
            ["Source"] = "Indexed_String02", ["Parent"] = "Indexed_String01", ["SourceLink"] = "String02", ["ParentLink"] = "String01",
        });
        var neither = new DynamicDataStore(RestoreParents.StoreName, "tblSystemBigTable", new Dictionary<string, string> { ["Source"] = "String01" });

        Assert.Equal(("String02", "String01"), RestoreParents.Columns(cms12));
        Assert.Equal(("Indexed_String02", "Indexed_String01"), RestoreParents.Columns(cms13));
        Assert.Null(RestoreParents.Columns(neither));
    }
}
