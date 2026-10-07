using System.Text.Json;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

/// <summary>CMS 13: a write's composition change, node by node, from the whole composition before and after the agent reports.</summary>
public class CompositionChangesTests
{
    private const string Before = """
        {"nodes":[
          {"nodeType":"section","key":"hero","name":"Hero","type":"VbSection","nodes":[
            {"nodeType":"row","key":"row","nodes":[
              {"nodeType":"column","key":"left","name":"Left","nodes":[
                {"nodeType":"component","key":"intro","name":"Intro","type":"Text","displayTemplate":"look","properties":{"Heading":"Hi","Body":"<p>a</p>"}},
                {"nodeType":"component","key":"second","name":"Second","type":"Text","properties":{"Heading":"Two"}}]},
              {"nodeType":"column","key":"right","name":"Right","nodes":[
                {"nodeType":"component","key":"shared","name":"Shared","type":"Text","ref":"103"}]}]}]},
          {"nodeType":"component","key":"promo","name":"Promo","type":"Banner","properties":{"Title":"T"}}]}
        """;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Show(IReadOnlyList<CompositionChange> changes) => JsonSerializer.Serialize(changes, AgentJson.Options);

    [Fact]
    public void Nothing_changed_is_no_change()
    {
        Assert.Empty(CompositionChanges.Compare(Json(Before), Json(Before)));
    }

    [Fact]
    public void A_new_node_is_added_where_it_is_with_what_it_holds_and_its_children_arent_listed_again()
    {
        var after = Before.Replace("""{"nodeType":"component","key":"promo",""", """{"nodeType":"section","key":"new","name":"Body","type":"VbSection","nodes":[{"nodeType":"row","key":"new-row","nodes":[{"nodeType":"column","key":"new-col","nodes":[]}]}]},{"nodeType":"component","key":"promo",""");

        var change = Assert.Single(CompositionChanges.Compare(Json(Before), Json(after)));

        Assert.Equal("""{"change":"added","nodeType":"section","key":"new","name":"Body","type":"VbSection","in":"root","at":1,"holds":2}""", Show([change])[1..^1]);
    }

    [Fact]
    public void A_removed_node_is_listed_once_with_what_it_held()
    {
        var after = """{"nodes":[{"nodeType":"component","key":"promo","name":"Promo","type":"Banner","properties":{"Title":"T"}}]}""";

        var change = Assert.Single(CompositionChanges.Compare(Json(Before), Json(after)));

        Assert.Equal(("removed", "section", "hero", 6), (change.Change, change.NodeType, change.Key, change.Holds));
    }

    [Fact]
    public void A_value_style_or_name_change_lists_each_field_before_and_after()
    {
        var after = Before.Replace("""{"nodeType":"component","key":"intro","name":"Intro","type":"Text","displayTemplate":"look","properties":{"Heading":"Hi","Body":"<p>a</p>"}}""",
            """{"nodeType":"component","key":"intro","name":"Opening","type":"Text","properties":{"Heading":"Hello","Body":"<p>a</p>","Extra":"x"}}""");

        var change = Assert.Single(CompositionChanges.Compare(Json(Before), Json(after)));

        Assert.Equal(("changed", "element", "intro"), (change.Change, change.NodeType, change.Key));
        Assert.Equal(["name", "displayTemplate", "properties.Heading", "properties.Extra"], change.Changes!.Select(c => c.Field));
        Assert.Equal(("Hi", "Hello"), (change.Changes![2].Before!.GetValue<string>(), change.Changes[2].After!.GetValue<string>()));
        Assert.Null(change.Changes[3].Before);
        Assert.Null(change.Changes[1].After);
    }

    [Fact]
    public void A_node_moved_to_another_parent_is_moved_there_and_its_old_siblings_arent()
    {
        var after = System.Text.Json.Nodes.JsonNode.Parse(Before)!.AsObject();
        var columns = after["nodes"]![0]!["nodes"]![0]!["nodes"]!.AsArray();
        var left = columns[0]!["nodes"]!.AsArray();
        var second = left[1]!;
        left.RemoveAt(1);
        columns[1]!["nodes"]!.AsArray().Insert(0, second);

        var change = Assert.Single(CompositionChanges.Compare(Json(Before), Json(after.ToJsonString())));

        Assert.Equal(("moved", "second", "right", 0), (change.Change, change.Key, change.In, change.At));
    }

    [Fact]
    public void Of_two_swapped_nodes_the_one_that_comes_first_now_is_the_one_moved_and_an_outline_block_is_a_component()
    {
        var swapped = System.Text.Json.Nodes.JsonNode.Parse(Before)!.AsObject();
        var nodes = swapped["nodes"]!.AsArray();
        var promo = nodes[1]!;
        nodes.RemoveAt(1);
        nodes.Insert(0, promo);

        var change = Assert.Single(CompositionChanges.Compare(Json(Before), Json(swapped.ToJsonString())));

        Assert.Equal(("moved", "component", "promo", "root", 0), (change.Change, change.NodeType, change.Key, change.In, change.At));
    }

    [Fact]
    public void The_compositions_own_display_template_is_a_change_of_the_composition()
    {
        var after = Before.Replace("""{"nodes":[""", """{"displayTemplate":"page","nodes":[""");

        var change = Assert.Single(CompositionChanges.Compare(Json(Before), Json(after)));

        Assert.Equal(("changed", "composition", null), (change.Change, change.NodeType, change.Key));
        Assert.Equal("displayTemplate", Assert.Single(change.Changes!).Field);
    }

    [Fact]
    public void Split_replaces_the_composition_pseudo_property_and_leaves_the_others()
    {
        var changes = new List<PropertyChange>
        {
            new("Summary", Json("\"a\""), Json("\"b\"")),
            new("composition", Json(Before), Json(Before.Replace("\"Hi\"", "\"Ho\""))),
        };

        var (rest, composition) = CompositionChanges.Split(changes);

        Assert.Equal(["Summary"], rest.Select(c => c.Property));
        Assert.Equal("intro", Assert.Single(composition!).Key);
        Assert.Null(CompositionChanges.Split([changes[0]]).Composition);
    }
}
