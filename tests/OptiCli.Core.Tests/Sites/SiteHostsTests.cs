using OptiCli.Core.Cms;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;
using OptiCli.Core.Sites;
using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Sites;

/// <summary>Host names, <c>sites primary</c> pairs, and the saved mapping that <c>doctor</c> compares with the database.</summary>
public class SiteHostsTests : IDisposable
{
    private static readonly string[] Languages = ["en", "nb"];

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private static SiteInfo Site(int id, string name, params HostInfo[] hosts) =>
        new(id, Guid.Parse($"0b1c2d3e-0000-4000-8000-{id:D12}"), name, null, "5", "Start", "en", "3", hosts);

    private static readonly IReadOnlyList<SiteInfo> Sites =
    [
        Site(1, "Site A", new HostInfo("*", HostType.Undefined, null, null), new HostInfo("site-a.example", HostType.Primary, null, null)),
        Site(2, "Site B", new HostInfo("localhost:5002", HostType.Primary, null, true), new HostInfo("site-b.example", HostType.Undefined, null, null)),
        Site(3, "Shop=Main", new HostInfo("shop.example", HostType.Primary, null, null)),
        Site(4, "Team@nb", new HostInfo("team.example", HostType.Primary, null, null)),
    ];

    [Theory]
    [InlineData("localhost:5001", "localhost:5001", null)]
    [InlineData("https://localhost:5001/", "localhost:5001", true)]
    [InlineData("HTTP://LocalHost:5001", "localhost:5001", false)]
    [InlineData("  site-a.example/ ", "site-a.example", null)]
    [InlineData("*", "*", null)]
    [InlineData("127.0.0.1:5010", "127.0.0.1:5010", null)]
    [InlineData("localhost:05001", "localhost:5001", null)]
    // The scheme's default port is left out, as browsers and Uri leave it out; alone it says the scheme.
    [InlineData("localhost:443", "localhost", true)]
    [InlineData("localhost:80", "localhost", false)]
    [InlineData("https://localhost:443/", "localhost", true)]
    [InlineData("http://localhost:80", "localhost", false)]
    [InlineData("http://localhost:443", "localhost:443", false)]
    [InlineData("https://localhost:80/", "localhost:80", true)]
    public void Host_names_are_normalised(string input, string name, bool? https)
    {
        Assert.True(HostNames.TryNormalize(input, out var normalised, out var scheme, out var error), error);
        Assert.Equal(name, normalised);
        Assert.Equal(https, scheme);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("local host", "whitespace")]
    [InlineData("https://localhost:5001/en/", "path")]
    [InlineData("localhost:5001?a=1", "query")]
    [InlineData("ftp://localhost", "scheme 'ftp'")]
    [InlineData("localhost:0", "port '0'")]
    [InlineData("localhost:65536", "port '65536'")]
    [InlineData("localhost:", "port ''")]
    [InlineData("user@localhost", "user info")]
    [InlineData("a:1:2", "more than one ':'")]
    [InlineData("[::1]:5001", "IPv6")]
    public void Bad_host_names_say_why(string input, string reason)
    {
        Assert.False(HostNames.TryNormalize(input, out _, out _, out var error));
        Assert.Contains(reason, error);
    }

    [Theory]
    [InlineData("localhost:5001", true)]
    [InlineData("edge-nested.localhost", true)]
    [InlineData("site-a.test:5001", true)]
    [InlineData("127.0.0.1:5010", true)]
    [InlineData("site-a.example", false)]
    [InlineData("localhost.example", false)]
    public void Local_hosts_are_told_apart_from_production_names(string name, bool local)
    {
        Assert.Equal(local, HostNames.IsLocal(name));
    }

    [Theory]
    [InlineData("localhost:5001", null, null, "https://localhost:5001/")]
    [InlineData("localhost:5001", false, "https://site-a.example/app/", "http://localhost:5001/app/")]
    [InlineData("localhost", true, "http://site-a.example:8080/", "https://localhost/")]
    [InlineData("localhost:443", null, null, "https://localhost/")]
    public void A_site_url_is_printed_as_the_cms_stores_it_and_keeps_its_path(string host, bool? https, string? current, string expected)
    {
        Assert.Equal(expected, HostNames.SiteUrl(host, https, current));
    }

    [Theory]
    [InlineData("https://localhost/", "localhost")]
    [InlineData("https://localhost:443/", "localhost")]
    [InlineData("http://localhost:443/", "localhost:443")]
    [InlineData("https://Site-A.example:5001/app/", "site-a.example:5001")]
    public void The_host_of_a_site_url_is_its_authority(string url, string host)
    {
        Assert.Equal(host, HostNames.FromUrl(url));
    }

    [Fact]
    public void A_pair_is_split_on_its_last_equals_sign()
    {
        var pair = PrimaryPairs.Parse("Shop=Main=localhost:5003", Sites, Languages);

        Assert.Equal(("Shop=Main", null, "localhost:5003", null), (pair.Site.Name, pair.Language, pair.Host, pair.Https));
    }

    [Fact]
    public void A_trailing_at_is_a_language_only_when_it_names_an_enabled_language()
    {
        var language = PrimaryPairs.Parse("site a@NB=https://localhost:5004/", Sites, Languages);
        var whole = PrimaryPairs.Parse("Team@nb=localhost:5005", Sites, Languages);

        Assert.Equal(("Site A", "nb", "localhost:5004", HostHttps.True), (language.Site.Name, language.Language, language.Host, language.Https));
        Assert.Equal("Site A@nb", language.Key);
        Assert.Equal("https://localhost:5004", language.Value);
        Assert.Equal(("Team@nb", (string?)null), (whole.Site.Name, whole.Language));
        var unknown = Assert.Throws<NotFoundException>(() => PrimaryPairs.Parse("Site A@de=localhost:5006", Sites, Languages));
        Assert.Contains("'Site A@de'", unknown.Message);
        Assert.Equal("'de' isn't an enabled language (enabled: en, nb), so the whole of 'Site A@de' was read as a site name; the site is 'Site A'.", unknown.Hint);
    }

    [Fact]
    public void Sites_are_named_by_name_id_or_guid_and_an_unknown_one_gets_close_names()
    {
        Assert.Equal("Site B", PrimaryPairs.Parse("2=localhost:5002", Sites, Languages).Site.Name);
        Assert.Equal("Site B", PrimaryPairs.Parse($"{Sites[1].Guid}=localhost:5002", Sites, Languages).Site.Name);

        var error = Assert.Throws<NotFoundException>(() => PrimaryPairs.Parse("Site C=localhost:5003", Sites, Languages));
        Assert.Contains("Did you mean", error.Hint);
    }

    [Theory]
    [InlineData("Site A=localhost:443", null, "localhost", null)]
    [InlineData("Site A=http://localhost:443", null, "localhost:443", HostHttps.False)]
    [InlineData("Site A=localhost:443", HostHttps.False, "localhost:443", HostHttps.False)]
    [InlineData("Site A=localhost:80", HostHttps.True, "localhost:80", HostHttps.True)]
    [InlineData("Site A=https://localhost:443", HostHttps.Unset, "localhost", HostHttps.Unset)]
    public void The_https_setting_decides_which_port_is_the_default(string text, string? https, string host, string? flag)
    {
        var pair = PrimaryPairs.Parse(text, Sites, Languages, https);

        Assert.Equal((host, flag), (pair.Host, pair.Https));
        Assert.Equal(text[(text.IndexOf('=') + 1)..], pair.Typed);
    }

    [Fact]
    public void A_port_alone_sends_no_https_setting_and_is_saved_as_typed()
    {
        var pair = PrimaryPairs.Parse("Site A=LocalHost:443/", Sites, Languages);
        var b = Site(2, "Site B", new HostInfo("localhost:443", HostType.Primary, null, false));

        Assert.Equal(("localhost", null, true, "localhost:443"), (pair.Host, pair.Https, pair.Inferred, pair.Value));
        Assert.Equal(PrimaryMapping.Matches, PrimaryMapping.Compare(new Dictionary<string, SavedPrimaryHost> { ["Site B"] = new("localhost:443") }, [b], Languages).Single().Status);
    }

    [Fact]
    public void The_https_option_overrides_the_scheme()
    {
        var pair = PrimaryPairs.Parse("Site A=https://localhost:5001", Sites, Languages, HostHttps.Unset);

        Assert.Equal((HostHttps.Unset, "localhost:5001"), (pair.Https, pair.Value));
    }

    [Theory]
    [InlineData("Site A")]
    [InlineData("=localhost:5001")]
    [InlineData("Site A=")]
    [InlineData("Site A=localhost:5001/en/")]
    public void Malformed_pairs_are_usage_errors(string text)
    {
        Assert.Throws<UsageException>(() => PrimaryPairs.Parse(text, Sites, Languages));
    }

    [Fact]
    public void The_saved_mapping_round_trips_and_a_save_replaces_only_the_named_sites()
    {
        var config = _root.Combine("config/opticli/config.json");
        var project = _root.Combine("repo/src/Web");
        UserConfig.SaveDatabase(config, project, new SavedDatabase("a1b2c3", ConnectionSource.UserSecrets, "secrets.json", "ConnectionStrings:EPiServerDB", null, "localhost", "Example", true, DateTimeOffset.UnixEpoch, SavedDatabase.ViaCommand));

        UserConfig.SavePrimaryHosts(config, project, ["Site A", "Site B", "Site A@Home"], Languages,
            [new("Site A", new("localhost:5001")), new("Site A@nb", new("https://localhost:5004")), new("Site B", new("localhost:5002")), new("Site A@Home", new("localhost:5009"))]);
        UserConfig.SavePrimaryHosts(config, project, ["site a"], Languages, [new("Site A", new("localhost:5011", KeepEdit: true))]);

        var settings = UserConfig.ForProject(config, project)!;
        Assert.Equal(
            new Dictionary<string, SavedPrimaryHost>
            {
                ["Site A"] = new("localhost:5011", KeepEdit: true),
                ["Site A@Home"] = new("localhost:5009"),
                ["Site B"] = new("localhost:5002"),
            },
            settings.PrimaryHosts!.OrderBy(e => e.Key).ToDictionary(e => e.Key, e => e.Value));
        Assert.Equal("a1b2c3", settings.Database!.Id);
        var json = File.ReadAllText(config);
        Assert.Contains("\"Site B\": \"localhost:5002\"", json);
        Assert.Contains("\"keepEdit\": true", json);
        Assert.DoesNotContain("keepSiteUrl", json);
    }

    [Fact]
    public void Forget_drops_a_sites_entries_or_one_entry_and_nothing_of_a_site_whose_name_merely_starts_the_same()
    {
        var config = _root.Combine("config.json");
        var project = _root.Combine("site");
        UserConfig.SavePrimaryHosts(config, project, [], Languages,
            [new("Gone", new("localhost:5001")), new("Gone@nb", new("localhost:5004")), new("Gone@Home", new("localhost:5009")), new("Site B", new("localhost:5002")), new("Site B@nb", new("localhost:5005"))]);

        Assert.Equal(["Gone", "Gone@nb"], UserConfig.ForgetPrimaryHosts(config, project, "gone", Languages));
        Assert.Equal(["Site B@nb"], UserConfig.ForgetPrimaryHosts(config, project, "Site B@nb", Languages));
        Assert.Empty(UserConfig.ForgetPrimaryHosts(config, project, "Nope", Languages));
        Assert.Equal(["Gone@Home", "Site B"], UserConfig.ForProject(config, project)!.PrimaryHosts!.Keys.Order());
    }

    [Fact]
    public void Saving_a_mapping_for_a_new_project_adds_it()
    {
        var config = _root.Combine("config.json");

        UserConfig.SavePrimaryHosts(config, _root.Combine("site"), ["Site A"], Languages, [new("Site A", new("localhost:5001"))]);

        Assert.Equal("localhost:5001", UserConfig.ForProject(config, _root.Combine("site"))!.PrimaryHosts!["site a"].Host);
    }

    [Fact]
    public void Doctor_compares_the_saved_mapping_with_the_primary_hosts()
    {
        var saved = new Dictionary<string, SavedPrimaryHost>
        {
            ["Site A"] = new("localhost:5001"),
            ["Site A@nb"] = new("localhost:5004"),
            ["Site B"] = new("localhost:5002"),
            ["Gone"] = new("localhost:5009"),
            ["Shop=Main"] = new("http://shop.example"),
        };

        var entries = PrimaryMapping.Compare(saved, Sites, Languages);
        var warnings = PrimaryMapping.Warnings(entries).ToList();

        Assert.Equal(
            [("Gone", PrimaryMapping.NoSite), ("Shop=Main", PrimaryMapping.Differs), ("Site A", PrimaryMapping.Differs), ("Site A@nb", PrimaryMapping.Differs), ("Site B", PrimaryMapping.Matches)],
            entries.Select(e => (e.Key, e.Status)));
        Assert.Contains("Site A's primary is site-a.example; the saved mapping says localhost:5001. Run `opticli sites primary --from-config`.", warnings);
        Assert.Contains("Site A@nb has no primary host; the saved mapping says localhost:5004. Run `opticli sites primary --from-config`.", warnings);
        Assert.Contains(warnings, w => w.StartsWith("Shop=Main's primary is shop.example, with another https setting", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'Gone'", StringComparison.Ordinal) && w.Contains("opticli sites primary --forget \"Gone\"", StringComparison.Ordinal));
        Assert.Equal(4, warnings.Count);
    }

    [Fact]
    public void Doctor_reads_an_entry_without_a_language_as_the_agent_plans_it()
    {
        // After `sites primary "Site D=localhost:5001"` on a site whose hosts are all for nb.
        var d = Site(5, "Site D", new HostInfo("site-d.example", HostType.Undefined, "nb", true), new HostInfo("localhost:5001", HostType.Primary, "nb", null));
        var two = Site(6, "Site E", new HostInfo("site-e.no", HostType.Primary, "nb", null), new HostInfo("site-e.se", HostType.Primary, "sv", null));
        var saved = new Dictionary<string, SavedPrimaryHost> { ["Site D"] = new("localhost:5001"), ["Site E"] = new("localhost:5002") };

        var entries = PrimaryMapping.Compare(saved, [d, two], Languages);

        Assert.Equal([PrimaryMapping.Matches, PrimaryMapping.Differs], entries.Select(e => e.Status));
        Assert.Equal("nb", UnqualifiedPrimary.Language(d.Hosts.Select(SiteHostsRunner.ToSiteHost).ToList(), ["localhost:5009"], Languages));
        Assert.Null(UnqualifiedPrimary.Language(two.Hosts.Select(SiteHostsRunner.ToSiteHost).ToList(), ["localhost:5009"], Languages));
        // A host that is a primary host already keeps its language; a sole primary in a disabled language is for every language.
        Assert.Equal("sv", UnqualifiedPrimary.Language(two.Hosts.Select(SiteHostsRunner.ToSiteHost).ToList(), ["SITE-E.se"], Languages));
        Assert.Null(UnqualifiedPrimary.Language([new SiteHost("site-f.de", HostTypes.Primary, "de", null)], ["localhost:5009"], Languages));
    }

    [Fact]
    public void Doctor_and_from_config_read_an_entry_naming_a_languages_primary_host_as_that_languages()
    {
        // Saved "Site F": "site-f.se", the site's primary host for sv beside one for every language.
        var f = Site(7, "Site F", new HostInfo("site-f.example", HostType.Primary, null, null), new HostInfo("site-f.se", HostType.Primary, "nb", null));
        var saved = new Dictionary<string, SavedPrimaryHost> { ["Site F"] = new("site-f.se") };

        Assert.Equal(PrimaryMapping.Matches, PrimaryMapping.Compare(saved, [f], Languages).Single().Status);
        Assert.Equal("nb", UnqualifiedPrimary.Language(f.Hosts.Select(SiteHostsRunner.ToSiteHost).ToList(), ["site-f.se"], Languages));
    }

    [Fact]
    public void From_config_skips_a_saved_site_that_is_gone_and_keeps_each_entrys_options()
    {
        var warnings = new List<string>();
        var saved = new Dictionary<string, SavedPrimaryHost> { ["Gone"] = new("localhost:5009"), ["Site A"] = new("localhost:5001", KeepSiteUrl: true), ["Site B"] = new("localhost:5002") };

        var pairs = PrimaryMapping.Pairs(saved, Sites, Languages, null, keepEdit: false, keepSiteUrl: false, warnings);
        var withOption = PrimaryMapping.Pairs(saved, Sites, Languages, null, keepEdit: true, keepSiteUrl: false, []);

        Assert.Equal([("Site A", false, true), ("Site B", false, false)], pairs.Select(p => (p.Key, p.KeepEdit, p.KeepSiteUrl)));

        // One entry's option is the site's: the nb entry doesn't demote the Edit host the site's other entry keeps.
        var perSite = PrimaryMapping.Pairs(new Dictionary<string, SavedPrimaryHost> { ["Site A"] = new("localhost:5001", KeepEdit: true), ["Site A@nb"] = new("localhost:5004") }, Sites, Languages, null, false, false, []);
        Assert.All(perSite, p => Assert.True(p.KeepEdit));
        Assert.All(withOption, p => Assert.True(p.KeepEdit));
        Assert.Equal("Skipped 'Gone' from the saved mapping: this database has no such site. Drop it with `opticli sites primary --forget \"Gone\"`.", Assert.Single(warnings));

        var none = Assert.Throws<NotFoundException>(() => PrimaryMapping.Pairs(new Dictionary<string, SavedPrimaryHost> { ["Gone"] = new("localhost:5009") }, Sites, Languages, null, false, false, []));
        Assert.Contains("--forget \"Gone\"", none.Hint);
    }

    [Fact]
    public void A_pairs_options_are_saved_with_it()
    {
        var pair = PrimaryPairs.Parse("Site A=https://localhost:5001/", Sites, Languages) with { KeepSiteUrl = true };

        Assert.Equal(new SavedPrimaryHost("https://localhost:5001", KeepSiteUrl: true), pair.Saved);
    }

    [Fact]
    public void Shared_mode_refuses_everything_but_adding_an_undefined_host_before_the_agent_is_asked()
    {
        SiteHostChange Change(string action, string? type = null) => new() { Site = Sites[0].Key, Host = "localhost:5001", Action = action, Type = type };

        var refused = Assert.Throws<RefusedException>(() => SiteHostsRunner.RequireAllowed([Change(SiteHostActions.Primary)], Sites, sharedDatabase: true));
        Assert.StartsWith("sites primary \"Site A=localhost:5001\": ", refused.Message);
        Assert.Throws<RefusedException>(() => SiteHostsRunner.RequireAllowed([Change(SiteHostActions.Remove)], Sites, sharedDatabase: true));
        Assert.Throws<RefusedException>(() => SiteHostsRunner.RequireAllowed([Change(SiteHostActions.Add, HostTypes.Edit)], Sites, sharedDatabase: true));
        SiteHostsRunner.RequireAllowed([Change(SiteHostActions.Add, HostTypes.Undefined)], Sites, sharedDatabase: true);
        SiteHostsRunner.RequireAllowed([Change(SiteHostActions.Primary)], Sites, sharedDatabase: false);
    }

    [Theory]
    [InlineData(HostTypes.Preview)]
    [InlineData(HostTypes.Media)]
    public void CMS_13s_host_types_are_refused_on_cms_12_before_the_agent_is_asked(string type)
    {
        SiteHostChange[] changes = [new() { Site = Sites[0].Key, Host = "media.localhost", Action = SiteHostActions.Add, Type = type.ToUpperInvariant() }];

        var refused = Assert.Throws<UsageException>(() => SiteHostsRunner.RequireHostTypes(changes, cmsMajor: 12));

        Assert.Equal($"{type} hosts are CMS 13 only; this site runs CMS 12.", refused.Message);
        Assert.Equal($"Host types on CMS 12: {HostTypes.Cms12Syntax}.", refused.Hint);
        SiteHostsRunner.RequireHostTypes(changes, cmsMajor: 13);
        SiteHostsRunner.RequireHostTypes([new() { Site = Sites[0].Key, Host = "localhost:5001", Action = SiteHostActions.Add, Type = HostTypes.Edit }], cmsMajor: 12);
    }

    [Theory]
    [InlineData(HostType.Undefined, HostTypes.Undefined)]
    [InlineData(HostType.Edit, HostTypes.Edit)]
    [InlineData(HostType.Preview, HostTypes.Preview)]
    [InlineData(HostType.Media, HostTypes.Media)]
    public void CMS_13s_host_types_go_to_the_agent_by_name(HostType type, string name)
    {
        // CMS 13 adds preview and media hosts; they used to go out as "type5" and "type6".
        var host = SiteHostsRunner.ToSiteHost(new HostInfo("media.example", type, null, null));

        Assert.Equal(name, host.Type);
        Assert.Equal(type, SiteHostsRunner.ToHostInfo(host).Type);
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("media")]
    public void Preview_and_media_hosts_can_be_given_as_a_type_and_are_cms_13_only(string type)
    {
        // The CLI takes them; the CMS 12 agent refuses them.
        Assert.Equal(type, HostTypes.Parse(type.ToUpperInvariant()));
        Assert.Contains(type, HostTypes.Cms13Only);
        Assert.Contains($"on CMS 13 preview or media", HostTypes.Syntax);
        Assert.DoesNotContain(type, HostTypes.Cms12Syntax);
        Assert.Equal(HostTypes.RedirectPermanent, HostTypes.Parse("redirect-permanent"));
    }

    [Fact]
    public void The_agents_answer_keeps_cms_12s_shape_and_names_a_cms_13_application_by_its_name()
    {
        var hosts = new[] { new SiteHost("localhost:5001", HostTypes.Primary, null, true) };
        var application = new SiteInfo(7, null, "Alloy", "https://localhost:5001/", "5", "Start", "en", "3", [], "Site_CB857AE6", "inProcessWebsite", true);
        var result = new SiteHostsResult
        {
            Sites =
            [
                new SiteHostsSite(Sites[0].Guid, "Site A", SiteHostStatus.Changed, ["x"], hosts, "https://localhost:5001/"),
                new SiteHostsSite(null, "Alloy", SiteHostStatus.Changed, ["y"], hosts, "https://localhost:5001/") { Application = "site_cb857ae6", IsDefault = true },
            ],
        };

        var views = SiteHostsRunner.Views(result, [.. Sites, application]);

        var cms12 = JsonOutput.ToNode(views[0])!.AsObject();
        Assert.Equal(Sites[0].Id, views[0].Id);
        Assert.Equal(["site", "id", "guid", "status", "changes", "url", "hosts"], cms12.Select(p => p.Key));
        var cms13 = JsonOutput.ToNode(views[1])!.AsObject();
        Assert.Equal(7, views[1].Id);
        Assert.Equal(["site", "id", "status", "changes", "url", "hosts", "application", "isDefault"], cms13.Select(p => p.Key));
        Assert.Equal(new HostInfo("localhost:5001", HostType.Primary, null, true), views[1].Hosts.Single());
    }
}
