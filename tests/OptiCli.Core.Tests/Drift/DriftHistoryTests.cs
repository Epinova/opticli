using System.Diagnostics;
using System.Text.Json;
using OptiCli.Core.Cms;
using OptiCli.Core.Drift;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Drift;

/// <summary><see cref="DriftHistory"/> against real git repositories in a temp directory.</summary>
public class DriftHistoryTests
{
    private const string NewsGuid = "0B2B9C1E-3F4A-4B5C-8D6E-7F8091A2B3A1";
    private const string ArticleGuid = "0B2B9C1E-3F4A-4B5C-8D6E-7F8091A2B3A2";

    private static readonly IReadOnlyList<ContentTypeInfo> Types =
    [
        new(1, Guid.Parse(NewsGuid), "NewsPage", null, null, ContentKind.Page, "Page", "Site.Models.NewsPage, Site", null),
        new(2, Guid.Parse(ArticleGuid), "ArticlePage", null, null, ContentKind.Page, "Page", "Site.Models.ArticlePage, Site", null),
    ];

    private static readonly DriftItem NewsOnlyInDatabase = new("NewsPage", DriftAhead.Database, DriftHistory.OnlyInDatabase);
    private static readonly DriftItem TeaserOnlyInDatabase = new("ArticlePage.Teaser", DriftAhead.Database, DriftHistory.OnlyInDatabase);

    private static DriftReport Report(DriftItem[] types, DriftItem[] properties) => DriftReport.Create(types, properties, [], [], [], []);

    [Fact]
    public void A_type_whose_class_the_history_removed_names_the_commit()
    {
        using var repo = new GitRepo();
        // The project is a folder of the repository; its classes are searched from the top.
        repo.Commit("Add news page", ("src/Site/Site.csproj", "<Project />"), ("src/Models/NewsPage.cs", NewsPage));
        repo.Remove("Remove news page", "src/Models/NewsPage.cs");
        var report = Report([NewsOnlyInDatabase], []);

        var annotated = DriftHistory.Annotate(report, repo.Combine("src/Site"), Types);

        var removed = annotated.ContentTypes.Single().RemovedFromCode;
        Assert.Equal(new CodeRemoval(repo.Head, "2026-09-14", "Remove news page"), removed);
        // What stops writes stays the agent's.
        Assert.Equal(report.Fingerprint, annotated.Fingerprint);
        Assert.Equal(report.Fingerprint, DriftReport.FingerprintOf(annotated.Sections()));
        Assert.Equal(report.Ahead, annotated.Ahead);
        Assert.Equal(report.Differences, annotated.Differences);
        Assert.Contains("content type NewsPage (only in the database; its class was removed from code in " + repo.Head + " (2026-09-14))", annotated.Describe(), StringComparison.Ordinal);
        Assert.Contains("\"removedFromCode\":{\"commit\":\"" + repo.Head + "\"", JsonSerializer.Serialize(annotated, AgentJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_database_s_guid_the_class_name_is_looked_up()
    {
        using var repo = new GitRepo();
        repo.Commit("Add news page", ("Models/News.cs", NewsPage.Replace($"[ContentType(GUID = \"{NewsGuid}\")]", "[ContentType]", StringComparison.Ordinal)));
        repo.Remove("Drop the news page class", "Models/News.cs");

        var annotated = DriftHistory.Annotate(Report([NewsOnlyInDatabase], []), repo.Path, []);

        Assert.Equal("Drop the news page class", annotated.ContentTypes.Single().RemovedFromCode?.Subject);
    }

    [Fact]
    public void A_class_still_in_head_was_not_removed()
    {
        using var repo = new GitRepo();
        repo.Commit("Add news page", ("Models/NewsPage.cs", NewsPage));
        repo.Commit("Touch something else", ("Models/Other.cs", "public class Other { }"));
        // Deleted in the working tree only: no commit removed it.
        File.Delete(repo.Combine("Models/NewsPage.cs"));

        var annotated = DriftHistory.Annotate(Report([NewsOnlyInDatabase], []), repo.Path, Types);

        Assert.Null(annotated.ContentTypes.Single().RemovedFromCode);
    }

    [Fact]
    public void A_property_removed_from_its_class_names_the_commit()
    {
        using var repo = new GitRepo();
        repo.Commit("Add article page", ("Models/ArticlePage.cs", ArticlePage(withTeaser: true)));
        repo.Commit("Remove the teaser", ("Models/ArticlePage.cs", ArticlePage(withTeaser: false)));
        var heading = new DriftItem("ArticlePage.Heading", DriftAhead.Database, DriftHistory.OnlyInDatabase);

        var annotated = DriftHistory.Annotate(Report([], [TeaserOnlyInDatabase, heading]), repo.Path, Types);

        Assert.Equal("Remove the teaser", annotated.Properties.Single(p => p.Name == "ArticlePage.Teaser").RemovedFromCode?.Subject);
        // Still declared in its class: not removed (the CMS may skip it for another reason).
        Assert.Null(annotated.Properties.Single(p => p.Name == "ArticlePage.Heading").RemovedFromCode);
        Assert.Contains("property ArticlePage.Teaser (only in the database; removed from code in ", annotated.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_class_property_no_file_declares_any_more_is_found_in_every_file()
    {
        using var repo = new GitRepo();
        repo.Commit("Add pages", ("Models/SitePageData.cs", "public abstract class SitePageData : PageData\n{\n    public virtual string Teaser { get; set; }\n}\n"),
            ("Models/ArticlePage.cs", ArticlePage(withTeaser: false).Replace(": PageData", ": SitePageData", StringComparison.Ordinal)));
        repo.Commit("Drop the base teaser", ("Models/SitePageData.cs", "public abstract class SitePageData : PageData\n{\n}\n"));

        var annotated = DriftHistory.Annotate(Report([], [TeaserOnlyInDatabase]), repo.Path, Types);

        Assert.Equal("Drop the base teaser", annotated.Properties.Single().RemovedFromCode?.Subject);
    }

    [Fact]
    public void A_class_moved_to_another_project_was_not_removed()
    {
        using var repo = new GitRepo();
        repo.Commit("Add pages", ("src/Site/Models/NewsPage.cs", NewsPage), ("src/Site/Models/ArticlePage.cs", ArticlePage(withTeaser: true)));
        // Moved to a shared library in one commit, with a lower-case GUID and Windows line endings there.
        repo.Remove("Move pages to the shared library", "src/Site/Models/NewsPage.cs");
        repo.Remove("Move pages to the shared library", "src/Site/Models/ArticlePage.cs");
        repo.Commit("Add them there",
            ("src/Shared/Pages/NewsPage.cs", NewsPage.Replace(NewsGuid, NewsGuid.ToLowerInvariant(), StringComparison.Ordinal).ReplaceLineEndings("\r\n")),
            ("src/Shared/Pages/Article.cs", ArticlePage(withTeaser: true).Replace(ArticleGuid, ArticleGuid.ToLowerInvariant(), StringComparison.Ordinal).ReplaceLineEndings("\r\n")));

        var annotated = DriftHistory.Annotate(Report([NewsOnlyInDatabase], [TeaserOnlyInDatabase]), repo.Combine("src/Site"), Types);

        Assert.Null(annotated.ContentTypes.Single().RemovedFromCode);
        Assert.Null(annotated.Properties.Single().RemovedFromCode);
    }

    [Fact]
    public void Other_differences_are_not_looked_up()
    {
        using var repo = new GitRepo();
        repo.Commit("Add news page", ("Models/NewsPage.cs", NewsPage));
        repo.Remove("Remove news page", "Models/NewsPage.cs");
        var newer = new DriftItem("NewsPage", DriftAhead.Database, "synced from version 2.0.0.0 of its assembly, newer than this build's, so the CMS ignores the model's changes");
        var report = DriftReport.Create([newer], [], [new DriftItem("20260101000000_AddEvents", DriftAhead.Database, "only in the database")], [], [], []);

        Assert.False(DriftHistory.Applies(report));
        Assert.Same(report, DriftHistory.Annotate(report, repo.Path, Types));
    }

    [Fact]
    public void Outside_a_git_work_tree_nothing_is_added()
    {
        using var plain = new TempDirectory();
        plain.Write("Models/NewsPage.cs", NewsPage);
        var report = Report([NewsOnlyInDatabase], []);

        Assert.Same(report, DriftHistory.Annotate(report, plain.Path, Types));
        Assert.Same(report, DriftHistory.Annotate(report, plain.Combine("missing"), Types));
        Assert.Same(report, DriftHistory.Annotate(report, null, Types));
    }

    [Fact]
    public void A_repository_without_commits_or_time_adds_nothing()
    {
        using var empty = new GitRepo();
        var report = Report([NewsOnlyInDatabase], []);
        Assert.Same(report, DriftHistory.Annotate(report, empty.Path, Types));

        using var repo = new GitRepo();
        repo.Commit("Add news page", ("Models/NewsPage.cs", NewsPage));
        repo.Remove("Remove news page", "Models/NewsPage.cs");
        Assert.Same(report, DriftHistory.Annotate(report, repo.Path, Types, budget: TimeSpan.Zero));
    }

    [Fact]
    public void Advice_says_pulling_won_t_help_only_when_the_history_accounts_for_everything_only_in_the_database()
    {
        var removal = new CodeRemoval("abc1234", "2026-09-14", "Remove news page");
        var all = Report([NewsOnlyInDatabase with { RemovedFromCode = removal }], [TeaserOnlyInDatabase with { RemovedFromCode = removal }]);
        var some = Report([NewsOnlyInDatabase with { RemovedFromCode = removal }], [TeaserOnlyInDatabase]);
        var both = Report([NewsOnlyInDatabase with { RemovedFromCode = removal }, new DriftItem("EventPage", DriftAhead.Local, "only in the code")], []);

        Assert.True(DriftHistory.AllRemovedFromCode(all));
        Assert.Equal(
            "This checkout's git history removed everything only the database has (removedFromCode names the commit): the CMS kept it for its content "
            + "(`opticli types --orphaned`), or that environment doesn't run the removal yet. Pulling won't change that, nor will writing with this build.",
            DriftText.Advice(all));
        Assert.Contains("Pulling won't change that", DriftText.Started(all), StringComparison.Ordinal);

        Assert.False(DriftHistory.AllRemovedFromCode(some));
        Assert.Equal(
            "The database has what this build lacks: newer code in that environment (pull and build), or classes or properties removed from code that "
            + "the CMS kept for their content (`opticli types --orphaned`; pulling won't change those).",
            DriftText.Advice(some));
        Assert.DoesNotContain("Pulling won't", DriftText.Started(some), StringComparison.Ordinal);

        Assert.Equal(DriftAhead.Both, both.Ahead);
        Assert.StartsWith("This branch has changes that aren't deployed there", DriftText.Advice(both), StringComparison.Ordinal);
        Assert.Contains("and this checkout's git history removed everything only the database has", DriftText.Advice(both), StringComparison.Ordinal);

        // The short warning every response carries only states the direction.
        Assert.Contains("the database is ahead", DriftText.Warning(some), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_migrations_or_the_schema_on_the_database_s_side_are_newer_code()
    {
        var migration = new DriftItem("20250101000000_Init", DriftAhead.Database, "applied to the database, not in this build");
        var schema = new DriftItem("CMS", DriftAhead.Database, "schema version 8024 in the database, this build's packages need 8023");
        var migrations = DriftReport.Create([], [], [migration], [], [], []);
        var schemaOnly = DriftReport.Create([], [], [], [], [schema], []);
        var withLocal = DriftReport.Create([new DriftItem("EventPage", DriftAhead.Local, "only in the code")], [], [migration], [], [], []);

        foreach (var report in new[] { migrations, schemaOnly })
        {
            Assert.False(report.ContentModelInDatabase);
            Assert.Equal("The environment runs newer code than this checkout: pull and build.", DriftText.Advice(report));
            Assert.Contains("(the database is ahead: its environment runs newer code than this checkout)", report.Describe(), StringComparison.Ordinal);
            Assert.DoesNotContain("removed from code", report.Describe(), StringComparison.Ordinal);
        }
        Assert.Equal("This branch and the environment each have changes the other lacks: check out what is deployed, or pull and deploy.", DriftText.Advice(withLocal));
        // A content type on the database's side brings the other cause back.
        Assert.Contains("removed from code", DriftText.Advice(DriftReport.Create([NewsOnlyInDatabase], [], [migration], [], [], [])), StringComparison.Ordinal);
    }

    [Fact]
    public void A_same_named_property_of_an_unrelated_class_is_not_this_type_s()
    {
        using var repo = new GitRepo();
        // ArticlePage derives from the CMS's PageData (not in the repository) and never declared Summary here.
        repo.Commit("Add pages", ("Models/ArticlePage.cs", ArticlePage(withTeaser: false)),
            ("Models/OtherBlock.cs", "public class OtherBlock : BlockData\n{\n    public virtual string Summary { get; set; }\n}\n"));
        repo.Commit("Drop other block summary", ("Models/OtherBlock.cs", "public class OtherBlock : BlockData\n{\n}\n"));
        var summary = new DriftItem("ArticlePage.Summary", DriftAhead.Database, DriftHistory.OnlyInDatabase);

        var annotated = DriftHistory.Annotate(Report([], [summary]), repo.Path, Types);

        Assert.Null(annotated.Properties.Single().RemovedFromCode);
    }

    [Fact]
    public void A_removed_class_of_the_same_name_with_another_guid_is_not_this_type()
    {
        using var repo = new GitRepo();
        repo.Commit("Add another news page", ("Models/NewsPage.cs", NewsPage.Replace(NewsGuid, "0B2B9C1E-3F4A-4B5C-8D6E-7F8091A2B3FF", StringComparison.Ordinal)));
        repo.Remove("Remove the other news page", "Models/NewsPage.cs");

        Assert.Null(DriftHistory.Annotate(Report([NewsOnlyInDatabase], []), repo.Path, Types).ContentTypes.Single().RemovedFromCode);
        // Nor when the database's GUID isn't known.
        Assert.Null(DriftHistory.Annotate(Report([NewsOnlyInDatabase], []), repo.Path, []).ContentTypes.Single().RemovedFromCode);
    }

    [Fact]
    public void A_removed_class_with_the_type_s_guid_in_another_format_is_found_by_name()
    {
        using var repo = new GitRepo();
        // Without hyphens: git log -S doesn't find the database's form, the name does, and the GUIDs are the same.
        repo.Commit("Add news page", ("Models/NewsPage.cs", NewsPage.Replace(NewsGuid, Guid.Parse(NewsGuid).ToString("N"), StringComparison.Ordinal)));
        repo.Remove("Remove news page", "Models/NewsPage.cs");

        Assert.Equal("Remove news page", DriftHistory.Annotate(Report([NewsOnlyInDatabase], []), repo.Path, Types).ContentTypes.Single().RemovedFromCode?.Subject);
    }

    [Fact]
    public void A_comment_naming_the_property_is_not_its_declaration()
    {
        using var repo = new GitRepo();
        var page = ArticlePage(withTeaser: false);
        repo.Commit("Add article page", ("Models/ArticlePage.cs", page.Replace("    public virtual string Heading", "    // shows the Teaser\n    public virtual string Heading", StringComparison.Ordinal)));
        repo.Commit("Reword a comment", ("Models/ArticlePage.cs", page.Replace("    public virtual string Heading", "    /// <summary>The Teaser\n    /// * Teaser {\n    public virtual string Heading", StringComparison.Ordinal)));
        repo.Commit("Drop the comment", ("Models/ArticlePage.cs", page));

        Assert.Null(DriftHistory.Annotate(Report([], [TeaserOnlyInDatabase]), repo.Path, Types).Properties.Single().RemovedFromCode);
    }

    [Fact]
    public void A_comment_naming_the_class_is_not_its_declaration()
    {
        // The type's class (no GUID) went first; a later commit only dropped a comment naming it.
        using var byName = new GitRepo();
        byName.Commit("Add news page", ("Models/NewsPage.cs", NewsPage.Replace($"[ContentType(GUID = \"{NewsGuid}\")]", "[ContentType]", StringComparison.Ordinal)),
            ("Models/Notes.cs", "// see class NewsPage\npublic class Notes { }\n"));
        byName.Remove("Remove news page", "Models/NewsPage.cs");
        byName.Commit("Tidy notes", ("Models/Notes.cs", "public class Notes { }\n"));
        Assert.Equal("Remove news page", DriftHistory.Annotate(Report([NewsOnlyInDatabase], []), byName.Path, Types).ContentTypes.Single().RemovedFromCode?.Subject);

        // A removed class of the same name with another GUID, whose doc comment names the class above that GUID.
        using var otherGuid = new GitRepo();
        otherGuid.Commit("Add another news page", ("Models/NewsPage.cs", "using Site;\n/// <summary>The class NewsPage of the old site.</summary>\n"
            + NewsPage.Replace(NewsGuid, "0B2B9C1E-3F4A-4B5C-8D6E-7F8091A2B3FF", StringComparison.Ordinal)));
        otherGuid.Remove("Remove the other news page", "Models/NewsPage.cs");
        Assert.Null(DriftHistory.Annotate(Report([NewsOnlyInDatabase], []), otherGuid.Path, Types).ContentTypes.Single().RemovedFromCode);

        // A file that only mentions the class in a comment isn't one of its files: its view model's Teaser isn't the page's.
        using var mention = new GitRepo();
        mention.Commit("Add article page", ("Models/ArticlePage.cs", ArticlePage(withTeaser: false)),
            ("Models/ArticleModel.cs", "// For the class ArticlePage.\npublic class ArticleModel\n{\n    public string Teaser { get; set; }\n}\n"));
        mention.Commit("Drop the model's teaser", ("Models/ArticleModel.cs", "// For the class ArticlePage.\npublic class ArticleModel\n{\n}\n"));
        Assert.Null(DriftHistory.Annotate(Report([], [TeaserOnlyInDatabase]), mention.Path, Types).Properties.Single().RemovedFromCode);
    }

    [Theory]
    // A "/" before the name on the line, and the name wrapped onto a line of its own: declarations the strict pattern misses.
    [InlineData("    [Display(Description = \"a/b\")] public virtual string Teaser { get; set; }")]
    [InlineData("    public virtual string\n        Teaser { get; set; }")]
    public void A_property_head_still_mentions_in_another_form_is_not_removed(string declaration)
    {
        using var repo = new GitRepo();
        repo.Commit("Add article page", ("Models/ArticlePage.cs", ArticlePage(withTeaser: true)));
        var page = ArticlePage(withTeaser: false).Replace("    public virtual string Heading { get; set; }", $"    public virtual string Heading {{ get; set; }}\n{declaration}", StringComparison.Ordinal);
        repo.Commit("Reformat the teaser", ("Models/ArticlePage.cs", page));

        Assert.Null(DriftHistory.Annotate(Report([], [TeaserOnlyInDatabase]), repo.Path, Types).Properties.Single().RemovedFromCode);
    }

    [Fact]
    public void Git_runs_only_below_a_dot_git()
    {
        using var plain = new TempDirectory();
        Assert.False(DriftHistory.InRepository(plain.Path));
        plain.Write("worktree/.git", "gitdir: /elsewhere");
        Assert.True(DriftHistory.InRepository(plain.Combine("worktree")));

        using var repo = new GitRepo();
        repo.Commit("Add project", ("src/Site/Site.csproj", "<Project />"));
        Assert.True(DriftHistory.InRepository(repo.Combine("src/Site")));
    }

    [Fact]
    public void Database_ahead_names_both_causes()
    {
        foreach (var text in new[] { DriftText.Advice(DriftAhead.Database), DriftText.Advice(DriftAhead.Both), DriftAhead.Describe(DriftAhead.Database), AgentErrors.DriftHint })
        {
            Assert.Contains("newer code", text, StringComparison.Ordinal);
            Assert.Contains("removed from code", text, StringComparison.Ordinal);
        }
        Assert.Contains("`opticli types --orphaned`", DriftText.Advice(DriftAhead.Database), StringComparison.Ordinal);
    }

    private const string NewsPage = $$"""
        [ContentType(GUID = "{{NewsGuid}}")]
        public class NewsPage : PageData
        {
            public virtual string Heading { get; set; }
        }

        """;

    private static string ArticlePage(bool withTeaser) => $$"""
        [ContentType(GUID = "{{ArticleGuid}}")]
        public class ArticlePage : PageData
        {
            [Display(Name = "Heading")]
            public virtual string Heading { get; set; }
        {{(withTeaser ? "\n    public virtual string Teaser\n    {\n        get; set;\n    }\n" : "")}}
        }

        """;

    /// <summary>A git repository in a temp directory, committed to with fixed author dates.</summary>
    private sealed class GitRepo : IDisposable
    {
        private readonly TempDirectory _directory = new();

        public GitRepo() => Git("init", "-q");

        public string Path => _directory.Path;

        /// <summary>The abbreviated hash of <c>HEAD</c>.</summary>
        public string Head => Git("rev-parse", "--short", "HEAD").Trim();

        public string Combine(string relativePath) => _directory.Combine(relativePath);

        public void Commit(string message, params (string Path, string Content)[] files)
        {
            foreach (var (path, content) in files)
            {
                _directory.Write(path, content);
                Git("add", "--", path);
            }
            Git("commit", "-q", "-m", message);
        }

        public void Remove(string message, string path)
        {
            Git("rm", "-q", "--", path);
            Git("commit", "-q", "-m", message);
        }

        private string Git(params string[] arguments)
        {
            var info = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path };
            foreach (var argument in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "-c", "init.defaultBranch=main" }.Concat(arguments))
            {
                info.ArgumentList.Add(argument);
            }
            info.Environment["GIT_AUTHOR_DATE"] = "2026-09-14T12:00:00+02:00";
            info.Environment["GIT_COMMITTER_DATE"] = "2026-09-14T12:00:00+02:00";
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error.Result}");
            return output.Result;
        }

        public void Dispose() => _directory.Dispose();
    }
}
