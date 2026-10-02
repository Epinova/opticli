using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

public class PlanMasterFirstTests
{
    private static MasterOrder? Order(WritePlan plan, int index, IReadOnlyDictionary<int, PlanTarget>? targets = null, IReadOnlyDictionary<string, string>? masters = null) =>
        PlanSimulation.MasterFirst(plan.Steps[index], plan.Steps, targets ?? new Dictionary<int, PlanTarget>(), masters);

    private static PlanTarget Existing(string language, bool masterPublished, bool branchExists = true) =>
        new(123, language, "no", branchExists, Versioned: false, masterPublished);

    [Fact]
    public void A_branch_published_before_its_master_is_refused_with_the_step_that_publishes_the_master()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Side", "lang": "no"},
              {"op": "translate", "ref": "$page", "lang": "en", "name": "Page"},
              {"op": "translate", "ref": "$page", "lang": "sv", "name": "Sida"},
              {"op": "publish", "ref": "$page", "lang": "en"},
              {"op": "publish", "ref": "$page", "lang": "sv"},
              {"op": "publish", "ref": "$page"}
            ]}
            """);

        var refused = Assert.Throws<ContentValidationException>(() => Order(plan, 3));

        Assert.Equal("Dry run: operation 3 publishes the 'en' branch of $page before its master language ('no') is published; the CMS refuses that.", refused.Message);
        Assert.Equal("Move operation 5, which publishes 'no', before operation 3.", refused.Hint);
        Assert.Equal(AgentErrorReasons.MasterNotPublished, Assert.IsType<AgentErrorDetails>(refused.Details).Reason);
        Assert.Throws<ContentValidationException>(() => Order(plan, 4));
        Assert.Null(Order(plan, 5));
        Assert.Null(Order(plan, 1));
    }

    [Fact]
    public void The_same_plan_with_the_master_published_first_passes()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Side", "lang": "no"},
              {"op": "translate", "ref": "$page", "lang": "en", "name": "Page"},
              {"op": "translate", "ref": "$page", "lang": "sv", "name": "Sida"},
              {"op": "publish", "ref": "$page"},
              {"op": "publish", "ref": "$page", "lang": "en"},
              {"op": "publish", "ref": "$page", "lang": "sv"}
            ]}
            """);

        Assert.Null(Order(plan, 3));
        var en = Order(plan, 4)!;
        Assert.Equal(3, en.PublishedBy);
        Assert.Contains("operation 3 publishes it first", en.Note);
        Assert.Equal(3, Order(plan, 5)!.PublishedBy);
    }

    [Fact]
    public void A_create_that_publishes_publishes_the_master_for_a_later_translate()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Side", "lang": "no", "publish": true},
              {"op": "translate", "ref": "$page", "lang": "en", "name": "Page", "publish": true}
            ]}
            """);

        Assert.Equal(0, Order(plan, 1)!.PublishedBy);
    }

    [Fact]
    public void A_translate_that_publishes_before_any_master_publish_is_refused()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Side", "lang": "no"},
              {"op": "translate", "ref": "$page", "lang": "en", "name": "Page", "publish": true}
            ]}
            """);

        var refused = Assert.Throws<ContentValidationException>(() => Order(plan, 1));
        Assert.Equal("Add a publish of the master branch before operation 1, or leave out \"publish\": true.", refused.Hint);
    }

    [Fact]
    public void Existing_content_starts_from_whether_the_database_has_its_master_published()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "publish", "ref": "123", "lang": "en"},
              {"op": "publish", "ref": "123"}
            ]}
            """);
        Dictionary<int, PlanTarget> Targets(bool masterPublished) => new() { [0] = Existing("en", masterPublished), [1] = Existing("no", masterPublished) };

        var refused = Assert.Throws<ContentValidationException>(() => Order(plan, 0, Targets(masterPublished: false)));
        Assert.Contains("the 'en' branch of 123 before its master language ('no')", refused.Message);
        Assert.Equal("Move operation 1, which publishes 'no', before operation 0.", refused.Hint);
        Assert.Null(Order(plan, 0, Targets(masterPublished: true)));

        var translate = WritePlan.Parse("""{"operations": [{"op": "translate", "ref": "123", "lang": "sv", "publish": true}]}""");
        Assert.Throws<ContentValidationException>(() => Order(translate, 0, new Dictionary<int, PlanTarget> { [0] = Existing("sv", false, branchExists: false) }));
        Assert.Null(Order(translate, 0, new Dictionary<int, PlanTarget> { [0] = Existing("sv", true, branchExists: false) }));
    }

    [Fact]
    public void An_explicit_lang_equal_to_the_master_counts_as_the_master()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Side", "lang": "no"},
              {"op": "translate", "ref": "$page", "lang": "en", "name": "Page"},
              {"op": "set", "ref": "$page", "lang": "NO", "properties": {"Heading": "Hei"}, "publish": true},
              {"op": "publish", "ref": "$page", "lang": "en"}
            ]}
            """);

        Assert.Null(Order(plan, 2));
        Assert.Equal(2, Order(plan, 3)!.PublishedBy);
    }

    [Fact]
    public void Planned_content_without_a_lang_has_its_parents_master()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Side"},
              {"op": "translate", "ref": "$page", "lang": "en", "name": "Page"},
              {"op": "publish", "ref": "$page", "lang": "en"},
              {"op": "publish", "ref": "$page", "lang": "no"}
            ]}
            """);
        var masters = new Dictionary<string, string> { ["page"] = "no" };

        Assert.Equal("Move operation 3, which publishes 'no', before operation 2.", Assert.Throws<ContentValidationException>(() => Order(plan, 2, masters: masters)).Hint);
        // Without knowing the master, only a publish without a lang is known to publish it.
        Assert.Null(Order(plan, 2));
    }

    [Fact]
    public void A_scheduled_publish_or_review_request_of_a_branch_only_warns()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Side", "lang": "no"},
              {"op": "translate", "ref": "$page", "lang": "en", "name": "Page"},
              {"op": "publish", "ref": "$page", "lang": "en", "publishAt": "2030-01-01T08:00:00Z"},
              {"op": "set", "ref": "$page", "lang": "en", "name": "Page 2", "requestApproval": true},
              {"op": "publish", "ref": "123", "lang": "en", "publishAt": "2030-01-01T08:00:00Z"}
            ]}
            """);
        var targets = new Dictionary<int, PlanTarget> { [4] = Existing("en", masterPublished: false) };

        var scheduled = Order(plan, 2, targets)!;
        Assert.Null(scheduled.PublishedBy);
        Assert.Contains("this scheduled publish fails when it comes due", scheduled.Note);
        Assert.Contains("can't be published once it is approved", Order(plan, 3, targets)!.Note);
        // On existing content the step's own dry run warns.
        Assert.Null(Order(plan, 4, targets));
    }

    [Fact]
    public void A_scheduled_publish_or_review_request_of_the_master_does_not_publish_it_first()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Side", "lang": "no"},
              {"op": "translate", "ref": "$page", "lang": "en", "name": "Page"},
              {"op": "publish", "ref": "$page", "publishAt": "2030-01-01T08:00:00Z"},
              {"op": "publish", "ref": "$page", "requestApproval": true},
              {"op": "publish", "ref": "$page", "lang": "en"}
            ]}
            """);

        var refused = Assert.Throws<ContentValidationException>(() => Order(plan, 4));
        Assert.Equal("Add a publish of the master branch before operation 4, or remove this publish.", refused.Hint);
    }

    [Fact]
    public void Apply_publish_in_plan_order_passes()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Side", "lang": "no"},
              {"op": "translate", "ref": "$page", "lang": "en", "name": "Page"},
              {"op": "set", "ref": "$page", "lang": "en", "properties": {"Heading": "Hello"}}
            ]}
            """);
        var steps = plan.Steps.Select(s => s with { Operation = s.Operation.WithPublish() }).ToList();

        Assert.Equal(0, PlanSimulation.MasterFirst(steps[1], steps, new Dictionary<int, PlanTarget>())!.PublishedBy);
        Assert.Equal(0, PlanSimulation.MasterFirst(steps[2], steps, new Dictionary<int, PlanTarget>())!.PublishedBy);
    }

    [Fact]
    public void A_set_or_publish_of_another_branch_of_planned_content_is_not_simulated_as_publishing_the_master()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Side", "lang": "no"},
              {"op": "translate", "ref": "$page", "lang": "en", "name": "Page"},
              {"op": "publish", "ref": "$page", "lang": "en"},
              {"op": "set", "ref": "$page", "lang": "en", "properties": {"Heading": "Hello"}, "publish": true},
              {"op": "publish", "ref": "$page", "lang": "no"}
            ]}
            """);
        var none = new Dictionary<string, int>();

        Assert.Null(PlanSimulation.For(plan.Steps[2], plan.Steps, none, updateExisting: false));
        Assert.Null(PlanSimulation.For(plan.Steps[3], plan.Steps, none, updateExisting: false));
        var master = Assert.IsType<CreateOperation>(PlanSimulation.For(plan.Steps[4], plan.Steps, none, updateExisting: false)!.Operation);
        Assert.Equal(("no", true), (master.Lang, master.Publish));
    }
}
