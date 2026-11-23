using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class ReplaceMediaTests
{
    [Fact]
    public void Plans_replace_a_file_by_ref_or_plan_id_without_a_place_or_type()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "upload", "id": "doc", "file": "a.pdf", "parent": "45"},
              {"op": "upload", "file": "b.pdf", "replace": "$doc", "publish": true}]}
            """);
        Assert.Equal("$doc", Assert.IsType<UploadOperation>(plan.Steps[1].Operation).Replace);
        Assert.Equal(["doc"], plan.Steps[1].DependsOn);

        var invalid = Assert.Throws<UsageException>(() => WritePlan.Parse("""{"operations": [{"op": "upload", "file": "b.pdf", "replace": "12", "parent": "45"}]}"""));
        Assert.Contains("takes no \"for\", \"parent\"", invalid.Message);
    }

    [Fact]
    public void A_plan_with_a_guid_namespace_replaces_a_file_without_an_id()
    {
        var plan = WritePlan.Parse("""
            {"guidNamespace": "3f6d2a9e-6c1b-4f7e-9a51-0d2b8c4e7f10", "operations": [
              {"op": "upload", "id": "doc", "file": "a.pdf", "parent": "45"},
              {"op": "upload", "file": "b.pdf", "replace": "$doc"},
              {"op": "upload", "file": "c.pdf", "replace": "12"}]}
            """);

        Assert.NotNull(plan.Steps[0].Operation.ContentGuid);
        Assert.Null(plan.Steps[1].Operation.ContentGuid);
        Assert.Null(plan.Steps[2].Operation.ContentGuid);
        Assert.Equal(["doc"], WritePlan.FixedGuids(plan.Steps).Keys);
    }

    [Fact]
    public void A_publish_after_a_new_file_for_existing_media_is_dry_run_as_that_upload_published()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "upload", "file": "new.png", "replace": "456"},
              {"op": "publish", "ref": "456", "publishAt": "2099-01-01T00:00:00Z"}]}
            """);
        var targets = new Dictionary<int, PlanTarget> { [0] = new(456, null, null, true, false), [1] = new(456, null, null, true, false) };

        var simulation = PlanSimulation.OnExisting(plan.Steps[1], plan.Steps, targets, new Dictionary<string, int>(), updateExisting: false)!;

        // Not the media's published version alone, which a publish would find nothing new in.
        var upload = Assert.IsType<UploadOperation>(simulation.Operation);
        Assert.True(upload is { Replace: "456", Publish: false } && upload.PublishAt!.Value.Year == 2099);
        Assert.Equal([0], simulation.Folded!.Select(s => s.Index));
    }

    [Fact]
    public void Replacing_planned_media_is_not_simulated_as_new_media()
    {
        var plan = WritePlan.Parse("""
            {"guidNamespace": "3f6d2a9e-6c1b-4f7e-9a51-0d2b8c4e7f10", "operations": [
              {"op": "upload", "id": "doc", "file": "a.pdf", "parent": "45"},
              {"op": "upload", "file": "b.pdf", "replace": "$doc"}]}
            """);

        Assert.NotNull(PlanSimulation.For(plan.Steps[0], plan.Steps, new Dictionary<string, int>(), updateExisting: false));
        Assert.Null(PlanSimulation.For(plan.Steps[1], plan.Steps, new Dictionary<string, int>(), updateExisting: false));
    }

    [Fact]
    public void Replacing_existing_media_that_refers_to_planned_content_is_still_simulated()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "upload", "id": "img", "file": "a.png", "parent": "45"},
              {"op": "upload", "id": "doc", "file": "a.pdf", "parent": "45"},
              {"op": "upload", "file": "v.mp4", "replace": "51", "properties": {"PreviewImage": "$img"}},
              {"op": "upload", "file": "b.pdf", "replace": "$doc", "properties": {"Description": "$img"}}]}
            """);

        // Existing media: the dry run checks the new file against it, leaving out what refers to planned content.
        var existingMedia = PlanSimulation.For(plan.Steps[2], plan.Steps, new Dictionary<string, int>(), updateExisting: false);
        Assert.NotNull(existingMedia);
        Assert.Equal("51", Assert.IsType<UploadOperation>(existingMedia.Operation).Replace);
        Assert.Equal(["PreviewImage"], existingMedia.Unchecked);

        // Planned media that --update-existing found: replaced by its ref; not found yet: checked when the plan runs.
        var found = PlanSimulation.For(plan.Steps[3], plan.Steps, new Dictionary<string, int> { ["doc"] = 291 }, updateExisting: true);
        Assert.NotNull(found);
        Assert.Equal("291", Assert.IsType<UploadOperation>(found.Operation).Replace);
        Assert.Null(PlanSimulation.For(plan.Steps[3], plan.Steps, new Dictionary<string, int>(), updateExisting: false));
    }

    [Theory]
    [InlineData("\"id\": \"doc2\"")]
    [InlineData("\"guid\": \"9d1c0b6e-2f4a-4c55-8a37-1e9f0d6b2a44\"")]
    public void A_replacing_upload_still_takes_no_id_or_guid_with_a_guid_namespace(string field)
    {
        var invalid = Assert.Throws<UsageException>(() => WritePlan.Parse($$"""
            {"guidNamespace": "3f6d2a9e-6c1b-4f7e-9a51-0d2b8c4e7f10", "operations": [
              {"op": "upload", "file": "b.pdf", "replace": "12", {{field}}}]}
            """));

        Assert.Contains("takes no \"for\", \"parent\", \"type\", \"id\" or \"guid\"", invalid.Message);
        Assert.DoesNotContain("needs an \"id\"", invalid.Message);
    }

    [Fact]
    public void A_new_upload_still_needs_an_id_with_a_guid_namespace()
    {
        var invalid = Assert.Throws<UsageException>(() => WritePlan.Parse("""
            {"guidNamespace": "3f6d2a9e-6c1b-4f7e-9a51-0d2b8c4e7f10", "operations": [
              {"op": "upload", "file": "a.pdf", "parent": "45"}]}
            """));

        Assert.Contains("needs an \"id\" (or a \"guid\")", invalid.Message);
    }

    [Fact]
    public void A_replaced_file_is_undone_like_any_new_version()
    {
        var output = new WriteOutput("123", "123_13", null, "PdfFile", "Report", null, "checkedOut", "5", Saved: true, Published: false, DryRun: false, Valid: true,
            BaseVersion: "123_12", Changes: [], Validation: null);

        Assert.StartsWith("123_13 is an unpublished draft", UndoHints.For(new UploadOperation("b.pdf") { Replace = "123" }, output));
    }
}
