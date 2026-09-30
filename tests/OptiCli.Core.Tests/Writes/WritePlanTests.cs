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
            "\"action\" must be add, remove or move",
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
}
