using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Tests.Writes;

public class PlanFilesTests
{
    [Fact]
    public void At_values_become_the_file_text_at_any_depth()
    {
        using var root = new TempDirectory();
        root.Write("plan/texts/body.html", "<p>Hei</p>");
        root.Write("plan/texts/hero.txt", "Velkommen");
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "page", "parent": "1", "type": "ArticlePage", "name": "N",
               "properties": {"MainBody": "@texts/body.html", "Hero": {"Heading": "@texts/hero.txt"}, "Mail": "@@olav", "Links": [{"href": "1", "text": "@texts/hero.txt"}]}},
              {"op": "set", "ref": "$page", "properties": {"Intro": "plain"}}
            ]}
            """);

        var steps = PlanFiles.Resolve(plan.Steps, root.Combine("plan"), allowOutside: false);

        var create = Assert.IsType<CreateOperation>(steps[0].Operation);
        Assert.Equal("<p>Hei</p>", (string?)create.Properties!["MainBody"]);
        Assert.Equal("Velkommen", (string?)create.Properties["Hero"]!["Heading"]);
        Assert.Equal("@olav", (string?)create.Properties["Mail"]);
        Assert.Equal("Velkommen", (string?)create.Properties["Links"]![0]!["text"]);
        Assert.Equal(["page"], steps[1].DependsOn);
        // The parsed plan is untouched.
        Assert.Equal("@texts/body.html", (string?)((CreateOperation)plan.Steps[0].Operation).Properties!["MainBody"]);
    }

    [Fact]
    public void Upload_files_are_resolved_against_the_plan_folder()
    {
        using var root = new TempDirectory();
        var pdf = root.Write("plan/files/q1.pdf", "%PDF");
        var plan = WritePlan.Parse("""{"operations": [{"op": "upload", "file": "files/q1.pdf", "parent": "3", "properties": {"Copyright": "@@2026"}}]}""");

        var upload = Assert.IsType<UploadOperation>(Assert.Single(PlanFiles.Resolve(plan.Steps, root.Combine("plan"), allowOutside: false)).Operation);

        Assert.Equal(pdf, upload.File);
        Assert.Equal("@2026", (string?)upload.Properties!["Copyright"]);
    }

    [Fact]
    public void Every_file_problem_is_reported_at_once()
    {
        using var root = new TempDirectory();
        root.Write("secret.txt", "x");
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "set", "ref": "1", "properties": {"A": "@missing.html", "B": "@../secret.txt"}},
              {"op": "upload", "file": "../secret.txt", "parent": "3"},
              {"op": "translate", "ref": "1", "lang": "en", "properties": {"C": "@/etc/hostname"}}
            ]}
            """);

        var error = Assert.Throws<UsageException>(() => PlanFiles.Resolve(plan.Steps, root.Combine("plan"), allowOutside: false));

        Assert.Contains("4 problem(s)", error.Message);
        Assert.Contains("operations[0]: properties.A: file", error.Message);
        Assert.Contains("operations[0]: properties.B: file '../secret.txt' is outside", error.Message);
        Assert.Contains("operations[1]: file '../secret.txt' is outside", error.Message);
        Assert.Contains("operations[2]: properties.C: file '/etc/hostname' is an absolute path", error.Message);
        Assert.Contains("@@", error.Hint);
    }

    [Fact]
    public void Allow_outside_reads_files_anywhere()
    {
        using var root = new TempDirectory();
        root.Write("shared/footer.html", "<p>footer</p>");
        var plan = WritePlan.Parse("""{"operations": [{"op": "set", "ref": "1", "properties": {"Footer": "@../shared/footer.html"}}]}""");

        var set = Assert.IsType<SetOperation>(Assert.Single(PlanFiles.Resolve(plan.Steps, root.Combine("plan"), allowOutside: true)).Operation);

        Assert.Equal("<p>footer</p>", (string?)set.Properties!["Footer"]);
    }
}
