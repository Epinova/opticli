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
    [InlineData("""{"op": "site", "name": "Alloy", "startPage": "5", "hosts": []}""", "\"hosts\" must be a non-empty array")]
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
}
