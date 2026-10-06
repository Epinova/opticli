using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class TextRefsTests
{
    private static readonly Guid Page = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Links_and_blocks_in_rich_text_are_found_but_other_dollars_are_text()
    {
        var found = TextRefs.Find("""<p>Costs $5. <a href="$page">Read</a> <a HREF='$page#prices'>prices</a></p><div data-contentguid="$teaser"></div><div data-contentlink="$hero"></div><a title="$page">x</a>""").ToList();

        Assert.Equal([new("href", "page"), new("href", "page"), new("data-contentguid", "teaser"), new("data-contentlink", "hero")], found);
    }

    [Fact]
    public void They_become_what_the_cms_stores()
    {
        var text = TextRefs.Map("""<a href="$page#prices">x</a><div data-contentguid="$page" data-contentlink="$page"></div>""",
            r => TextRefs.Value(r.Attribute, 42, Page));

        Assert.Equal("""<a href="~/link/11111111222233334444555555555555.aspx#prices">x</a><div data-contentguid="11111111-2222-3333-4444-555555555555" data-contentlink="42"></div>""", text);
        Assert.Equal("""<a href="$page">x</a>""", TextRefs.Map("""<a href="$page">x</a>""", r => TextRefs.Value(r.Attribute, null, null)));
    }

    private const string Namespace = "\"guidNamespace\": \"0f6c2a8e-5b1d-4e7a-9a43-7d2c8e1b5f60\",";

    private static string Plan(string guidNamespace, string firstBody, string secondBody = "<p>B</p>") => $$$"""
        { {{{guidNamespace}}} "operations": [
          {"op": "create", "id": "a", "parent": "5", "type": "StandardPage", "name": "A", "properties": {"MainBody": "{{{firstBody}}}"}},
          {"op": "create", "id": "b", "parent": "5", "type": "StandardPage", "name": "B", "properties": {"MainBody": "{{{secondBody}}}"}}]}
        """;

    [Fact]
    public void A_link_to_an_earlier_step_is_a_dependency()
    {
        var steps = WritePlan.LinkText(WritePlan.Parse(Plan("", "<p>A</p>", "<a href='$a'>A</a>")).Steps);

        Assert.Equal(["a"], steps[1].DependsOn);
    }

    [Fact]
    public void A_link_to_a_later_step_needs_its_guid_in_advance()
    {
        var problem = Assert.Throws<UsageException>(() => WritePlan.LinkText(WritePlan.Parse(Plan("", "<a href='$b'>B</a>")).Steps));
        Assert.Contains("guidNamespace", problem.Message);

        var steps = WritePlan.LinkText(WritePlan.Parse(Plan(Namespace, "<a href='$b'>B</a>")).Steps);
        Assert.Empty(steps[0].DependsOn);
        var resolved = (CreateOperation)WritePlan.Resolve(steps[0], new Dictionary<string, int>(), WritePlan.FixedGuids(steps));
        Assert.Equal($"<a href='{TextRefs.PermanentLink(steps[1].Operation.ContentGuid!.Value)}'>B</a>", (string?)resolved.Properties!["MainBody"]);
    }

    [Fact]
    public void On_cms_13_a_link_to_a_later_step_is_refused_unless_the_content_exists()
    {
        var steps = WritePlan.LinkText(WritePlan.Parse(Plan(Namespace, "<a href='$b'>B</a>", "<a href='$a'>A</a>")).Steps);

        var problem = Assert.Throws<UsageException>(() => WritePlan.RequireNoForwardLinks(steps, new Dictionary<string, int>()));
        Assert.Contains("operations[0]: the text links to '$b', which a later operation (operations[1]) creates.", problem.Message);
        Assert.DoesNotContain("operations[1]", problem.Message.Replace("(operations[1])", ""));
        Assert.Contains("later set operation", problem.Hint);
        // A run with --update-existing that finds the content made by an earlier run.
        WritePlan.RequireNoForwardLinks(steps, new Dictionary<string, int> { ["b"] = 42 });
        WritePlan.RequireNoForwardLinks(WritePlan.LinkText(WritePlan.Parse(Plan("", "<p>A</p>", "<a href='$a'>A</a>")).Steps), new Dictionary<string, int>());
    }

    [Fact]
    public void A_block_by_id_and_an_unknown_id_are_problems()
    {
        var later = Assert.Throws<UsageException>(() => WritePlan.LinkText(WritePlan.Parse(Plan(Namespace, "<div data-contentlink='$b'></div>")).Steps));
        Assert.Contains("needs the content's id", later.Message);
        var unknown = Assert.Throws<UsageException>(() => WritePlan.LinkText(WritePlan.Parse(Plan("", "<a href='$nope'>?</a>")).Steps));
        Assert.Contains("not the id of", unknown.Message);
    }

    [Fact]
    public void Content_created_earlier_is_linked_by_its_id_and_guid_once_known()
    {
        var steps = WritePlan.LinkText(WritePlan.Parse(Plan("", "<p>A</p>", "<a href='$a'>A</a><div data-contentlink='$a'></div>")).Steps);

        var resolved = (CreateOperation)WritePlan.Resolve(steps[1], new Dictionary<string, int> { ["a"] = 42 }, new Dictionary<string, Guid> { ["a"] = Page });

        Assert.Equal($"<a href='{TextRefs.PermanentLink(Page)}'>A</a><div data-contentlink='42'></div>", (string?)resolved.Properties!["MainBody"]);
    }

    [Fact]
    public void A_dry_run_on_existing_content_links_planned_content_through_a_stand_in()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "a", "parent": "5", "type": "StandardPage", "name": "A"},
              {"op": "set", "ref": "7", "properties": {"MainBody": "<a href=\"$a\">A</a>"}},
              {"op": "set", "ref": "8", "properties": {"MainArea": [{"ref": "$a"}]}}]}
            """);
        var steps = WritePlan.LinkText(plan.Steps);

        var linked = Assert.IsType<SetOperation>(PlanSimulation.WithStandInLinks(steps[1].Operation, steps, new Dictionary<string, int>()));
        Assert.Matches("^<a href=\"~/link/[0-9a-f]{32}\\.aspx\">A</a>$", (string?)linked.Properties!["MainBody"]);
        Assert.Null(PlanSimulation.WithStandInLinks(steps[2].Operation, steps, new Dictionary<string, int>()));
    }
}
