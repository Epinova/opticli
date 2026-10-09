using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class WritePlanTests
{
    private const string ThreeSteps = """
        {"operations": [
          {"op": "create", "id": "page", "parent": 123, "type": "ArticlePage", "name": "News", "properties": {"Heading": "Hi"}},
          {"op": "block", "id": "teaser", "type": "TeaserBlock", "name": "Teaser", "for": "$page", "properties": {"Text": "t"}},
          {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "item": "$teaser", "at": 0, "display": "wide"},
          {"op": "set", "ref": "456", "properties": {"RelatedPage": "$page", "Heading": "$notAnId", "MainArea": [{"ref": "$teaser"}]}}
        ]}
        """;

    [Fact]
    public void Parses_operations_with_their_fields_and_dependencies()
    {
        var plan = WritePlan.Parse(ThreeSteps);

        Assert.Equal(4, plan.Steps.Count);
        var create = Assert.IsType<CreateOperation>(plan.Steps[0].Operation);
        Assert.Equal(("123", "ArticlePage", "News", "page"), (create.Parent, create.Type, create.Name, create.Id));
        Assert.Empty(plan.Steps[0].DependsOn);

        Assert.Equal(["page"], plan.Steps[1].DependsOn);
        var area = Assert.IsType<AreaEdit>(plan.Steps[2].Operation);
        Assert.Equal(("add", "$teaser", 0, "wide"), (area.Action, area.Item, area.At, area.Display));
        Assert.Equal(new HashSet<string> { "page", "teaser" }, plan.Steps[2].DependsOn);

        // Property values count only when they are exactly "$<plan id>".
        Assert.Equal(new HashSet<string> { "page", "teaser" }, plan.Steps[3].DependsOn);
    }

    [Fact]
    public void Composition_operations_blueprints_and_variations_are_parsed_with_their_dependencies()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "block", "id": "shared", "type": "TextElement", "name": "Shared", "parent": 3},
              {"op": "create", "id": "exp", "parent": 6, "blueprint": "Landing blueprint", "name": "Landing"},
              {"op": "composition", "ref": "$exp", "action": "add", "nodeType": "element", "in": "Left", "at": 0, "value": {"ref": "$shared", "name": "S"}},
              {"op": "composition", "ref": "$exp", "action": "set", "node": "Hero", "value": {"displayTemplate": "look"}, "variation": "Campaign", "publish": true},
              {"op": "set", "ref": "$exp", "variation": "Campaign", "properties": {"Summary": "x"}}
            ]}
            """);

        var create = Assert.IsType<CreateOperation>(plan.Steps[1].Operation);
        Assert.Equal(("", "Landing blueprint"), (create.Type, create.Blueprint));
        var add = Assert.IsType<CompositionEdit>(plan.Steps[2].Operation);
        Assert.Equal(("add", "element", "Left", 0, (string?)null), (add.Action, add.NodeType, add.Parent, add.At, add.Node));
        Assert.Equal(new HashSet<string> { "exp", "shared" }, plan.Steps[2].DependsOn);
        Assert.Equal("""{"ref":"901","name":"S"}""", ((CompositionEdit)WritePlan.Resolve(plan.Steps[2], new Dictionary<string, int> { ["exp"] = 900, ["shared"] = 901 })).Value!.ToJsonString());
        var set = Assert.IsType<CompositionEdit>(plan.Steps[3].Operation);
        Assert.Equal(("Hero", "Campaign", true), (set.Node, set.Variation, set.Publish));
        Assert.Equal("Campaign", Assert.IsType<SetOperation>(plan.Steps[4].Operation).Variation);
    }

    [Theory]
    [InlineData("""{"op": "composition", "ref": "1", "action": "rename"}""", "\"action\" must be add, remove, move or set")]
    [InlineData("""{"op": "composition", "ref": "1", "action": "add", "node": "Hero", "value": {"type": "T"}}""", "add makes a new node")]
    [InlineData("""{"op": "composition", "ref": "1", "action": "add", "nodeType": "cell", "value": {"type": "T"}}""", "must be section, row, column or element")]
    [InlineData("""{"op": "composition", "ref": "1", "action": "remove"}""", "remove needs the node")]
    [InlineData("""{"op": "composition", "ref": "1", "action": "move", "node": "Hero"}""", "move needs where to")]
    [InlineData("""{"op": "composition", "ref": "1", "action": "set", "node": "Hero", "in": "root", "value": {"name": "x"}}""", "set changes a node where it is")]
    [InlineData("""{"op": "create", "parent": "1", "name": "x"}""", "\"type\" is required (or \"blueprint\"")]
    public void Composition_operations_with_fields_their_action_doesnt_take_are_refused(string step, string message)
    {
        var refused = Assert.Throws<UsageException>(() => WritePlan.Parse($$"""{"operations": [{{step}}]}"""));

        Assert.Contains(message, refused.Message);
    }

    [Fact]
    public void Resolve_substitutes_created_ids_in_refs_and_property_values()
    {
        var plan = WritePlan.Parse(ThreeSteps);
        var created = new Dictionary<string, int> { ["page"] = 900, ["teaser"] = 901 };

        var area = Assert.IsType<AreaEdit>(WritePlan.Resolve(plan.Steps[2], created));
        Assert.Equal(("900", "901"), (area.Ref, area.Item));

        var set = Assert.IsType<SetOperation>(WritePlan.Resolve(plan.Steps[3], created));
        Assert.Equal("900", (string?)set.Properties!["RelatedPage"]);
        Assert.Equal("$notAnId", (string?)set.Properties["Heading"]);
        Assert.Equal("901", (string?)set.Properties["MainArea"]![0]!["ref"]);

        // The plan itself is untouched.
        Assert.Equal("$page", ((SetOperation)plan.Steps[3].Operation).Properties!["RelatedPage"]!.GetValue<string>());
    }

    [Fact]
    public void A_restore_takes_a_ref_and_an_optional_parent_which_may_be_planned_content()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "restore", "ref": "123"},
              {"op": "create", "id": "home", "parent": "1", "type": "ArticlePage", "name": "Home"},
              {"op": "restore", "ref": "456", "to": "$home"}
            ]}
            """);

        Assert.Equal(new RestoreOperation("123"), plan.Steps[0].Operation);
        Assert.Equal(new RestoreOperation("456", "$home"), plan.Steps[2].Operation);
        Assert.Equal(["home"], plan.Steps[2].DependsOn);
        Assert.Equal("900", Assert.IsType<RestoreOperation>(WritePlan.Resolve(plan.Steps[2], new Dictionary<string, int> { ["home"] = 900 })).To);
        Assert.Contains("unknown field \"parent\"", Assert.Throws<UsageException>(() => WritePlan.Parse("""{"operations": [{"op": "restore", "ref": "1", "parent": "2"}]}""")).Message);
    }

    [Fact]
    public void An_area_step_adds_a_new_inline_block_by_its_type_with_values_and_name()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "1", "type": "ArticlePage", "name": "n"},
              {"op": "area", "ref": "$page", "property": "MainArea", "action": "add", "type": "TeaserBlock", "values": {"Text": "Hi", "Link": "$page"}, "name": "Intro", "at": 0}
            ]}
            """);

        var add = Assert.IsType<AreaEdit>(plan.Steps[1].Operation);
        Assert.Equal(("TeaserBlock", "Intro", null), (add.Type, add.Name, add.Item));
        Assert.Equal("""{"Text":"Hi","Link":"$page"}""", add.Values!.ToJsonString());
        var resolved = Assert.IsType<AreaEdit>(WritePlan.Resolve(plan.Steps[1], new Dictionary<string, int> { ["page"] = 900 }));
        Assert.Equal("""{"Text":"Hi","Link":"900"}""", resolved.Values!.ToJsonString());
    }

    [Theory]
    [InlineData("""{"op": "area", "ref": "1", "property": "MainArea", "action": "add", "item": "2", "type": "TeaserBlock"}""", "give \"item\" (a shared block) or \"type\"")]
    [InlineData("""{"op": "area", "ref": "1", "property": "MainArea", "action": "add", "values": {"Text": "x"}}""", "give its \"type\" too")]
    [InlineData("""{"op": "area", "ref": "1", "property": "MainArea", "action": "remove", "index": 0, "type": "TeaserBlock"}""", "are for adding (or setting) an inline block")]
    [InlineData("""{"op": "area", "ref": "1", "property": "MainArea", "action": "set", "values": {"Text": "x"}}""", "takes the inline block's \"index\"")]
    public void An_inline_area_step_with_the_wrong_fields_is_a_plan_problem(string step, string problem) =>
        Assert.Contains(problem, Assert.Throws<UsageException>(() => WritePlan.Parse($$"""{"operations": [{{step}}]}""")).Message);

    [Fact]
    public void Every_shape_problem_is_reported_at_once()
    {
        const string json = """
            {"operations": [
              {"op": "set", "ref": "$later", "properties": {"Heading": "x"}},
              {"op": "create", "id": "later", "parent": "1", "type": "ArticlePage", "name": "n", "colour": "red"},
              {"op": "area", "ref": "1", "property": "MainArea", "action": "sideways"},
              {"op": "explode"},
              {"op": "publish"},
              {"op": "set", "ref": "1", "publish": "yes", "properties": []},
              {"op": "delete", "ref": "$nothing"},
              {"op": "create", "id": "later", "parent": "1", "type": "T", "name": "dup"},
              {"op": "block", "type": "TeaserBlock", "name": "b"},
              {"op": "create", "id": "1bad", "parent": "1", "type": "T", "name": "n"},
              {"op": "set", "ref": "1", "id": "x"}
            ]}
            """;

        var error = Assert.Throws<UsageException>(() => WritePlan.Parse(json));

        foreach (var expected in new[]
        {
            "'$later' is created by a later operation",
            "unknown field \"colour\"",
            "\"action\" must be add, remove, move or set",
            "\"op\" must be one of",
            "(publish): \"ref\" is required",
            "\"publish\" must be true or false",
            "\"properties\" must be an object",
            "'$nothing' is not the id of an earlier",
            "id 'later' is already used by operations[1]",
            "give exactly one of \"for\" and \"parent\"",
            "id '1bad' must start with a letter",
            "(set): unknown field \"id\"",
        })
        {
            Assert.Contains(expected, error.Message);
        }
        Assert.NotNull(error.Details);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"operations": []}""")]
    [InlineData("""{"operations": [{"op": "delete", "ref": "1"}], "extra": 1}""")]
    [InlineData("not json")]
    public void The_plan_must_be_an_object_with_operations(string json)
    {
        Assert.Throws<UsageException>(() => WritePlan.Parse(json));
    }

    [Fact]
    public void A_property_value_naming_a_later_step_is_an_error()
    {
        const string json = """
            {"operations": [
              {"op": "set", "ref": "1", "properties": {"RelatedPage": "$page"}},
              {"op": "create", "id": "page", "parent": "1", "type": "ArticlePage", "name": "n"}
            ]}
            """;

        var error = Assert.Throws<UsageException>(() => WritePlan.Parse(json));
        Assert.Contains("'$page' is created by a later operation", error.Message);
    }

    [Fact]
    public void With_publish_turns_on_publishing_only_for_ops_that_can_publish()
    {
        Assert.True(((SetOperation)new SetOperation("1").WithPublish()).Publish);
        Assert.True(((AreaEdit)new AreaEdit("1", "MainArea", "add", "2").WithPublish()).Publish);
        var create = new CreateOperation("1", "T", "n") { Id = "keep" }.WithPublish();
        Assert.True(((CreateOperation)create).Publish);
        Assert.Equal("keep", create.Id);
        var move = new MoveOperation("1", "2");
        Assert.Same(move, move.WithPublish());
    }

    [Fact]
    public void Parses_access_operations_and_keeps_role_names_out_of_ref_mapping()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "root", "parent": "200", "type": "ArticlePage", "name": "Members"},
              {"op": "access", "ref": "$root", "grant": {"Authenticated": "Read", "$root": ["Read", "Edit"]}, "revoke": ["Everyone"], "breakInheritance": true},
              {"op": "access", "ref": "123", "grantUsers": {"someone@example.com": "FullAccess"}, "revoke": "Everyone", "allowUnknownRole": true}
            ]}
            """);

        var access = Assert.IsType<AccessOperation>(plan.Steps[1].Operation);
        Assert.Equal("Read", access.Grant!["Authenticated"]);
        Assert.Equal("Read,Edit", access.Grant["$root"]);
        Assert.Equal(["Everyone"], access.Revoke);
        Assert.True(access.BreakInheritance);
        Assert.Equal(["root"], plan.Steps[1].DependsOn);

        var resolved = Assert.IsType<AccessOperation>(WritePlan.Resolve(plan.Steps[1], new Dictionary<string, int> { ["root"] = 900 }));
        Assert.Equal("900", resolved.Ref);
        Assert.True(resolved.Grant!.ContainsKey("$root"));

        var users = Assert.IsType<AccessOperation>(plan.Steps[2].Operation);
        Assert.Equal("FullAccess", users.GrantUsers!["someone@example.com"]);
        Assert.Equal(["Everyone"], users.Revoke);
        Assert.True(users.AllowUnknownRole);
    }

    [Fact]
    public void Access_levels_and_shapes_are_checked_when_the_plan_is_parsed()
    {
        var error = Assert.Throws<UsageException>(() => WritePlan.Parse("""
            {"operations": [
              {"op": "access", "ref": "123", "grant": {"Authenticated": "Raed", "Everyone": 1}, "revoke": [1]}
            ]}
            """));

        Assert.Contains("'Raed' is not an access level", error.Message);
        Assert.Contains("\"grant\".Everyone must be levels", error.Message);
        Assert.Contains("\"revoke\" must be an array of strings", error.Message);
    }

    [Fact]
    public void Parses_upload_operations_whose_ids_later_steps_can_use()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "upload", "id": "pdf", "file": "files/q1.pdf", "parent": "3", "name": "Q1", "type": "PdfFile", "properties": {"Copyright": "x"}}
            ]}
            """);

        var upload = Assert.IsType<UploadOperation>(plan.Steps[0].Operation);
        Assert.Equal(("files/q1.pdf", "3", null, "Q1", "PdfFile", "pdf"), (upload.File, upload.Parent, upload.For, upload.Name, upload.Type, upload.Id));
        Assert.True(upload.WithPublish() is UploadOperation { Publish: true });

        var chained = WritePlan.Parse("""
            {"operations": [
              {"op": "upload", "id": "pdf", "file": "q1.pdf", "for": "123"},
              {"op": "set", "ref": "456", "properties": {"Files": ["$pdf"]}}
            ]}
            """);
        Assert.Equal(["pdf"], chained.Steps[1].DependsOn);
    }

    [Fact]
    public void An_upload_needs_a_file_and_exactly_one_of_for_and_parent()
    {
        var error = Assert.Throws<UsageException>(() => WritePlan.Parse("""
            {"operations": [
              {"op": "upload", "file": "a.pdf"},
              {"op": "upload", "parent": "3", "for": "4"}
            ]}
            """));

        Assert.Contains("operations[0] (upload): give exactly one of \"for\" and \"parent\"", error.Message);
        Assert.Contains("operations[1] (upload): \"file\" is required", error.Message);
    }

    [Fact]
    public void A_guid_namespace_gives_every_creating_step_a_guid_from_its_id()
    {
        var plan = WritePlan.Parse("""
            {"guidNamespace": "6f1c2b3a-9d4e-4f5a-8b7c-1d2e3f4a5b6c", "operations": [
              {"op": "create", "id": "root", "parent": "1", "type": "ArticlePage", "name": "N"},
              {"op": "block", "id": "text", "type": "TextBlock", "name": "T", "for": "$root", "guid": "0b1c2d3e-0000-4000-8000-000000000001"},
              {"op": "set", "ref": "$root", "properties": {"Heading": "x"}}
            ]}
            """);

        var ns = Guid.Parse("6f1c2b3a-9d4e-4f5a-8b7c-1d2e3f4a5b6c");
        Assert.Equal(ns, plan.GuidNamespace);
        Assert.Equal(StableGuids.Create(ns, "root"), plan.Steps[0].Operation.ContentGuid);
        Assert.Equal(Guid.Parse("0b1c2d3e-0000-4000-8000-000000000001"), plan.Steps[1].Operation.ContentGuid);
        Assert.Null(plan.Steps[2].Operation.ContentGuid);
        // Resolving $ids keeps the GUID.
        Assert.Equal(plan.Steps[1].Operation.ContentGuid, WritePlan.Resolve(plan.Steps[1], new Dictionary<string, int> { ["root"] = 5 }).ContentGuid);
    }

    [Fact]
    public void Without_a_namespace_only_explicit_guids_are_set()
    {
        var plan = WritePlan.Parse("""{"operations": [{"op": "create", "id": "a", "parent": "1", "type": "ArticlePage", "name": "N"}]}""");

        Assert.Null(plan.GuidNamespace);
        Assert.Null(plan.Steps[0].Operation.ContentGuid);
    }

    [Fact]
    public void Guid_problems_are_reported_with_the_rest()
    {
        var error = Assert.Throws<UsageException>(() => WritePlan.Parse("""
            {"guidNamespace": "not-a-guid", "operations": [
              {"op": "create", "parent": "1", "type": "ArticlePage", "name": "N", "guid": "nope"},
              {"op": "upload", "file": "a.pdf", "parent": "1", "guid": "0b1c2d3e-0000-4000-8000-000000000001"},
              {"op": "block", "type": "TextBlock", "name": "T", "parent": "1", "guid": "0b1c2d3e-0000-4000-8000-000000000001"},
              {"op": "set", "ref": "1", "guid": "0b1c2d3e-0000-4000-8000-000000000002", "properties": {"A": "b"}}
            ]}
            """));

        Assert.Contains("\"guidNamespace\" must be a GUID", error.Message);
        Assert.Contains("\"guid\" 'nope' is not a GUID", error.Message);
        Assert.Contains("operations[1, 2] all have GUID 0b1c2d3e-0000-4000-8000-000000000001", error.Message);
        Assert.Contains("operations[3] (set): unknown field \"guid\"", error.Message);
    }

    [Fact]
    public void Steps_that_publish_can_confirm_other_peoples_drafts()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "set", "ref": "1", "properties": {"A": "b"}, "publish": true, "includeDraft": true},
              {"op": "publish", "ref": "2", "includeDraft": true},
              {"op": "translate", "ref": "3", "lang": "sv", "publish": true},
              {"op": "area", "ref": "4", "property": "MainArea", "action": "add", "item": "5", "publish": true, "includeDraft": false}
            ]}
            """);

        Assert.Equal([true, true, false, false], plan.Steps.Select(s => s.Operation.IncludeDraft));
        // Kept through $id resolution and apply --publish.
        Assert.True(WritePlan.Resolve(plan.Steps[0], new Dictionary<string, int>()).WithPublish().IncludeDraft);

        var error = Assert.Throws<UsageException>(() => WritePlan.Parse("""
            {"operations": [{"op": "move", "ref": "1", "to": "2", "includeDraft": true}, {"op": "set", "ref": "1", "properties": {"A": "b"}, "includeDraft": "yes"}]}
            """));
        Assert.Contains("operations[0] (move): unknown field \"includeDraft\"", error.Message);
        Assert.Contains("operations[1] (set): \"includeDraft\" must be true or false", error.Message);
    }

    [Fact]
    public void With_a_namespace_a_creating_step_needs_an_id()
    {
        var error = Assert.Throws<UsageException>(() => WritePlan.Parse("""
            {"guidNamespace": "6f1c2b3a-9d4e-4f5a-8b7c-1d2e3f4a5b6c", "operations": [
              {"op": "create", "parent": "1", "type": "ArticlePage", "name": "N"}
            ]}
            """));

        Assert.Contains("operations[0] (create): needs an \"id\"", error.Message);
    }

    [Theory]
    [InlineData("""{"operations": [{"op": "delete", "ref": "1"}], "extra": 1}""")]
    [InlineData("""{"guidNamespace": "6f1c2b3a-9d4e-4f5a-8b7c-1d2e3f4a5b6c"}""")]
    public void Only_operations_and_a_guid_namespace_are_allowed_at_the_top(string json)
    {
        Assert.Contains("guidNamespace", Assert.Throws<UsageException>(() => WritePlan.Parse(json)).Message);
    }
}
