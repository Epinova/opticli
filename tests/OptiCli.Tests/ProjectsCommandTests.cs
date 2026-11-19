using OptiCli.Commands;
using OptiCli.Protocol;

namespace OptiCli.Tests;

/// <summary><c>projects</c> subcommands and <c>--project</c>/<c>--ready</c> on the writes: what is checked before a site is asked.</summary>
[Collection(ConsoleCollection.Name)]
public class ProjectsCommandTests
{
    [Theory]
    [InlineData("needs a project", "set", "123", "Heading=Hi", "--ready")]
    [InlineData("isn't published or scheduled by itself", "set", "123", "Heading=Hi", "--in-project", "Spring", "--publish")]
    [InlineData("isn't published or scheduled by itself", "area", "123", "MainArea", "add", "456", "--in-project", "Spring", "--publish-at", "2099-01-01T08:00Z")]
    [InlineData("isn't published or scheduled by itself", "create", "5", "--type", "StandardPage", "--name", "News", "--in-project", "12", "--publish")]
    [InlineData("needs a project", "translate", "123", "--lang", "sv", "--ready")]
    [InlineData("Give the items to mark ready (refs), or --all", "projects", "ready", "12")]
    [InlineData("Give the items to mark ready, or --all, not both", "projects", "ready", "12", "123", "--all")]
    [InlineData("--comment goes to the reviewers", "projects", "ready", "12", "--all", "--comment", "Please")]
    [InlineData("--at", "projects", "publish", "12", "--at", "tomorrow")]
    public async Task Options_that_dont_fit_are_usage_errors(string message, params string[] args)
    {
        var run = await Opticli.RunAsync([.. args, "--json"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal("usage", run.Error().GetProperty("code").GetString());
        Assert.Contains(message.Replace("needs a project", "give the project too", StringComparison.Ordinal), run.Error().GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_needs_the_items()
    {
        var run = await Opticli.RunAsync("projects", "remove", "12", "--json");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal("usage", run.Error().GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("set")]
    [InlineData("create")]
    [InlineData("area")]
    [InlineData("composition")]
    [InlineData("block", "create")]
    [InlineData("upload")]
    [InlineData("translate")]
    public async Task The_saves_take_a_project(params string[] command)
    {
        var run = await Opticli.RunAsync([.. command, "--help"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("--in-project <id|name>", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("--ready", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projects_lists_its_writes_and_publish_documents_ready_only()
    {
        var projects = await Opticli.RunAsync("projects", "--help");
        var publish = await Opticli.RunAsync("projects", "publish", "--help");
        var flat = string.Join(' ', publish.Stdout.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(0, projects.ExitCode);
        foreach (var subcommand in new[] { "create", "ready", "remove", "publish", "reactivate", "delete" })
        {
            Assert.Contains(subcommand, projects.Stdout, StringComparison.Ordinal);
        }
        Assert.Contains("--at <time>", publish.Stdout, StringComparison.Ordinal);
        Assert.Contains("--include-draft", publish.Stdout, StringComparison.Ordinal);
        Assert.Contains("whose own Publish puts every unpublished item live", flat, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("projects", "publish")]
    public async Task A_publish_names_blocks_to_take_along_or_keep_back(params string[] command)
    {
        var run = await Opticli.RunAsync([.. command, "--help"]);
        var flat = string.Join(' ', run.Stdout.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("--with-block <ref>", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("--without-block <ref>", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("by default only blocks never published go along", flat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writes_confirm_someone_elses_block_draft_with_include_draft_and_publish_names_blocks_of_live_content()
    {
        static string Flat(string text) => string.Join(' ', text.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        foreach (var command in new[] { "set", "area" })
        {
            var run = await Opticli.RunAsync(command, "--help");
            Assert.Equal(0, run.ExitCode);
            Assert.Contains("--include-draft", run.Stdout, StringComparison.Ordinal);
            Assert.Contains("over a draft someone else saved", Flat(run.Stdout), StringComparison.Ordinal);
            // It never takes others' drafts of other blocks along with --publish: naming them does.
            Assert.Contains("Someone else's drafts of other blocks never go along with --publish: name those in `opticli publish <ref> --with-block`", Flat(run.Stdout), StringComparison.Ordinal);
        }
        // translate has no --include-draft (decision 15): a new branch has no draft of its own and takes no block over.
        var translate = await Opticli.RunAsync("translate", "--help");
        Assert.Equal(0, translate.ExitCode);
        Assert.DoesNotContain("--include-draft", translate.Stdout, StringComparison.Ordinal);
        var publish = Flat((await Opticli.RunAsync("publish", "--help")).Stdout);
        Assert.DoesNotContain("over a draft someone else saved", publish, StringComparison.Ordinal);
        Assert.Contains("When the version is published already, only the drafts of the blocks --with-block names go live, never-published ones too", publish, StringComparison.Ordinal);
        // Someone else's block draft goes along only when --with-block names it.
        Assert.Contains("and a draft someone else saved, stays behind with a warning unless --with-block names it (--include-draft doesn't take it along)", publish, StringComparison.Ordinal);
        Assert.Contains("It doesn't publish someone else's drafts of the content's blocks along: --with-block names those.", publish, StringComparison.Ordinal);
        Assert.Contains("or although someone else saved that draft (naming it is what confirms their changes going live; --include-draft doesn't)", publish, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_documents_the_plans_project()
    {
        var run = await Opticli.RunAsync("apply", "--help");
        var flat = string.Join(' ', run.Stdout.Split((char[])[' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("\"project\": null leaves it out", flat, StringComparison.Ordinal);
        Assert.Contains("A sealed run refuses \"project\"", flat, StringComparison.Ordinal);
    }

    [Fact]
    public void A_publish_says_whose_changes_went_live_and_who_marked_them_ready()
    {
        static ContentSummary Content(int id) => new() { Ref = $"{id}_1", Id = id, Guid = Guid.Empty, Name = "x" };
        var draft = new PendingDraft("1_1", "bob", new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc), []);
        var result = new ProjectResult
        {
            Project = new ProjectSummary { Id = 12, Name = "Spring", Status = ProjectStatuses.Published },
            Published = [
                new ProjectItemResult { Content = Content(1), Outcome = ProjectItemOutcomes.Published, PendingDraft = draft, ReadyBy = "bob" },
                new ProjectItemResult { Content = Content(2), Outcome = ProjectItemOutcomes.Published },
            ],
        };

        var warning = Assert.Single(ProjectsCommand.Warnings(result));

        Assert.Equal("1_1: put live changes saved by bob in 1_1 (2026-11-16 08:00:00Z) that aren't published yet; bob marked it ready.", warning);
        Assert.StartsWith("1_1: would put live", Assert.Single(ProjectsCommand.Warnings(result with { DryRun = true })), StringComparison.Ordinal);
    }

    [Fact]
    public void A_publish_that_failed_part_says_so_in_the_warnings()
    {
        static ContentSummary Content(int id) => new() { Ref = $"{id}_1", Id = id, Guid = Guid.Empty, Name = "x" };
        var result = new ProjectResult
        {
            Project = new ProjectSummary { Id = 12, Name = "Spring", Status = ProjectStatuses.PublishFailed },
            Published = [new ProjectItemResult { Content = Content(1), Outcome = ProjectItemOutcomes.Published }],
            Failed = [new ProjectItemResult { Content = Content(2), Outcome = ProjectItemOutcomes.Failed, Error = "No access" }],
            SiteError = "Failed to publish 1 content item(s)",
        };

        var warning = Assert.Single(ProjectsCommand.Warnings(result));

        Assert.Contains("1 ready item(s) failed to publish (2_1): the project is publishFailed", warning, StringComparison.Ordinal);
        Assert.Contains("The CMS said: Failed to publish", warning, StringComparison.Ordinal);
    }
}
