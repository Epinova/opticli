using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

public class FromVersionTests
{
    [Theory]
    [InlineData("published", null, null, "published")]
    [InlineData(" Published ", null, null, "published")]
    [InlineData("456", 456, null, "456")]
    [InlineData("123_456", 456, 123, "456")]
    public void From_is_the_published_version_or_one_version(string text, int? version, int? contentId, string request)
    {
        var from = FromVersion.Parse(text, "--from");

        Assert.Equal((version, contentId, request), (from.Version, from.ContentId, from.Request));
        Assert.Equal(version is null, from.IsPublished);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("123_")]
    [InlineData("1_2_3")]
    public void Anything_else_is_a_usage_error(string text)
    {
        var error = Assert.Throws<UsageException>(() => FromVersion.Parse(text, "--from"));

        Assert.Equal($"--from '{text}' is not a version: give published, or a version (456 or 123_456), as `opticli versions` shows them.", error.Message);
    }

    [Fact]
    public void From_needs_the_content_written_and_a_ref_without_a_version()
    {
        FromVersion.Parse("123_456", "--from").Check(123, refVersion: null);
        FromVersion.Published.Check(123, refVersion: null);

        var other = Assert.Throws<UsageException>(() => FromVersion.Parse("124_456", "--from").Check(123, refVersion: null));
        Assert.Equal("--from 124_456 is a version of 124, not of 123.", other.Message);
        var both = Assert.Throws<UsageException>(() => FromVersion.Published.Check(123, refVersion: 450));
        Assert.Equal("The ref names version 450 and --from says published; give one of them.", both.Message);
        Assert.Contains("123_450 bases the change on that version, which must be the latest", both.Hint);
    }

    [Fact]
    public void Plans_take_from_as_published_a_number_or_a_version_ref()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "set", "ref": "123", "from": "published", "name": "x"},
              {"op": "set", "ref": "123", "from": 456, "baseVersion": 460, "name": "y"},
              {"op": "area", "ref": "123", "property": "MainArea", "action": "remove", "index": 0, "from": "123_456"}
            ]}
            """);

        Assert.Equal(FromVersion.Published, Assert.IsType<SetOperation>(plan.Steps[0].Operation).From);
        var pinned = Assert.IsType<SetOperation>(plan.Steps[1].Operation);
        Assert.Equal((new FromVersion(456), 460), (pinned.From, pinned.BaseVersion));
        Assert.Equal(new FromVersion(456, 123), Assert.IsType<AreaEdit>(plan.Steps[2].Operation).From);
    }

    [Fact]
    public void A_plan_reports_a_from_that_is_no_version_or_on_content_it_creates()
    {
        var error = Assert.Throws<UsageException>(() => WritePlan.Parse("""
            {"operations": [
              {"op": "set", "ref": "123", "from": "yesterday", "name": "x"},
              {"op": "create", "id": "page", "parent": "100", "type": "ArticlePage", "name": "Page"},
              {"op": "set", "ref": "$page", "from": "published", "name": "y"},
              {"op": "publish", "ref": "123", "from": "published"}
            ]}
            """));

        var problems = Assert.IsType<List<string>>(error.Details!.GetType().GetProperty("problems")!.GetValue(error.Details));
        Assert.Equal(3, problems.Count);
        Assert.Contains("operations[0] (set): \"from\" must be published, or a version (456 or 123_456).", problems);
        Assert.Contains(problems, p => p.StartsWith("operations[2] (set): \"from\" bases a change on a version of existing content, and '$page' is created by the plan", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("operations[3] (publish): unknown field \"from\"", StringComparison.Ordinal));
    }

    private static readonly DateTime Monday = new(2025, 1, 27, 9, 0, 0, DateTimeKind.Utc);

    private static WriteOutput Output(bool dryRun = false, bool published = false, params LeftOutVersion[] leftOut) =>
        new("123", dryRun ? "123_450" : "123_460", null, "ArticlePage", "News", "en", published ? "published" : "checkedOut", "5",
            Saved: !dryRun, Published: published, DryRun: dryRun, Valid: true, BaseVersion: "123_450", Changes: [], Validation: null)
        { LeftOut = leftOut.Length > 0 ? leftOut : null };

    private static readonly LeftOutVersion TheirDraft = new("123_458", "checkedOut", "editor@example.com", Monday, Primary: true);

    private static readonly LeftOutVersion OurDraft = new("123_459", "checkedOut", "opticli", Monday.AddHours(1), Primary: false);

    [Fact]
    public void A_draft_from_the_published_version_says_what_it_leaves_out_and_what_edit_mode_opens()
    {
        var warning = LeftOutVersions.Warning(Output(leftOut: TheirDraft), FromVersion.Published, publishes: false);

        Assert.Equal(
            "123_460 is based on 123_450 (the published version), so it leaves out the newer version 123_458 (checkedOut, saved by editor@example.com 2025-01-27 09:00:00Z): "
            + "it stays as it is, without this change. Edit mode now opens 123_460 instead of 123_458 (still in its version list).",
            warning);
    }

    [Fact]
    public void A_published_change_says_publishing_a_left_out_draft_later_would_drop_it()
    {
        var warning = LeftOutVersions.Warning(Output(published: true, leftOut: [OurDraft with { SavedBy = "" }, TheirDraft]), new FromVersion(450), publishes: true);

        Assert.Equal(
            "123_460 is published, based on 123_450, so it leaves out the newer versions 123_459 (checkedOut, saved without a user name 2025-01-27 10:00:00Z), "
            + "123_458 (checkedOut, saved by editor@example.com 2025-01-27 09:00:00Z): they stay as they are, without this change, "
            + "and publishing one of them later would put its changes live without this one. Edit mode now opens the published version.",
            warning);
    }

    [Fact]
    public void A_dry_run_says_the_same_as_what_would_happen()
    {
        Assert.Equal(
            "Based on 123_450 (the published version), this would leave out the newer version 123_458 (checkedOut, saved by editor@example.com 2025-01-27 09:00:00Z): "
            + "it would stay as it is, without this change, and edit mode would open the new version instead of 123_458 (still in its version list).",
            LeftOutVersions.Warning(Output(dryRun: true, leftOut: TheirDraft), FromVersion.Published, publishes: false));
        Assert.EndsWith("and publishing it later would put its changes live without this one.",
            LeftOutVersions.Warning(Output(dryRun: true, leftOut: TheirDraft), FromVersion.Published, publishes: true));
    }

    [Fact]
    public void Nothing_left_out_is_no_warning_and_long_lists_are_counted()
    {
        Assert.Null(LeftOutVersions.Warning(Output(), FromVersion.Published, publishes: false));

        var many = Enumerable.Range(1, 7).Select(i => TheirDraft with { Version = $"123_{450 + i}" }).ToList();
        Assert.EndsWith(" and 2 more", LeftOutVersions.Describe(many));
    }

    [Fact]
    public void The_conflict_hint_points_to_from_and_keeps_it_when_given()
    {
        Assert.Contains("to base it on an older one and leave the newer ones out, use --from <version> (or --from published)", WriteExecutor.ConflictHint("123_461", null));
        Assert.Contains("--from still bases the change on version 450", WriteExecutor.ConflictHint("123_461", new FromVersion(450)));
        Assert.Contains("--from still bases the change on the published version", WriteExecutor.ConflictHint("123_461", FromVersion.Published));
    }

    [Fact]
    public void A_set_or_area_operation_carries_from_and_reads_its_draft_request_value()
    {
        var set = new SetOperation("123", Name: "x") { From = FromVersion.Published };

        Assert.Equal(DraftRequest.FromPublished, set.From!.Request);
        Assert.Equal(set, set.MapRefs(r => r));
        Assert.Equal(FromVersion.Published, ((SetOperation)set.WithPublish()).From);
    }
}
