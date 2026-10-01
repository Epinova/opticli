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
    public void A_replaced_file_is_undone_like_any_new_version()
    {
        var output = new WriteOutput("123", "123_13", null, "PdfFile", "Report", null, "checkedOut", "5", Saved: true, Published: false, DryRun: false, Valid: true,
            BaseVersion: "123_12", Changes: [], Validation: null);

        Assert.StartsWith("123_13 is an unpublished draft", UndoHints.For(new UploadOperation("b.pdf") { Replace = "123" }, output));
    }
}
