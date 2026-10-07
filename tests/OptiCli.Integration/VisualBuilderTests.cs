using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Properties;
using OptiCli.Core.Queries;
using OptiCli.Core.SourceScan;
using OptiCli.Integration.Comparison;
using OptiCli.Integration.Sampling;

namespace OptiCli.Integration;

/// <summary>
/// CMS 13's Visual Builder on a site with tests/fixtures/cms13/VisualBuilderFixture.cs (the CMS 13 test sites and the CMS 13
/// edge-case site): the fixture's two experiences, their variations and blueprints, read the way the commands read them,
/// and compared with what the CMS itself loads. Each test returns early on CMS 12 and on a site without the fixture.
/// </summary>
public sealed class VisualBuilderTests
{
    private static readonly Guid Experience = Guid.Parse("7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b01");

    private static readonly Guid SharedElement = Guid.Parse("7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b02");

    private static readonly Guid SecondExperience = Guid.Parse("7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b03");

    private static readonly DecodeOptions Get = new(Composition: true);

    /// <summary>The fixture's content ids, or null where the test should stop (CMS 12, no fixture, or an older fixture).</summary>
    private static async Task<(int Experience, int Shared, int Second)?> FixtureAsync(SiteUnderTest site, CancellationToken cancellationToken)
    {
        if (!site.Session.Model.Schema.Compositions)
        {
            return null;
        }
        var ids = await ContentHeaderReader.IdsByGuidsAsync(site.Session.Db, [Experience, SharedElement, SecondExperience], cancellationToken);
        return ids.Count == 3 ? (ids[Experience], ids[SharedElement], ids[SecondExperience]) : null;
    }

    private static Task<ContentDocument> GetAsync(SiteUnderTest site, int id, VersionSelector? version = null, string? language = null, CancellationToken cancellationToken = default) =>
        new ContentLoader(site.Session.Db, site.Session.Identities).GetAsync(id, version ?? VersionSelector.Published,
            language is null ? null : site.Session.Model.RequireLanguage(language), Get, cancellationToken);

    private static IEnumerable<JsonObject> Objects(JsonNode? array) => (array as JsonArray ?? []).OfType<JsonObject>();

    private static string? Text(JsonNode? node) => node?.GetValue<string>();

    [SiteFact]
    public async Task An_experience_shows_its_composition_as_the_fixture_built_it()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is not { } fixture)
        {
            return;
        }

        var document = await GetAsync(site, fixture.Experience, cancellationToken: cancellationToken);

        Assert.Equal("experience", document.Kind);
        Assert.DoesNotContain("Layout", document.Properties.Select(p => p.Key));
        Assert.DoesNotContain("UnstructuredData", document.Properties.Select(p => p.Key));
        var composition = document.Composition!;
        Assert.Equal("outline", Text(composition["layout"]));
        var sections = Objects(composition["sections"]).ToList();
        Assert.Equal(["Hero", "Body"], sections.Select(s => Text(s["name"])));
        var hero = sections[0];
        Assert.Equal(("VbSection", "vbSection", "dark", "true"),
            (Text(hero["type"]), Text(hero["displayTemplate"]), Text(hero["displaySettings"]!["background"]), Text(hero["displaySettings"]!["fullWidth"])));
        var intro = Objects(Objects(Objects(hero["rows"]).Single()["columns"]).Single()["elements"]).Single();
        Assert.Equal(("Intro", "VbTextElement", "vbElement", "accent", "Welcome"),
            (Text(intro["name"]), Text(intro["type"]), Text(intro["displayTemplate"]), Text(intro["displaySettings"]!["color"]), Text(intro["properties"]!["Heading"]!["value"])));
        var columns = Objects(Objects(sections[1]["rows"]).Single()["columns"]).ToList();
        Assert.Equal(["Left", "Right"], columns.Select(c => Text(c["name"])));
        var right = Objects(columns[1]["elements"]).ToList();
        Assert.Equal(["Second text", "Shared"], right.Select(e => Text(e["name"])));
        Assert.True(right[0]["inline"]!.GetValue<bool>());
        Assert.Equal((fixture.Shared.ToString(System.Globalization.CultureInfo.InvariantCulture), "element"), (Text(right[1]["content"]!["ref"]), Text(right[1]["content"]!["kind"])));
        var link = Objects(columns[0]["elements"]).Single()["properties"]!;
        Assert.Equal(("ContentReference", "Url"), (Text(link["Target"]!["type"]), Text(link["Link"]!["type"])));
    }

    [SiteFact]
    public async Task A_second_experience_has_a_banner_a_styled_row_of_cards_and_a_swedish_branch()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is not { } fixture)
        {
            return;
        }

        var english = (await GetAsync(site, fixture.Second, cancellationToken: cancellationToken)).Composition!;
        var swedish = await GetAsync(site, fixture.Second, language: "sv", cancellationToken: cancellationToken);

        var sections = Objects(english["sections"]).ToList();
        // A section-enabled block stands in the outline as a section without rows of its own.
        Assert.Equal(("Banner", "VbBanner", "A banner as a whole section"), (Text(sections[0]["name"]), Text(sections[0]["type"]), Text(sections[0]["properties"]!["Title"]!["value"])));
        Assert.False(sections[0].ContainsKey("rows"));
        var row = Objects(sections[1]["rows"]).Single();
        Assert.Equal(("vbRow", "wide"), (Text(row["displayTemplate"]), Text(row["displaySettings"]!["gap"])));
        var card = Objects(Objects(row["columns"]).First()["elements"]).Single()["properties"]!.AsObject();
        Assert.Equal(["Featured", "Heading", "Image", "Links", "Priority", "Published", "Teaser"], card.Select(p => p.Key));
        Assert.Equal(("ImageFile", "Start"), (Text(card["Image"]!["value"]!["type"]), Text(card["Links"]!["value"]![0]!["content"]!["name"])));
        Assert.Equal("2026-05-01T08:30:00Z", Text(card["Published"]!["value"]));
        Assert.Single(Objects(card["Teaser"]!["links"]));
        Assert.Equal(fixture.Shared.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Text(Objects(Objects(row["columns"]).Last()["elements"]).Single()["content"]!["ref"]));

        Assert.Equal(("sv", "sv"), (swedish.Language, Text(swedish.Composition!["culture"])));
        Assert.Equal("Svensk", Text(Objects(swedish.Composition["sections"]).Single()["name"]));
    }

    [SiteFact]
    public async Task Variations_are_marked_listed_as_drafts_of_their_own_and_picked_by_key()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is not { } fixture)
        {
            return;
        }
        var model = site.Session.Model;

        var versions = await VersionReader.ListAsync(site.Session.Db, model, fixture.Experience, null, 0, 100, cancellationToken);
        Assert.All(versions.Where(v => v.Variation is not null), v => Assert.Null(v.Primary));
        Assert.Contains(versions, v => v is { Variation: "vbFixtureVariation", Status: "published" });

        var published = await GetAsync(site, fixture.Experience, VersionSelector.Published with { Variation = "vbFixtureVariation" }, cancellationToken: cancellationToken);
        Assert.Equal(("vbFixtureVariation", "The variation's summary"), (published.Variation, Text(published.Properties["Summary"]!["value"])));
        // The variation doesn't change the composition: the published version's.
        Assert.Equal("Hero", Text(Objects(published.Composition!["sections"]).First()["name"]));

        var draft = await GetAsync(site, fixture.Experience, new VersionSelector(VersionKind.Latest) { Variation = "vbfixturedraft" }, cancellationToken: cancellationToken);
        var intro = Objects(Objects(Objects(Objects(draft.Composition!["sections"]).Single()["rows"]).Single()["columns"]).Single()["elements"]).Single();
        Assert.Equal(("vbFixtureDraft", "Welcome, variation"), (draft.Variation, Text(intro["properties"]!["Heading"]!["value"])));
        Assert.Equal("An experience made by opticli's CMS 13 fixture", Text(draft.Properties["Summary"]!["value"]));
        var unpublished = await Assert.ThrowsAsync<NotFoundException>(() => GetAsync(site, fixture.Experience, VersionSelector.Published with { Variation = "vbFixtureDraft" }, cancellationToken: cancellationToken));
        Assert.Contains("--version latest", unpublished.Hint);

        var drafts = await new DraftReader(site.Session).ListAsync(null, null, null, [model.Type((await site.Session.HeaderAsync(fixture.Experience, cancellationToken)).TypeId)!.Id], 0, 100, cancellationToken);
        var row = Assert.Single(drafts, d => d.Ref == fixture.Experience.ToString(System.Globalization.CultureInfo.InvariantCulture) && d.Variation == "vbFixtureDraft");
        Assert.Equal(draft.Version, row.Version);
        var found = await new FindQuery(site.Session).RunAsync(model.RequireType("VbExperience"), [], null, FindStatus.Draft, null, 0, 100, cancellationToken);
        Assert.Contains(found, f => f.Id == fixture.Experience);
    }

    [SiteFact]
    public async Task Blueprints_are_left_out_of_listings_unless_asked_for()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is null)
        {
            return;
        }
        var experiences = site.Session.Model.RequireType("VbExperience");
        var blueprints = await site.Session.Db.QueryAsync("SELECT pkID, fkParentID FROM tblContent WHERE Blueprint = 1 AND Deleted = 0",
            r => (Id: r.GetInt32(0), Parent: r.GetInt32(1)), cancellationToken);
        Assert.NotEmpty(blueprints);
        var folder = blueprints[0].Parent;

        var found = await new FindQuery(site.Session).RunAsync(experiences, [], null, FindStatus.Any, null, 0, 100, cancellationToken);
        var all = await new FindQuery(site.Session, includeBlueprints: true).RunAsync(experiences, [], null, FindStatus.Any, null, 0, 100, cancellationToken);
        Assert.DoesNotContain(found, f => blueprints.Any(b => b.Id == f.Id));
        Assert.Contains(all, f => blueprints.Any(b => b.Id == f.Id));

        var tree = new TreeReader(site.Session);
        Assert.Empty(await tree.ChildIdsAsync(folder, null, cancellationToken));
        Assert.Equal(blueprints.Count(b => b.Parent == folder), await tree.BlueprintsLeftOutAsync(folder, cancellationToken));
        var (root, _) = await tree.TreeAsync((await site.Session.HeaderAsync(folder, cancellationToken)).ParentId!.Value, 1, 100, null, cancellationToken);
        var node = root.Children!.Single(c => c.Ref == folder.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal((0, blueprints.Count(b => b.Parent == folder)), (node.ChildCount, node.Blueprints));
        Assert.Equal(blueprints.Count(b => b.Parent == folder), (await new TreeReader(site.Session, includeBlueprints: true).ChildIdsAsync(folder, null, cancellationToken)).Count);

        var (hits, _) = await new SearchReader(site.Session).SearchAsync("A section made to be copied", SearchScope.Strings, null, cancellationToken);
        var (allHits, _) = await new SearchReader(site.Session, includeBlueprints: true).SearchAsync("A section made to be copied", SearchScope.Strings, null, cancellationToken);
        Assert.Empty(hits);
        Assert.True(Assert.Single(allHits).Blueprint);
    }

    [SiteFact]
    public async Task Search_and_where_used_name_the_section_and_element_a_value_is_in()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is not { } fixture)
        {
            return;
        }
        var experience = fixture.Experience.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var (hits, _) = await new SearchReader(site.Session).SearchAsync("Another inline element", SearchScope.Strings, null, cancellationToken);
        var match = Assert.Single(Assert.Single(hits, h => h.Ref == experience).Matches);
        Assert.Equal(("Body", "Body", "Second text", "VbTextElement"), (match.Property, match.Section?.Name, match.Element?.Name, match.Element?.Type));
        Assert.Equal("experience", hits.Single(h => h.Ref == experience).Kind);

        var usages = await new WhereUsedReader(site.Session).FindAsync(await site.Session.HeaderAsync(fixture.Shared, cancellationToken), cancellationToken);
        // The fixture's own placements: other content (the write tests', in the recycle bin) may place it too.
        var fixtureRefs = new[] { fixture.Experience, fixture.Second }.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var placements = usages.Where(u => u.Blueprint != true && fixtureRefs.Contains(u.Ref)).ToList();
        Assert.Equal(["Shared", "Shared again"], placements.Select(u => u.Element?.Name).Order());
        Assert.All(placements, u => Assert.Equal(("composition", "composition"), (u.Property, u.Kind)));
        // An element's own reference: the link element's Target is the start page.
        var start = (await site.Session.HeaderAsync((await GetAsync(site, fixture.Experience, cancellationToken: cancellationToken)).Parent is { } parent ? int.Parse(parent, System.Globalization.CultureInfo.InvariantCulture) : 0, cancellationToken));
        var toStart = await new WhereUsedReader(site.Session).FindAsync(start, cancellationToken);
        Assert.Contains(toStart, u => u is { Property: "Target", Element.Name: "Link", Section.Name: "Body" } && u.Ref == experience);

        var cards = await new TypeUsageReader(site.Session).InlineAsync(site.Session.Model.RequireType("VbCardElement").Id, cancellationToken);
        var card = Assert.Single(cards!.Usages, u => fixtureRefs.Contains(u.Ref));
        Assert.Equal(("inline", "Cards", "Card"), (card.Kind, card.Section?.Name, card.Element?.Name));
    }

    [SiteFact]
    public async Task Types_know_kinds_contracts_composition_behaviours_and_where_a_composition_takes_them()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is null)
        {
            return;
        }
        var model = site.Session.Model;
        var types = await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken, countInstances: true);

        Assert.Equal(
            [("IVbHeading", ContentKind.Contract), ("VbBanner", ContentKind.Block), ("VbCardElement", ContentKind.Element), ("VbExperience", ContentKind.Experience),
             ("VbLinkElement", ContentKind.Element), ("VbSection", ContentKind.Section), ("VbTextElement", ContentKind.Element)],
            types.Where(t => t.Name.StartsWith("Vb", StringComparison.Ordinal) || t.Name == "IVbHeading").Select(t => (t.Name, t.Kind)).OrderBy(t => t.Name, StringComparer.Ordinal));
        Assert.Equal(["VbCardElement", "VbLinkElement", "VbTextElement"], types.Where(t => t.Contracts.Contains("IVbHeading")).Select(t => t.Name).Order());
        Assert.Equal(["SectionEnabled"], types.Single(t => t.Name == "VbBanner").CompositionBehaviors);
        Assert.True(types.Single(t => t.Name == "VbExperience").Blueprints >= 1);

        var templates = await DisplayTemplateReader.ListAsync(site.Session.Db, model, cancellationToken);
        Assert.Equal(["vbElement", "vbRow", "vbSection"], templates.Select(t => t.Key).Where(k => k.StartsWith("vb", StringComparison.Ordinal)).Order());
        var element = types.Single(t => t.Name == "VbTextElement");
        Assert.Contains("vbElement", templates.Where(t => t.AppliesTo(element, DisplayTemplateInfo.NodeTypeOf(element)!)).Select(t => t.Key));

        var index = CSharpSourceIndex.Build(site.ProjectDirectory);
        var inSections = AllowedInQuery.Find(model, index, site.ProjectDirectory, element, explicitOnly: false);
        Assert.Contains(inSections, a => (a.Type, a.Property, a.Allowed, a.MatchedBy) == ("VbSection", "composition", "composition", "ElementEnabled"));
        Assert.DoesNotContain(inSections, a => a.Property == "UnstructuredData");
        var banners = AllowedInQuery.Find(model, index, site.ProjectDirectory, types.Single(t => t.Name == "VbBanner"), explicitOnly: true);
        Assert.Contains(banners, a => (a.Type, a.Allowed, a.MatchedBy) == ("VbExperience", "composition", "SectionEnabled"));
    }

    /// <summary>
    /// The Visual Builder oracle: every fixture item, version and variation, both ways (the DB read and the CMS through the
    /// agent), compositions node by node.
    /// </summary>
    [SiteFact]
    public async Task The_fixtures_compositions_variations_and_blueprints_read_as_the_cms_loads_them()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await FixtureAsync(site, cancellationToken) is null)
        {
            return;
        }
        var sample = await ContentSampler.VisualBuilderAsync(site.Session, 500, cancellationToken);
        Assert.Contains(sample, s => s.VersionId is not null);
        Assert.Contains(sample, s => s.Kind == ContentKind.Section);

        var comparer = new ItemComparer(site);
        var results = new List<ItemResult>();
        foreach (var item in sample)
        {
            results.Add(await comparer.CompareAsync(item, cancellationToken));
        }

        var failing = results.SelectMany(r => r.Mismatches).Where(m => KnownDifferences.Find(m) is null).ToList();
        Assert.True(failing.Count == 0, string.Join('\n', failing.Select(m => $"{m.Item} {m.Field} {m.Path}: db {m.Db} / cms {m.Agent}")));
    }
}
