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
