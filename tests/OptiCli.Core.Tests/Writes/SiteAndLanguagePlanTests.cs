using OptiCli.Core.Errors;
using OptiCli.Core.Seals;
using OptiCli.Core.Sites;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

public class SiteAndLanguagePlanTests
{
    [Fact]
    public void Parses_language_and_site_steps_with_the_start_page_as_a_dependency()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "language", "lang": "no"},
              {"op": "language", "lang": ["da", "fi"], "enabled": false},
              {"op": "create", "id": "home", "parent": "1", "type": "StartPage", "name": "Home", "lang": "no"},
              {"op": "site", "name": "Alloy", "startPage": "$home", "https": true,
               "hosts": ["alloy.localhost:5000", "https://alloy-sv.localhost:5001@sv", {"name": "*"}, {"name": "edit.alloy.localhost:5000", "type": "edit", "https": false}]}
            ]}
            """);

        var enable = Assert.IsType<LanguageOperation>(plan.Steps[0].Operation);
        Assert.Equal(["no"], enable.Langs);
        Assert.True(enable.Enabled);
        var disable = Assert.IsType<LanguageOperation>(plan.Steps[1].Operation);
        Assert.Equal(["da", "fi"], disable.Langs);
        Assert.False(disable.Enabled);

        var site = Assert.IsType<SiteOperation>(plan.Steps[3].Operation);
        Assert.Equal(("Alloy", "$home", true), (site.Name, site.StartPage, site.Https));
        Assert.Null(site.SiteAssets);
        Assert.Equal(["home"], plan.Steps[3].DependsOn);
        Assert.Equal(
            [
                new SiteAddHost("alloy.localhost:5000"),
                new SiteAddHost("https://alloy-sv.localhost:5001", Language: "sv"),
                new SiteAddHost("*"),
                new SiteAddHost("edit.alloy.localhost:5000", "edit", null, false),
            ],
            site.Hosts);

        var resolved = Assert.IsType<SiteOperation>(WritePlan.Resolve(plan.Steps[3], new Dictionary<string, int> { ["home"] = 42 }));
        Assert.Equal("42", resolved.StartPage);
    }

    [Theory]
    [InlineData("own", SiteAssetsModes.Own)]
    [InlineData("Global", SiteAssetsModes.Global)]
    public void A_site_step_takes_its_assets_root(string value, string mode)
    {
        var plan = WritePlan.Parse($$"""{"operations": [{"op": "site", "name": "Alloy", "startPage": "5", "hosts": ["a.localhost"], "siteAssets": "{{value}}"}]}""");

        Assert.Equal(mode, Assert.IsType<SiteOperation>(plan.Steps[0].Operation).SiteAssets);
    }

    [Theory]
    [InlineData("""{"op": "site", "name": "Alloy", "startPage": "5", "hosts": []}""", "\"hosts\" must be a non-empty array")]
    [InlineData("""{"op": "site", "name": "Alloy", "startPage": "5", "hosts": ["a.localhost"], "siteAssets": "$folder"}""", "\"siteAssets\" must be own or global")]
    [InlineData("""{"op": "site", "name": "Alloy", "startPage": "5", "hosts": [{"name": "a.localhost", "port": 1}]}""", "unknown field \"port\"")]
    [InlineData("""{"op": "site", "name": "Alloy", "startPage": "5", "hosts": [{"name": "a.localhost", "https": "yes"}]}""", ".https must be true or false")]
    [InlineData("""{"op": "site", "name": "Alloy", "hosts": ["a.localhost"]}""", "\"startPage\" is required")]
    [InlineData("""{"op": "language", "lang": "no", "enabled": "yes"}""", "\"enabled\" must be true or false")]
    public void Malformed_site_and_language_steps_are_problems(string step, string problem)
    {
        var error = Assert.Throws<UsageException>(() => WritePlan.Parse($$"""{"operations": [{{step}}]}"""));
        Assert.Contains(problem, error.Message);
    }

    [Fact]
    public void A_content_id_lands_as_a_string_in_a_string_property()
    {
        // A tag property that stores a content id as text: the plan fills it from $id, as it fills a reference property.
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "create", "id": "tag", "parent": "1", "type": "TagPage", "name": "Focus"},
              {"op": "create", "id": "article", "parent": "1", "type": "ArticlePage", "name": "News", "properties": {"TagId": "$tag", "Ids": ["$tag", "x"]}}
            ]}
            """);

        Assert.Equal(["tag"], plan.Steps[1].DependsOn);
        var resolved = Assert.IsType<CreateOperation>(WritePlan.Resolve(plan.Steps[1], new Dictionary<string, int> { ["tag"] = 456 }));
        Assert.Equal("456", (string?)resolved.Properties!["TagId"]);
        Assert.Equal("456", (string?)resolved.Properties["Ids"]![0]);
        Assert.Equal("x", (string?)resolved.Properties["Ids"]![1]);
    }

    [Fact]
    public void A_sealed_plan_never_changes_sites_or_languages()
    {
        var plan = WritePlan.Parse("""
            {"operations": [
              {"op": "language", "lang": "no"},
              {"op": "site", "name": "Alloy", "startPage": "6ba0f1d2-0c7e-4a3b-9a2f-6e1f1c1d2e3f", "hosts": ["alloy.example.com"]}
            ]}
            """);

        var error = Assert.Throws<RefusedException>(() => SealedPlan.Require(plan, null));
        Assert.Contains("changes language branches", error.Message);
        Assert.Contains("changes site definitions", error.Message);
    }

    [Theory]
    [InlineData("alloy.localhost:5000", "alloy.localhost:5000", null)]
    [InlineData("alloy.localhost:5000@sv", "alloy.localhost:5000", "sv")]
    [InlineData("https://alloy.localhost:5001@en-GB", "https://alloy.localhost:5001", "en-GB")]
    [InlineData("*", "*", null)]
    [InlineData(" alloy.localhost@ ", "alloy.localhost@", null)]
    public void A_host_spec_splits_at_its_last_at_sign(string text, string name, string? language)
    {
        Assert.Equal(new SiteAddHost(name, Language: language), SiteHostSpecs.Parse(text));
    }

    [Fact]
    public void Undo_hints_say_how_to_reverse_a_language_change_and_where_a_site_goes()
    {
        var enabled = new LanguagesOutput([new LanguageBranchChange("no", LanguageBranchStatus.Enabled, true), new LanguageBranchChange("sv", LanguageBranchStatus.Unchanged, true)], Saved: true);
        Assert.Equal("opticli languages disable no", UndoHints.For(new LanguageOperation(["no", "sv"]), enabled));
        Assert.Null(UndoHints.For(new LanguageOperation(["sv"]), new LanguagesOutput([new LanguageBranchChange("sv", LanguageBranchStatus.Unchanged, true)], Saved: false)));

        var created = new SiteHostsView("Alloy", null, Guid.NewGuid(), SiteHostStatus.Created, ["created, start page 5"], "http://alloy.localhost:5000/", []);
        Assert.Contains("admin mode (Settings > Websites)", UndoHints.For(new SiteOperation("Alloy", "5", [new SiteAddHost("alloy.localhost:5000")]), created));
    }

    private static readonly WritePlan HostsPlan = WritePlan.Parse("""
        {"operations": [
          {"op": "create", "id": "home", "parent": "1", "type": "StartPage", "name": "Home"},
          {"op": "site", "name": "Alloy", "startPage": "$home", "hosts": ["alloy.localhost:5000", "https://alloy-sv.localhost:5001@sv", "alloy.example.com"]},
          {"op": "upload", "file": "logo.png", "parent": "http://alloy.localhost:5000/siteassets/"}
        ]}
        """);

    [Theory]
    [InlineData("http://alloy.localhost:5000/siteassets/", "alloy.localhost:5000")]
    [InlineData("HTTP://Alloy.Localhost:5000/en/", "alloy.localhost:5000")]
    [InlineData("https://alloy-sv.localhost:5001/sv/", "alloy-sv.localhost:5001")]
    [InlineData("http://alloy.example.com:8080/", "alloy.example.com")]
    public void A_url_on_a_host_an_earlier_site_step_adds_waits_for_that_step(string url, string host)
    {
        var op = new UploadOperation("logo.png", Parent: url);
        var deferredSite = new PlanStepResult(1, "site", null, PlanStepStatus.Deferred);

        var planned = PlanRunner.OnPlannedSite(op, HostsPlan.Steps.Take(2), [new PlanStepResult(0, "create", "home", PlanStepStatus.Deferred), deferredSite]);

        Assert.Equal((url, host, 1, "Alloy"), planned);
    }

    [Theory]
    [InlineData("http://alloy.localhost:5001/siteassets/")]
    [InlineData("http://other.localhost:5000/")]
    [InlineData("/siteassets/")]
    [InlineData("$home")]
    [InlineData("5")]
    public void Other_refs_are_checked_as_they_are(string reference)
    {
        Assert.Null(PlanRunner.OnPlannedSite(new UploadOperation("logo.png", Parent: reference), HostsPlan.Steps.Take(2), [new PlanStepResult(1, "site", null, PlanStepStatus.Deferred)]));
    }

    [Fact]
    public void A_site_step_that_changes_nothing_leaves_its_hosts_to_the_database()
    {
        var unchanged = new SiteHostsView("Alloy", 1, Guid.NewGuid(), SiteHostStatus.Unchanged, [], "http://alloy.localhost:5000/", []);
        var changed = unchanged with { Status = SiteHostStatus.Changed };
        var op = new UploadOperation("logo.png", Parent: "http://alloy.localhost:5000/siteassets/");

        Assert.Null(PlanRunner.OnPlannedSite(op, HostsPlan.Steps.Take(2), [new PlanStepResult(1, "site", null, PlanStepStatus.Valid, unchanged)]));
        Assert.NotNull(PlanRunner.OnPlannedSite(op, HostsPlan.Steps.Take(2), [new PlanStepResult(1, "site", null, PlanStepStatus.Valid, changed)]));
    }

    [Theory]
    [InlineData("https://www.example.com/docs", true)]
    [InlineData("HTTP://www.example.com/", true)]
    [InlineData("//cdn.example.com/a", true)]
    [InlineData("/en/about/", false)]
    [InlineData("/", false)]
    public void Only_an_external_shortcut_has_a_url_with_a_host(string url, bool external)
    {
        Assert.Equal(external, PlanRunner.ExternalLink(url));
    }
}
