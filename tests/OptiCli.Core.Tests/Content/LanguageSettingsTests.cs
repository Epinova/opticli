using OptiCli.Core.Content;
using OptiCli.Core.Urls;
using static OptiCli.Core.Tests.Content.ModelFixture;

namespace OptiCli.Core.Tests.Content;

public class LanguageSettingsTests
{
    private static LanguageBranch? Code(string code) => code switch { "en" => En, "sv" => Sv, _ => null };

    [Fact]
    public void Without_a_branch_the_nearest_settings_fall_back_in_order()
    {
        var settings = new LanguageSettings([new(200, Swedish, null, ["de", "en"], true), new(1, Swedish, null, [], true)]);

        Assert.Equal(new LanguageChoice(English, "fallback", 200), settings.Choose(210, [1, 200], [English], Swedish, Code));
        // Below a node whose settings have no fallback, nothing is shown in that language.
        Assert.Equal(new LanguageChoice(null, "none", 1), settings.Choose(50, [1], [English], Swedish, Code));
        // A branch of its own is shown as it is, and content outside every node with settings has none.
        Assert.Null(settings.Choose(210, [1, 200], [English, Swedish], Swedish, Code));
        Assert.Null(new LanguageSettings([]).Choose(210, [1, 200], [English], Swedish, Code));
    }

    [Fact]
    public void A_replacement_language_wins_even_over_the_requested_branch()
    {
        var settings = new LanguageSettings([new(200, Swedish, English, [], true)]);

        Assert.Equal(new LanguageChoice(English, "replacement", 200), settings.Choose(200, [1], [English, Swedish], Swedish, Code));
    }

    [Fact]
    public void A_site_whose_start_page_has_settings_only_takes_its_active_languages_as_prefixes()
    {
        var model = Create();
        var everything = model.Sites.Parse("https://www.example.com/se/om-oss/", null);
        var englishOnly = new SiteMap(model.Sites.All, model.Languages, null, null, new LanguageSettings([new(5, English, null, [], true), new(5, Swedish, null, [], false)]))
            .Parse("https://www.example.com/se/om-oss/", null);

        Assert.Equal(("sv", "path"), (everything.Language!.Code, everything.LanguageSource));
        Assert.Equal(["se", "om-oss"], englishOnly.Segments);
        Assert.Equal("en", englishOnly.Language!.Code);
    }
}
