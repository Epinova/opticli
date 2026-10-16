using System.Text.Json.Nodes;
using OptiCli.Core.Cms;
using OptiCli.Core.Export;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Export;

/// <summary>
/// The plan an export writes, from items as the source has them (<c>get</c>'s shape): order, GUID references, cycles,
/// language branches, folded publishes, rich text and compositions. The plans are parsed as <c>apply</c> parses them.
/// </summary>
public class ExportPlanBuilderTests
{
    private static readonly Guid Start = Guid.Parse("7b08c7d5-7585-47e5-add7-5822da68c3ce");
    private static readonly Guid Section = Guid.Parse("f7a3aca5-85c9-5b56-9fec-4a82895877da");
    private static readonly Guid PageA = Guid.Parse("e351ed2f-34a5-5da4-be93-cb2fb61413ca");
    private static readonly Guid PageB = Guid.Parse("93f7bac3-ef17-5a8c-a360-7b6bc009bf43");
    private static readonly Guid Teaser = Guid.Parse("08c8a045-d49e-58dc-b501-e4957fb8f3f8");
    private static readonly Guid Image = Guid.Parse("155cba62-ae10-561d-b0c6-831fbd91606f");
    private static readonly Guid Folder = Guid.Parse("c955624a-5cee-52d7-83c2-90a364467ef0");
    private static readonly Guid Experience = Guid.Parse("f1a09757-4afc-51d4-8e55-26e52ca861e6");
    private static readonly Guid Outside = Guid.Parse("61104228-962d-4dd2-925c-b5b12e2373ee");

    private static string Id(Guid guid) => ExportPlanBuilder.PlanId(guid);

    private static string Ref(Guid guid) => $"${Id(guid)}";

    /// <summary>A reference as get shows it, with an id that means other content in another database.</summary>
    private static string Identity(Guid guid, int id = 37) => $$$"""{"ref": "{{{id}}}", "guid": "{{{guid}}}", "type": "StandardPage", "name": "N", "status": "published"}""";

    private static ExportBranch Branch(string? language, string name, string properties = "{}", bool published = true, string? more = null) =>
        new(language, 1, published, JsonNode.Parse($$$"""
            {"ref": "99", "guid": "00000000-0000-0000-0000-000000000099", "name": "{{{name}}}", "status": "{{{(published ? "published" : "checkedOut")}}}",
             "startPublish": "2026-10-15T09:05:01.123", "properties": {{{properties}}} {{{(more is null ? "" : "," + more)}}}}
            """)!.AsObject());

    private static ExportItem Page(Guid guid, Guid parent, int order, string properties = "{}", bool published = true, string? more = null, params ExportBranch[] otherBranches) =>
        new(order, guid, ContentKind.Page, "StandardPage", parent, null, "en",
            [Branch("en", $"Page {order}", properties, published, more), .. otherBranches],
            new HashSet<string>(["MainBody", "Heading", "composition"], StringComparer.OrdinalIgnoreCase), order);

    private static JsonArray Operations(IReadOnlyList<ExportItem> items) => ExportPlanBuilder.Build(items).Operations;

    private static JsonObject Step(JsonArray operations, string op, Guid guid) =>
        operations.OfType<JsonObject>().Single(o => (string?)o["op"] == op && ((string?)o["guid"] == guid.ToString() || (string?)o["ref"] == Ref(guid)));

    private static int IndexOf(JsonArray operations, Guid guid) =>
        operations.Select((o, i) => (o, i)).First(p => (string?)p.o!["guid"] == guid.ToString()).i;

    /// <summary>The plan as apply reads it: valid, with every $id defined by an earlier step.</summary>
    private static WritePlan Parse(JsonArray operations) => WritePlan.Parse(new JsonObject { ["source"] = new JsonObject(), ["operations"] = operations.DeepClone() }.ToJsonString());

    [Fact]
    public void Parents_come_first_and_refer_to_planned_content_by_id_and_to_the_rest_by_guid()
    {
        var child = Page(PageA, Section, order: 0, $$$"""{"PageImage": {"type": "ContentReference", "value": {{{Identity(Image, 3765)}}}}, "Heading": {"type": "LongString", "value": "Hi", "culture": "en"}}""");
        var parent = Page(Section, Start, order: 1);

        var operations = Operations([child, parent]);

        Assert.Equal([Section.ToString(), PageA.ToString()], operations.Select(o => (string?)o!["guid"]));
        var create = Step(operations, "create", PageA);
        Assert.Equal(Ref(Section), (string?)create["parent"]);
        Assert.Equal(Start.ToString(), (string?)Step(operations, "create", Section)["parent"]);
        // By GUID, never the source's id (3765), which is other content in another database.
        Assert.Equal(Image.ToString(), (string?)create["properties"]!["PageImage"]);
        Assert.Equal("Hi", (string?)create["properties"]!["Heading"]);
        Assert.Equal(("StandardPage", "Page 0", "en", true), ((string?)create["type"], (string?)create["name"], (string?)create["lang"], (bool?)create["publish"]));

        var plan = ExportPlanBuilder.Build([child, parent]);
        Assert.Equal([Image, Start], plan.ExternalRefs.Select(e => e.Guid).Order());
        Assert.True(plan.ExternalRefs.Single(e => e.Guid == Start).Placement);
        Assert.Equal([PageA], plan.ExternalRefs.Single(e => e.Guid == Image).From);
        Parse(operations);
    }

    [Fact]
    public void Content_comes_before_what_refers_to_it_when_there_is_no_cycle()
    {
        var page = Page(PageA, Start, order: 0, $$$"""{"MainContentArea": {"type": "ContentArea", "value": [{{{Identity(Teaser)}}}]}}""");
        var block = new ExportItem(1, Teaser, ContentKind.Block, "TeaserBlock", Folder, null, "en", [Branch("en", "Teaser")], new HashSet<string>(), Order: 1);

        var operations = Operations([page, block]);

        Assert.True(IndexOf(operations, Teaser) < IndexOf(operations, PageA));
        Assert.Equal($$$"""[{"ref":"{{{Ref(Teaser)}}}"}]""", Step(operations, "create", PageA)["properties"]!["MainContentArea"]!.ToJsonString());
        Assert.Equal(0, ExportPlanBuilder.Build([page, block]).HeldBack);
        Assert.Equal("block", (string?)operations[0]!["op"]);
    }

    [Fact]
    public void A_cycle_is_closed_by_a_set_that_publishes_once_both_sides_exist()
    {
        var a = Page(PageA, Section, order: 0, $$$"""
            {"MainBody": {"type": "XhtmlString", "value": "<p>See <a href=\"~/link/{{{PageB:N}}}.aspx#top\">B</a>.</p>", "links": [{{{Identity(PageB)}}}]},
             "MainContentArea": {"type": "ContentArea", "value": [{{{Identity(PageB)}}}]},
             "Heading": {"type": "LongString", "value": "A"}}
            """);
        var b = Page(PageB, Section, order: 1, $$$"""{"Related": {"type": "ContentReference", "value": {{{Identity(PageA)}}}}}""");

        var plan = ExportPlanBuilder.Build([a, b]);
        var operations = plan.Operations;

        Assert.Equal(["create", "create", "set"], operations.Select(o => (string?)o!["op"]));
        var createA = Step(operations, "create", PageA);
        // A's create has its own values, not those that refer to B (created after it), and doesn't publish: its set does.
        var own = createA["properties"]!.AsObject();
        Assert.Equal("A", (string?)own["Heading"]);
        Assert.False(own.ContainsKey("MainBody") || own.ContainsKey("MainContentArea"));
        Assert.Null(createA["publish"]);
        Assert.Equal(Ref(PageA), (string?)Step(operations, "create", PageB)["properties"]!["Related"]);
        var set = Step(operations, "set", PageA);
        Assert.Equal($"<p>See <a href=\"{Ref(PageB)}#top\">B</a>.</p>", (string?)set["properties"]!["MainBody"]);
        Assert.Equal($$$"""[{"ref":"{{{Ref(PageB)}}}"}]""", set["properties"]!["MainContentArea"]!.ToJsonString());
        Assert.Equal(("en", true), ((string?)set["lang"], (bool?)set["publish"]));
        Assert.Equal(2, plan.HeldBack);
        // Valid as a plan: every $id an earlier step's, and the rich text link to content that exists by then.
        WritePlan.RequireNoForwardLinks(WritePlan.LinkText(Parse(operations).Steps), new Dictionary<string, int>());
    }

    [Fact]
    public void Content_that_refers_to_a_cycle_comes_after_all_of_it_even_when_it_is_earlier_in_the_tree()
    {
        // An experience under Start (earlier in the tree than A and B, which are a cycle) that refers to A.
        var experience = Page(Experience, Start, order: 0, $$$"""{"Target": {"type": "ContentReference", "value": {{{Identity(PageA)}}}}}""");
        var a = Page(PageA, Start, order: 1, $$$"""{"Related": {"type": "ContentReference", "value": {{{Identity(PageB)}}}}}""");
        var b = Page(PageB, Start, order: 2, $$$"""{"Related": {"type": "ContentReference", "value": {{{Identity(PageA)}}}}}""");

        var plan = ExportPlanBuilder.Build([experience, a, b]);

        Assert.Equal([PageA.ToString(), PageB.ToString(), Experience.ToString()], plan.Operations.Take(3).Select(o => (string?)o!["guid"]));
        Assert.Equal(1, plan.HeldBack);
        Assert.Equal(Ref(PageA), (string?)Step(plan.Operations, "create", Experience)["properties"]!["Target"]);
    }

    [Fact]
    public void A_reference_to_itself_is_held_back_too()
    {
        var page = Page(PageA, Start, order: 0, $$$"""{"Related": {"type": "ContentReference", "value": {{{Identity(PageA)}}}}}""");

        var operations = Operations([page]);

        Assert.Null(Step(operations, "create", PageA)["properties"]!["Related"]);
        Assert.Equal(Ref(PageA), (string?)Step(operations, "set", PageA)["properties"]!["Related"]);
        Parse(operations);
    }

    [Fact]
    public void Other_branches_are_translated_with_their_culture_specific_values_and_published_when_they_are()
    {
        var page = Page(PageA, Start, order: 0,
            """{"Heading": {"type": "LongString", "value": "Hello", "culture": "en"}, "Shared": {"type": "Number", "value": 3}}""",
            otherBranches:
            [
                Branch("sv", "Hej", """{"Heading": {"type": "LongString", "value": "Hej", "culture": "sv"}, "Shared": {"type": "Number", "value": 3}}""",
                    more: """ "urlSegment": "hej", "visibleInMenu": false, "sortIndex": 5, "category": ["News"], "childSortOrder": "Index" """),
                Branch("de", "Hallo", """{"Heading": {"type": "LongString", "value": "Hallo", "culture": "de"}}""", published: false),
            ]);

        var operations = Operations([page]);

        var sv = operations.OfType<JsonObject>().Single(o => (string?)o["op"] == "translate" && (string?)o["lang"] == "sv");
        Assert.Equal(("Hej", true), ((string?)sv["name"], (bool?)sv["publish"]));
        // Only culture-specific values, and of the built-in settings those that are per branch.
        Assert.Equal(["Heading", "StartPublish", "StopPublish", "PageURLSegment", "SimpleAddress", "Shortcut"], sv["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal("hej", (string?)sv["properties"]!["PageURLSegment"]);
        var de = operations.OfType<JsonObject>().Single(o => (string?)o["op"] == "translate" && (string?)o["lang"] == "de");
        Assert.Null(de["publish"]);
        // The master branch first: its create publishes it before the translations.
        Assert.Equal(["create", "translate", "translate"], operations.Select(o => (string?)o!["op"]));
        Assert.Equal(3, Step(operations, "create", PageA)["properties"]!["Shared"]!.GetValue<int>());
        Parse(operations);
    }

    [Fact]
    public void Built_in_settings_are_written_as_the_source_has_them()
    {
        var page = Page(PageA, Start, order: 0, more: $$$"""
            "stopPublish": "2030-01-01T00:00:00", "childSortOrder": "Index", "sortIndex": 309, "urlSegment": "page-a", "visibleInMenu": false,
            "simpleAddress": "/campaign", "category": ["Meet"],
            "shortcut": {"type": "shortcut", "to": {{{Identity(PageB)}}}}
            """);
        var folder = new ExportItem(2, Folder, ContentKind.Folder, "SysContentFolder", Start, null, null, [Branch(null, "Media")], new HashSet<string>(), Order: 1);

        var operations = Operations([page, folder]);

        var values = Step(operations, "create", PageA)["properties"]!;
        Assert.Equal("2026-10-15T09:05:01.123Z", (string?)values["StartPublish"]);
        Assert.Equal("2030-01-01T00:00:00Z", (string?)values["StopPublish"]);
        Assert.Equal(("Index", 309, "page-a", false, "/campaign"), ((string?)values["ChildSortOrder"], values["SortIndex"]!.GetValue<int>(), (string?)values["PageURLSegment"], values["PageVisibleInMenu"]!.GetValue<bool>(), (string?)values["SimpleAddress"]));
        Assert.Equal("""["Meet"]""", values["Category"]!.ToJsonString());
        Assert.Equal($$$"""{"type":"shortcut","to":"{{{PageB}}}"}""", values["Shortcut"]!.ToJsonString());
        // A folder has no versions: no publish dates, no publish.
        var created = Step(operations, "create", Folder);
        Assert.Null(created["properties"]);
        Assert.Null(created["publish"]);
        Assert.Null(created["lang"]);
    }

    [Fact]
    public void Blocks_and_media_in_a_for_this_page_folder_go_with_their_owner()
    {
        var owner = Page(Section, Start, order: 1);
        var block = new ExportItem(3, Teaser, ContentKind.Block, "TeaserBlock", Guid.NewGuid(), Section, "en", [Branch("en", "Teaser")], new HashSet<string>(), Order: 0);
        var image = new ExportItem(4, Image, ContentKind.Media, "ImageFile", Folder, null, null, [Branch(null, "a.png")], new HashSet<string>(), Order: 2)
        {
            File = ExportPlanBuilder.FilePath(Image, "a.png"),
        };
        var noFile = new ExportItem(5, Guid.NewGuid(), ContentKind.Media, "ImageFile", Folder, null, null, [Branch(null, "b.png")], new HashSet<string>(), Order: 3);

        var plan = ExportPlanBuilder.Build([owner, block, image, noFile]);

        var created = Step(plan.Operations, "block", Teaser);
        Assert.Equal(Ref(Section), (string?)created["for"]);
        Assert.Null(created["parent"]);
        Assert.True(IndexOf(plan.Operations, Section) < IndexOf(plan.Operations, Teaser));
        var upload = Step(plan.Operations, "upload", Image);
        Assert.Equal(($"files/{Image:N}/a.png", Folder.ToString(), "ImageFile", "a.png"), ((string?)upload["file"], (string?)upload["parent"], (string?)upload["type"], (string?)upload["name"]));
        Assert.True((bool?)upload["publish"]);
        Assert.Single(plan.Skipped);
        Assert.Equal(noFile.Guid, plan.Skipped[0].Guid);
        Parse(plan.Operations);
    }

    [Fact]
    public void Rich_text_names_blocks_by_guid_and_planned_content_by_id()
    {
        var text = $$$"""
            <p><img src="/link/{{{Image:N}}}.aspx" alt="i"><a href="~/link/{{{Outside:N}}}.aspx">out</a><a href="~/link/{{{PageB:N}}}.aspx?epslanguage=sv">sv</a></p>
            <div data-classid="36f4349b" data-contentlink="3767" data-contentguid="{{{Teaser}}}" data-contentname="T">{}</div>
            <div data-classid="36f4349b" data-contentlink="12">{}</div>
            """;
        var page = Page(PageA, Start, order: 2, $$$"""
            {"MainBody": {"type": "XhtmlString", "value": {{{JsonValue.Create(text).ToJsonString()}}}, "blocks": [{{{Identity(Teaser, 3767)}}}, {{{Identity(Outside, 12)}}}]}}
            """);
        var b = Page(PageB, Start, order: 0);
        var image = new ExportItem(4, Image, ContentKind.Media, "ImageFile", Start, null, null, [Branch(null, "a.png")], new HashSet<string>(), Order: 1) { File = "files/a.png" };
        var teaser = new ExportItem(3, Teaser, ContentKind.Block, "TeaserBlock", Folder, null, "en", [Branch("en", "Teaser")], new HashSet<string>(), Order: 3);

        var plan = ExportPlanBuilder.Build([page, b, image, teaser]);

        var body = (string)Step(plan.Operations, "create", PageA)["properties"]!["MainBody"]!;
        Assert.Contains($"<img src=\"{Ref(Image)}\" alt=\"i\">", body);
        Assert.Contains($"<a href=\"~/link/{Outside:N}.aspx\">out</a>", body);
        Assert.Contains($"<a href=\"{Ref(PageB)}?epslanguage=sv\">sv</a>", body);
        // The id is the source's: dropped where the GUID is there, the GUID put in where it isn't.
        Assert.Contains($"<div data-classid=\"36f4349b\" data-contentguid=\"{Ref(Teaser)}\" data-contentname=\"T\">", body);
        Assert.Contains($"<div data-classid=\"36f4349b\" data-contentguid=\"{Outside}\">", body);
        Assert.DoesNotContain("data-contentlink", body);
        Assert.Equal([Outside], plan.ExternalRefs.Select(e => e.Guid).Where(g => g != Start && g != Folder));
        WritePlan.LinkText(Parse(plan.Operations).Steps);
    }

    [Fact]
    public void Values_that_cant_be_written_elsewhere_are_dropped_and_listed()
    {
        var page = Page(PageA, Start, order: 0, """
            {"Gone": {"type": "ContentReference", "value": {"ref": "99", "guid": "00000000-0000-0000-0000-000000000000", "missing": true}},
             "MainBody": {"type": "XhtmlString", "value": "<div data-inlineblocktypeid=\"196\" data-contentguid=\"\">{}</div>", "blocks": [{"inline": true, "type": "TeaserBlock"}]},
             "Dam": {"type": "ContentReference", "value": {"ref": "63__dam", "guid": "8c9ccb7a-963f-4d19-9672-7fd7356a2252", "provider": "dam"}}}
            """);

        var plan = ExportPlanBuilder.Build([page]);

        Assert.Equal(["Gone", "MainBody"], plan.Dropped.Select(d => d.Property).Order());
        Assert.All(plan.Dropped, d => Assert.Equal((PageA, "en"), (d.Guid, d.Language)));
        var values = Step(plan.Operations, "create", PageA)["properties"]!.AsObject();
        Assert.False(values.ContainsKey("Gone"));
        Assert.Equal("63__dam", (string?)values["Dam"]);
        Assert.Equal(["63__dam"], plan.ProviderRefs);
    }

    [Fact]
    public void Areas_links_and_blocks_are_written_as_set_takes_them()
    {
        var page = Page(PageA, Start, order: 0, $$$"""
            {"MainContentArea": {"type": "ContentArea", "value": [
               {"ref": "37", "guid": "{{{Outside}}}", "type": "TeaserBlock", "name": "T", "displayOption": "wide", "group": "g1", "visitorGroups": ["5c1b2a0e-3f4d-4a6b-9c8d-7e6f5a4b3c2d"], "visitorGroupNames": ["Returning"], "renderSettings": {"x": "y"}},
               {"inline": true, "type": "TeaserBlock", "name": "Inline", "properties": {"Link": {"type": "ContentReference", "value": {{{Identity(Start, 5)}}}}, "Text": {"type": "LongString", "value": "@home"}} }]},
             "Links": {"type": "LinkCollection", "value": [{"text": "Start", "href": "~/link/{{{Start:N}}}.aspx", "content": {{{Identity(Start, 5)}}}}]},
             "Hero": {"type": "Block", "blockType": "HeroBlock", "value": {"Image": {"type": "ContentReference", "value": {{{Identity(Image)}}}}, "Title": {"type": "LongString", "value": null}} },
             "Items": {"type": "BlockList", "blockType": "Fact", "value": [{"Label": {"type": "LongString", "value": "L"}}]},
             "Pages": {"type": "ContentReferenceList", "value": [{{{Identity(Outside)}}}]}}
            """);

        var plan = ExportPlanBuilder.Build([page]);
        var values = Step(plan.Operations, "create", PageA)["properties"]!;

        Assert.Equal($$$"""[{"ref":"{{{Outside}}}","displayOption":"wide","group":"g1","visitorGroups":["5c1b2a0e-3f4d-4a6b-9c8d-7e6f5a4b3c2d"]},{"type":"TeaserBlock","name":"Inline","properties":{"Link":"{{{Start}}}","Text":"@@home"}}]""",
            values["MainContentArea"]!.ToJsonString());
        Assert.Equal($$$"""[{"text":"Start","href":"~/link/{{{Start:N}}}.aspx"}]""", values["Links"]!.ToJsonString());
        Assert.Equal($$$"""{"Image":"{{{Image}}}","Title":null}""", values["Hero"]!.ToJsonString());
        Assert.Equal("""[{"Label":"L"}]""", values["Items"]!.ToJsonString());
        Assert.Equal($$$"""["{{{Outside}}}"]""", values["Pages"]!.ToJsonString());
        Assert.Equal(["5c1b2a0e-3f4d-4a6b-9c8d-7e6f5a4b3c2d"], plan.VisitorGroups);
    }

    [Fact]
    public void Long_rich_text_goes_to_a_file_the_plan_reads()
    {
        var text = "<p>" + new string('x', 50) + "</p>";
        var page = Page(PageA, Start, order: 0, $$$"""{"MainBody": {"type": "XhtmlString", "value": "{{{text}}}"}}""",
            otherBranches: Branch("sv", "Sv", $$$"""{"MainBody": {"type": "XhtmlString", "value": "@{{{text}}}"}}"""));

        var plan = ExportPlanBuilder.Build([page], textFileLength: 20);

        Assert.Equal($"@texts/{PageA:N}.en.MainBody.html", (string?)Step(plan.Operations, "create", PageA)["properties"]!["MainBody"]);
        Assert.Equal(text, plan.Texts[$"texts/{PageA:N}.en.MainBody.html"]);
        // A text that starts with @ is the file's text as it is.
        Assert.Equal("@" + text, plan.Texts[$"texts/{PageA:N}.sv.MainBody.html"]);
    }

    /// <summary>A composition as get shows it: an inline section with an element that links to planned content, and a shared one.</summary>
    private static string Composition(Guid target, string culture) => $$$"""
        {"layout": "outline", "culture": "{{{culture}}}", "sections": [
          {"key": "a2e986c4-7de7-471e-aba3-7713d60663ab", "name": "Cards", "type": "VbSection", "inline": true, "rows": [
            {"key": "e8a64c2b-55d9-41d5-b20a-ac7da4df9ac8", "name": "Row", "displayTemplate": "vbRow", "displaySettings": {"gap": "wide"}, "columns": [
              {"key": "c3511fff-d754-4829-88aa-ca2deaeed4a4", "name": "First", "elements": [
                {"key": "b1c6e1fb-e689-45d4-9902-a8c902adc28c", "name": "Link", "type": "VbLinkElement", "inline": true,
                 "properties": {"Target": {"type": "ContentReference", "value": {{{Identity(target, 975)}}}}, "Heading": {"type": "LongString", "value": "To A"}} },
                {"key": "9d123751-29ae-48cb-8fc0-89ac25372f2e", "name": "Shared", "type": "VbTextElement", "content": {{{Identity(Outside, 103)}}}},
                {"key": "0a0a0a0a-29ae-48cb-8fc0-89ac25372f2e", "name": "Gone", "missing": true}]}]}]}]}
        """;

    [Fact]
    public void A_composition_keeps_its_keys_with_plain_values_and_is_written_per_branch_that_stores_one()
    {
        var a = Page(PageA, Start, order: 1);
        var experience = new ExportItem(1, Experience, ContentKind.Experience, "VbExperience", Start, null, "en",
            [
                Branch("en", "Experience", more: $"\"composition\": {Composition(PageA, "en")}"),
                Branch("sv", "Upplevelse", more: $"\"composition\": {Composition(PageA, "sv")}"),
                // A branch made by a translation, with no composition of its own: none is written.
                Branch("de", "Erlebnis", more: """ "composition": {"layout": "outline", "sections": []} """),
                // A branch that stores an empty composition: not written (CMS 13.3 would delete the master branch's blocks), listed.
                Branch("nb", "Opplevelse", more: """ "composition": {"layout": "outline", "culture": "nb", "sections": []} """),
            ],
            new HashSet<string>(["composition"]), Order: 0);

        var plan = ExportPlanBuilder.Build([experience, a]);
        var operations = plan.Operations;

        Assert.True(IndexOf(operations, PageA) < IndexOf(operations, Experience));
        var composition = Step(operations, "create", Experience)["properties"]!["composition"]!;
        var section = composition["sections"]![0]!;
        Assert.Equal(("a2e986c4-7de7-471e-aba3-7713d60663ab", "VbSection"), ((string?)section["key"], (string?)section["type"]));
        Assert.Null(composition["layout"]);
        Assert.Null(composition["culture"]);
        Assert.Null(section["inline"]);
        var elements = section["rows"]![0]!["columns"]![0]!["elements"]!.AsArray();
        Assert.Equal($$$"""{"Target":"{{{Ref(PageA)}}}","Heading":"To A"}""", elements[0]!["properties"]!.ToJsonString());
        Assert.Equal((Outside.ToString(), null), ((string?)elements[1]!["ref"], elements[1]!["content"]));
        Assert.Equal(2, elements.Count);
        Assert.Contains(plan.Dropped, d => d.Property.EndsWith("elements[2]", StringComparison.Ordinal) && d.Language == "en");
        Assert.Equal("wide", (string?)section["rows"]![0]!["displaySettings"]!["gap"]);

        // sv: translate, then a set with its composition (translate takes none), which publishes it.
        var sv = operations.OfType<JsonObject>().Where(o => (string?)o["lang"] == "sv").ToList();
        Assert.Equal(["translate", "set"], sv.Select(o => (string?)o["op"]));
        Assert.Null(sv[0]["publish"]);
        Assert.True((bool?)sv[1]["publish"]);
        Assert.NotNull(sv[1]["properties"]!["composition"]);
        var de = operations.OfType<JsonObject>().Where(o => (string?)o["lang"] == "de").ToList();
        Assert.Equal(["translate"], de.Select(o => (string?)o["op"]));
        Assert.Null(de[0]["properties"]?["composition"]);
        var nb = operations.OfType<JsonObject>().Where(o => (string?)o["lang"] == "nb").ToList();
        Assert.Equal(["translate"], nb.Select(o => (string?)o["op"]));
        Assert.True((bool?)nb[0]["publish"]);
        Assert.Null(nb[0]["properties"]?["composition"]);
        Assert.Contains(plan.Dropped, d => d.Property == "composition" && d.Language == "nb" && d.Reason.Contains("master branch's stored blocks", StringComparison.Ordinal));
        Parse(operations);
    }

    [Fact]
    public void A_folder_in_a_for_this_page_folder_goes_with_its_owner_and_what_is_in_it_with_the_folder()
    {
        var owner = Page(Section, Start, order: 0);
        var subfolder = new ExportItem(2, Folder, ContentKind.Folder, "SysContentFolder", Guid.NewGuid(), Section, null, [Branch(null, "Sub")], new HashSet<string>(), Order: 1);
        var image = new ExportItem(3, Image, ContentKind.Media, "ImageFile", Folder, null, null, [Branch(null, "a.png")], new HashSet<string>(), Order: 2) { File = "files/a.png" };

        var operations = Operations([owner, subfolder, image]);

        var folder = Step(operations, "create", Folder);
        Assert.Equal((Ref(Section), null), ((string?)folder["for"], (string?)folder["parent"]));
        Assert.Equal(Ref(Folder), (string?)Step(operations, "upload", Image)["parent"]);
        Parse(operations);
    }

    [Fact]
    public void A_branch_scheduled_for_later_is_published_then_and_one_whose_time_has_passed_stays_a_draft()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var later = Page(PageA, Start, order: 0, published: false);
        later.Branches[0].Document["status"] = "delayedPublish";
        later.Branches[0].Document["startPublish"] = "2030-01-01T08:00:00";
        var passed = Page(PageB, Start, order: 1, published: false);
        passed.Branches[0].Document["status"] = "delayedPublish";
        passed.Branches[0].Document["startPublish"] = "2026-01-01T08:00:00";

        var operations = ExportPlanBuilder.Build([later, passed], now: now).Operations;

        var publish = operations.OfType<JsonObject>().Single(o => (string?)o["op"] == "publish");
        Assert.Equal((Ref(PageA), "en", "2030-01-01T08:00:00Z"), ((string?)publish["ref"], (string?)publish["lang"], (string?)publish["publishAt"]));
        Assert.Equal("publish", (string?)operations[^1]!["op"]);
        Assert.Null(Step(operations, "create", PageA)["publish"]);
        Assert.Null(Step(operations, "create", PageB)["publish"]);
        Parse(operations);
    }

    [Fact]
    public void For_a_known_target_values_that_refer_to_what_it_lacks_are_dropped_and_the_rest_of_an_area_kept()
    {
        var group = "5c1b2a0e-3f4d-4a6b-9c8d-7e6f5a4b3c2d";
        var page = Page(PageA, Start, order: 0, $$$"""
            {"MainContentArea": {"type": "ContentArea", "value": [{{{Identity(Outside)}}}, {{{Identity(Image)}}}, {"ref": "4", "guid": "{{{PageB}}}", "visitorGroups": ["{{{group}}}"]}]},
             "PageImage": {"type": "ContentReference", "value": {{{Identity(Outside)}}}},
             "MainBody": {"type": "XhtmlString", "value": "<p><a href=\"~/link/{{{Outside:N}}}.aspx\">x</a></p>"},
             "Topics": {"type": "Category", "value": ["News", "Gone"]},
             "Heading": {"type": "LongString", "value": "Kept"}}
            """, more: """ "category": ["Gone"] """);
        var target = new ExportTarget(g => g == Image || g == PageB || g == Start, id => id != group, name => name == "News");

        var plan = ExportPlanBuilder.Build([page], target: target);

        var values = Step(plan.Operations, "create", PageA)["properties"]!.AsObject();
        // The image stays; the item the target lacks and the item for a visitor group it lacks go.
        Assert.Equal($$$"""[{"ref":"{{{Image}}}"}]""", values["MainContentArea"]!.ToJsonString());
        Assert.False(values.ContainsKey("PageImage") || values.ContainsKey("MainBody") || values.ContainsKey("Topics") || values.ContainsKey("Category"));
        Assert.Equal("Kept", (string?)values["Heading"]);
        Assert.Equal(["Category", "MainBody", "MainContentArea[0]", "MainContentArea[2]", "PageImage", "Topics"], plan.Dropped.Select(d => d.Property).Order());
        Assert.Contains(plan.Dropped, d => d.Property == "MainContentArea[2]" && d.Reason.Contains(group, StringComparison.Ordinal));
        Assert.DoesNotContain(plan.ExternalRefs, e => e.Guid == Outside);
        Parse(plan.Operations);
    }

    [Fact]
    public void Comparable_values_name_every_reference_by_guid_with_held_back_values_in()
    {
        var a = Page(PageA, Start, order: 0, $$$"""{"Related": {"type": "ContentReference", "value": {{{Identity(PageB)}}}}, "Heading": {"type": "LongString", "value": "A"}}""",
            otherBranches: Branch("sv", "A sv", """{"Heading": {"type": "LongString", "value": "A sv", "culture": "sv"}}""", published: false));

        var comparable = ExportPlanBuilder.Comparable(a);

        Assert.Equal(["en", "sv"], comparable.Keys.Order());
        var en = comparable["en"];
        Assert.Equal(("Page 0", true), ((string?)en["name"], (bool?)en["published"]));
        Assert.Equal(PageB.ToString(), (string?)en["values"]!["Related"]);
        Assert.Equal("A", (string?)en["values"]!["Heading"]);
        Assert.Equal(("A sv", false), ((string?)comparable["sv"]["name"], (bool?)comparable["sv"]["published"]));
        // With a target that lacks B, the reference is left out of what is compared, as the plan leaves it out.
        var dropped = new List<DroppedValue>();
        Assert.False(ExportPlanBuilder.Comparable(a, new ExportTarget(_ => false, _ => true, _ => true), dropped)["en"]["values"]!.AsObject().ContainsKey("Related"));
        Assert.Single(dropped);
    }
}
