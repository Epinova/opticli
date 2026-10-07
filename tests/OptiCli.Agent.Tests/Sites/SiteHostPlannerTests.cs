using OptiCli.Agent.Sites;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Sites;

/// <summary>The plan for site host changes, on the sites of a restored production database, without a CMS.</summary>
public class SiteHostPlannerTests
{
    private static readonly Guid A = Guid.Parse("0b1c2d3e-0000-4000-8000-00000000000a");
    private static readonly Guid B = Guid.Parse("0b1c2d3e-0000-4000-8000-00000000000b");
    private static readonly Guid C = Guid.Parse("0b1c2d3e-0000-4000-8000-00000000000c");

    private static readonly string[] Languages = ["en", "nb", "sv"];

    private static SiteHost Host(string name, string type = HostTypes.Undefined, string? language = null, bool? https = null) => new(name, type, language, https);

    /// <summary>Sites A, B and C of a multi-site solution, as a production copy has them.</summary>
    private static List<SiteState> Production() =>
    [
        new(A, "Site A", "https://site-a.example/", [Host("*"), Host("site-a.example", HostTypes.Primary)]),
        new(B, "Site B", "https://site-b.example/", [Host("site-b.example", HostTypes.Primary), Host("www.site-b.example")]),
        new(C, "Site C", "https://site-c.example/", [Host("site-c.example", HostTypes.Primary), Host("edit.site-c.example", HostTypes.Edit)]),
    ];

    private static SiteHostChange Primary(string site, string host, string? language = null, string? https = null, bool keepEdit = false, bool keepSiteUrl = false) =>
        new() { Site = site, Host = host, Action = SiteHostActions.Primary, Language = language, Https = https, KeepEdit = keepEdit, KeepSiteUrl = keepSiteUrl };

    private static SiteHostChange Add(string site, string host, string? type = null, string? language = null, string? https = null) =>
        new() { Site = site, Host = host, Action = SiteHostActions.Add, Type = type, Language = language, Https = https };

    private static SiteHostChange Remove(string site, string host) => new() { Site = site, Host = host, Action = SiteHostActions.Remove };

    private static SiteHostPlan Plan(IReadOnlyList<SiteState> sites, params SiteHostChange[] changes) => SiteHostPlanner.Plan(sites, changes, Languages, sharedDatabase: false);

    private static AgentException Fails(IReadOnlyList<SiteState> sites, params SiteHostChange[] changes) =>
        Assert.Throws<AgentException>(() => Plan(sites, changes));

    private static string Messages(AgentException error) => string.Join("\n", error.Validation?.Select(v => v.Message) ?? [error.Message]);

    [Fact]
    public void Primary_adds_the_host_and_demotes_the_previous_primary_and_the_edit_host()
    {
        var plan = Plan(Production(), Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:5002"), Primary("Site C", "localhost:5003"));

        Assert.Equal(["Site A", "Site B", "Site C"], plan.Sites.Select(s => s.After.Name));
        Assert.All(plan.Sites, s => Assert.True(s.Changed));
        var a = plan.Sites[0].After;
        Assert.Equal([Host("*"), Host("site-a.example"), Host("localhost:5001", HostTypes.Primary)], a.Hosts);
        Assert.Equal("https://localhost:5001/", a.SiteUrl);
        Assert.Equal(
            ["added localhost:5001 (primary)", "site-a.example: primary → undefined", "SiteUrl: https://site-a.example/ → https://localhost:5001/"],
            plan.Sites[0].Changes);
        var c = plan.Sites[2].After;
        Assert.Equal([Host("site-c.example"), Host("edit.site-c.example"), Host("localhost:5003", HostTypes.Primary)], c.Hosts);
        Assert.Contains("edit.site-c.example: edit → undefined", plan.Sites[2].Changes);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void A_second_run_on_the_result_changes_nothing()
    {
        var changes = new[] { Primary("Site A", "localhost:5001"), Primary("Site C", "https://localhost:5003/") };
        var first = Plan(Production(), changes);
        var after = Production().Select(s => first.Sites.FirstOrDefault(p => p.After.Id == s.Id)?.After ?? s).ToList();

        var second = Plan(after, changes);

        Assert.All(second.Sites, s => Assert.False(s.Changed));
        Assert.All(second.Sites, s => Assert.Empty(s.Changes));
    }

    [Fact]
    public void Keep_edit_and_keep_site_url_leave_them_as_they_are()
    {
        var plan = Plan(Production(), Primary("Site C", "localhost:5003", keepEdit: true, keepSiteUrl: true));

        var c = plan.Sites.Single().After;
        Assert.Equal(HostTypes.Edit, c.Hosts.Single(h => h.Name == "edit.site-c.example").Type);
        Assert.Equal("https://site-c.example/", c.SiteUrl);
        Assert.DoesNotContain(plan.Sites.Single().Changes, line => line.StartsWith("SiteUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void An_existing_host_is_made_primary_and_keeps_its_https_setting_unless_one_is_given()
    {
        var sites = Production();
        sites[1] = sites[1] with { Hosts = [.. sites[1].Hosts, Host("localhost:5002", https: false)] };

        var kept = Plan(sites, Primary("Site B", "localhost:5002")).Sites.Single();
        var changed = Plan(sites, Primary("Site B", "localhost:5002", https: HostHttps.Unset)).Sites.Single();

        Assert.Equal(Host("localhost:5002", HostTypes.Primary, https: false), kept.After.Hosts[^1]);
        Assert.Equal("http://localhost:5002/", kept.After.SiteUrl);
        Assert.Contains("localhost:5002: undefined, http → primary, http", kept.Changes);
        Assert.Equal(Host("localhost:5002", HostTypes.Primary), changed.After.Hosts[^1]);
        Assert.Equal("https://localhost:5002/", changed.After.SiteUrl);
    }

    [Fact]
    public void The_scheme_of_a_url_sets_https_and_the_host_is_normalised()
    {
        var plan = Plan(Production(), Primary("site a", "HTTP://LocalHost:5001"));

        var a = plan.Sites.Single().After;
        Assert.Equal(Host("localhost:5001", HostTypes.Primary, https: false), a.Hosts[^1]);
        Assert.Equal("http://localhost:5001/", a.SiteUrl);
    }

    [Fact]
    public void A_language_pair_leaves_the_site_url_and_other_languages_alone()
    {
        var sites = Production();
        sites[0] = sites[0] with { Hosts = [.. sites[0].Hosts, Host("site-a.no", HostTypes.Primary, "nb"), Host("site-a.se", HostTypes.Primary, "sv")] };

        var plan = Plan(sites, Primary("Site A", "localhost:5001"), Primary("Site A", "localhost:5004", "NB"));

        var a = plan.Sites.Single().After;
        Assert.Equal(Host("localhost:5004", HostTypes.Primary, "nb"), a.Hosts.Single(h => h.Name == "localhost:5004"));
        Assert.Equal(HostTypes.Undefined, a.Hosts.Single(h => h.Name == "site-a.no").Type);
        Assert.Equal(HostTypes.Primary, a.Hosts.Single(h => h.Name == "site-a.se").Type);
        Assert.Equal("https://localhost:5001/", a.SiteUrl);
        Assert.Equal("Site A's sv URLs still use site-a.se; add `Site A@sv=localhost:<port>`.", Assert.Single(plan.Warnings));
    }

    [Fact]
    public void A_host_another_site_has_is_a_validation_error_naming_that_site_and_the_pair()
    {
        var error = Fails(Production(), Primary("Site A", "localhost:5001"), Primary("Site B", "localhost:5001"));

        Assert.Equal(AgentErrorCodes.Validation, error.Code);
        Assert.Equal(AgentErrorReasons.SiteHosts, error.Reason);
        var issue = Assert.Single(error.Validation!);
        Assert.StartsWith("Site B=localhost:5001: localhost:5001 belongs to Site A", issue.Message);
    }

    [Fact]
    public void Every_failing_pair_is_reported_and_nothing_is_planned()
    {
        var error = Fails(Production(), Primary("Site A", "site-b.example"), Primary("Site B", "localhost:70000"), Primary("Site C", "localhost:5003", "de"));

        var messages = Messages(error);
        Assert.Equal(3, error.Validation!.Count);
        Assert.Contains("Site A=site-b.example: site-b.example belongs to Site B", messages);
        Assert.Contains("Site B=localhost:70000: 'localhost:70000' has the port '70000'", messages);
        Assert.Contains("Site C@de=localhost:5003: 'de' is not an enabled language (enabled: en, nb, sv).", messages);
    }

    [Theory]
    [InlineData("localhost:0")]
    [InlineData("localhost:port")]
    [InlineData("https://localhost:5001/path")]
    [InlineData("localhost:5001?x=1")]
    [InlineData("ftp://localhost")]
    [InlineData("local host")]
    public void Bad_host_names_are_validation_errors(string host)
    {
        var error = Fails(Production(), Primary("Site A", host));

        Assert.Equal(AgentErrorCodes.Validation, error.Code);
        Assert.StartsWith($"Site A={host}: ", error.Validation![0].Message);
    }

    [Fact]
    public void The_wildcard_host_can_be_on_one_site_only_and_never_primary()
    {
        var twice = Fails(Production(), Add("Site B", "*"));
        var primary = Fails(Production(), Primary("Site A", "*"));

        Assert.Contains("Site A has the * host already", Messages(twice));
        Assert.Contains("it can't be a primary host", Messages(primary));
    }

    [Fact]
    public void A_site_that_already_breaks_the_cms_rules_is_reported_before_it_is_saved()
    {
        var sites = Production();
        sites[1] = sites[1] with { Hosts = [Host("site-b.example", HostTypes.Primary), Host("www.site-b.example", HostTypes.Primary)] };

        var error = Fails(sites, Add("Site B", "localhost:5002"));

        Assert.Contains("host add Site B localhost:5002: Site B would have 2 primary hosts for every language: site-b.example, www.site-b.example.", Messages(error));
    }

    [Fact]
    public void An_unknown_site_is_not_found_with_close_names()
    {
        var error = Fails(Production(), Primary("Site X", "localhost:5001"));

        Assert.Equal(AgentErrorCodes.NotFound, error.Code);
        Assert.Contains("Site A", error.Hint);
    }

    [Fact]
    public void Sites_are_found_by_guid_and_named_in_messages()
    {
        var error = Fails(Production(), Primary(B.ToString(), "site-a.example"));

        Assert.StartsWith("Site B=site-a.example: ", error.Validation![0].Message);
    }

    [Fact]
    public void Add_takes_a_type_and_demotes_the_previous_one()
    {
        var plan = Plan(Production(), Add("Site C", "localhost:5003", "edit"), Add("Site B", "localhost:5002", HostTypes.Primary));

        var c = plan.Sites[0];
        Assert.Equal(HostTypes.Undefined, c.After.Hosts.Single(h => h.Name == "edit.site-c.example").Type);
        Assert.Equal(["added localhost:5003 (edit)", "edit.site-c.example: edit → undefined"], c.Changes);
        var b = plan.Sites[1];
        Assert.Equal(HostTypes.Undefined, b.After.Hosts.Single(h => h.Name == "site-b.example").Type);
        Assert.Equal("https://site-b.example/", b.After.SiteUrl);
        Assert.Contains(plan.Warnings, w => w.Contains("Site B's SiteUrl stays https://site-b.example/", StringComparison.Ordinal));
    }

    [Fact]
    public void Adding_a_host_the_site_has_is_a_conflict()
    {
        var error = Fails(Production(), Add("Site B", "WWW.site-b.example"));

        Assert.Equal(AgentErrorCodes.Conflict, error.Code);
        Assert.Contains("sites primary \"Site B=www.site-b.example\"", error.Hint);
    }

    [Fact]
    public void An_edit_host_is_for_every_language()
    {
        Assert.Contains("an Edit host is for every language", Messages(Fails(Production(), Add("Site A", "localhost:5001", "edit", "nb"))));
    }

    [Fact]
    public void Remove_takes_one_host_but_never_the_last_one_or_the_site_urls()
    {
        var sites = Production();
        sites[0] = sites[0] with { Hosts = [.. sites[0].Hosts, Host("localhost:5001")] };

        var removed = Plan(sites, Remove("Site A", "localhost:5001")).Sites.Single();
        var siteUrl = Fails(sites, Remove("Site A", "site-a.example"));
        var last = Fails([new(A, "Site A", "https://localhost:5001/", [Host("localhost:5001", HostTypes.Primary)])], Remove("Site A", "localhost:5001"));
        var missing = Fails(sites, Remove("Site A", "localhost:5002"));

        Assert.Equal(["removed localhost:5001 (undefined)"], removed.Changes);
        Assert.Equal(2, removed.After.Hosts.Count);
        Assert.Contains("the CMS adds back", Messages(siteUrl));
        Assert.Equal(AgentErrorCodes.Refused, last.Code);
        Assert.Equal(AgentErrorCodes.NotFound, missing.Code);
        Assert.Contains("localhost:5001", missing.Hint);
    }

    [Fact]
    public void Removing_the_wildcard_host_warns()
    {
        var plan = Plan(Production(), Remove("Site A", "*"));

        Assert.Contains("Site A no longer has the * host", Assert.Single(plan.Warnings));
    }

    [Fact]
    public void Shared_mode_only_allows_adding_an_undefined_host()
    {
        AgentException Refused(SiteHostChange change) =>
            Assert.Throws<AgentException>(() => SiteHostPlanner.Plan(Production(), [change], Languages, sharedDatabase: true));

        Assert.Equal(AgentErrorCodes.Refused, Refused(Primary("Site A", "localhost:5001")).Code);
        Assert.Equal(AgentErrorCodes.Refused, Refused(Remove("Site B", "www.site-b.example")).Code);
        var primary = Refused(Add(A.ToString(), "localhost:5001", "primary"));
        Assert.StartsWith("host add Site A localhost:5001 --type primary: ", primary.Message);
        Assert.Contains("sites host add <site> localhost:<port>", primary.Hint);

        var added = SiteHostPlanner.Plan(Production(), [Add("Site A", "localhost:5001")], Languages, sharedDatabase: true);
        Assert.True(added.Sites.Single().Changed);
    }

    [Fact]
    public void Bad_actions_types_and_settings_are_usage_errors()
    {
        Assert.Equal(AgentErrorCodes.Usage, Fails(Production(), new SiteHostChange { Site = "Site A", Host = "x", Action = "move" }).Code);
        Assert.Equal(AgentErrorCodes.Usage, Fails(Production(), Add("Site A", "localhost:5001", "main")).Code);
        Assert.Equal(AgentErrorCodes.Usage, Fails(Production(), Primary("Site A", "localhost:5001", https: "yes")).Code);
        Assert.Equal(AgentErrorCodes.Usage, Assert.Throws<AgentException>(() => SiteHostPlanner.Plan(Production(), [], Languages, false)).Code);
    }

    [Fact]
    public void Redirect_types_are_accepted_with_or_without_dashes_and_printed_as_the_cli_spells_them()
    {
        var plan = Plan(Production(), Add("Site B", "old.site-b.example", "redirectPermanent"));

        Assert.Equal(HostTypes.RedirectPermanent, plan.Sites.Single().After.Hosts[^1].Type);
        Assert.Equal(["added old.site-b.example (redirect-permanent)"], plan.Sites.Single().Changes);
    }

    [Fact]
    public void A_default_port_is_left_out_so_the_plan_is_what_the_cms_stores_and_a_rerun_changes_nothing()
    {
        var first = Plan(Production(), Primary("Site B", "localhost:443"));
        var b = first.Sites.Single().After;

        Assert.Equal(Host("localhost", HostTypes.Primary, https: true), b.Hosts[^1]);
        Assert.Equal("https://localhost/", b.SiteUrl);
        Assert.DoesNotContain(first.Sites.Single().Changes, c => c.Contains("CMS adds", StringComparison.Ordinal));

        var after = Production().Select(s => s.Id == B ? b : s).ToList();
        Assert.False(Plan(after, Primary("Site B", "localhost:443")).Sites.Single().Changed);
        Assert.False(Plan(after, Primary("Site B", "https://localhost/")).Sites.Single().Changed);
        Assert.Equal("localhost:5001", Plan(Production(), Primary("Site A", "localhost:05001")).Sites.Single().After.Hosts[^1].Name);
    }

    [Fact]
    public void The_host_the_cms_adds_for_site_url_is_in_the_plan_and_checked_against_the_other_sites()
    {
        // A site as admin mode can leave it: SiteUrl on a host the site doesn't list by that name.
        var sites = Production();
        sites[0] = sites[0] with { SiteUrl = "https://localhost/", Hosts = [Host("*"), Host("localhost:443", HostTypes.Primary)] };
        sites[1] = sites[1] with { Hosts = [.. sites[1].Hosts, Host("localhost")] };

        var error = Fails(sites, Add("Site A", "localhost:5001"));
        var alone = Plan([sites[0]], Add("Site A", "localhost:5001")).Sites.Single();

        Assert.Contains("localhost belongs to Site B", Messages(error));
        Assert.Equal(Host("localhost"), alone.After.Hosts[^1]);
        Assert.Contains("added localhost (undefined): SiteUrl's host, which the CMS adds when it saves the site", alone.Changes);
    }

    [Fact]
    public void Two_sites_on_the_same_host_written_differently_fail_before_anything_is_saved()
    {
        var sites = Production();
        sites[0] = sites[0] with { Hosts = [.. sites[0].Hosts, Host("localhost")] };

        var error = Fails(sites, Primary("Site A", "localhost:5871"), Primary("Site B", "localhost:443"));

        Assert.Contains("Site B=localhost:443: localhost belongs to Site A", Messages(error));
    }

    [Theory]
    // The https setting is the scheme the host is reached by: only that scheme's default port goes.
    [InlineData("localhost:443", HostHttps.False, "localhost:443", false, "http://localhost:443/")]
    [InlineData("localhost:80", HostHttps.True, "localhost:80", true, "https://localhost:80/")]
    [InlineData("http://localhost:443", null, "localhost:443", false, "http://localhost:443/")]
    [InlineData("https://localhost:80/", null, "localhost:80", true, "https://localhost:80/")]
    [InlineData("localhost:443", HostHttps.True, "localhost", true, "https://localhost/")]
    [InlineData("localhost:443", null, "localhost", true, "https://localhost/")]
    [InlineData("localhost:443", HostHttps.Unset, "localhost", null, "https://localhost/")]
    [InlineData("localhost:80", HostHttps.Unset, "localhost", null, "https://localhost/")]
    public void A_port_is_dropped_only_when_it_is_the_default_of_the_hosts_scheme(string host, string? https, string name, bool? flag, string siteUrl)
    {
        var plan = Plan(Production(), Primary("Site A", host, https: https), Add("Site B", host.Replace("localhost", "b.localhost", StringComparison.Ordinal), https: https));

        Assert.Equal(Host(name, HostTypes.Primary, https: flag), plan.Sites[0].After.Hosts[^1]);
        Assert.Equal(siteUrl, plan.Sites[0].After.SiteUrl);
        Assert.Equal(Host($"b.{name}", https: flag), plan.Sites[1].After.Hosts[^1]);
    }

    [Fact]
    public void A_host_stored_with_a_default_port_is_found_by_that_name()
    {
        var sites = Production();
        sites[1] = sites[1] with { Hosts = [.. sites[1].Hosts, Host("www.site-b.example:443"), Host("old.site-b.example:80")] };

        var removed = Plan(sites, Remove("Site B", "www.site-b.example:443")).Sites.Single();
        var primary = Plan(sites, Primary("Site B", "old.site-b.example:80")).Sites.Single();

        Assert.Equal(["removed www.site-b.example:443 (undefined)"], removed.Changes);
        Assert.Contains("old.site-b.example:80: undefined → primary", primary.Changes);
        Assert.DoesNotContain(primary.After.Hosts, h => h.Name == "old.site-b.example" && h.Type == HostTypes.Primary);
        Assert.Equal(AgentErrorCodes.Conflict, Fails(sites, Add("Site B", "www.site-b.example:443")).Code);
    }

    [Fact]
    public void A_port_alone_keeps_an_existing_hosts_https_setting_and_only_says_the_scheme_of_a_new_host()
    {
        // sites primary "Site B=http://localhost:443", then "Site B=localhost:443".
        var first = Plan(Production(), Primary("Site B", "http://localhost:443")).Sites.Single().After;
        var after = Production().Select(s => s.Id == B ? first : s).ToList();

        var again = Plan(after, Primary("Site B", "localhost:443")).Sites.Single();

        Assert.Equal((Host("localhost:443", HostTypes.Primary, https: false), "http://localhost:443/"), (first.Hosts[^1], first.SiteUrl));
        Assert.False(again.Changed);
        Assert.DoesNotContain(again.After.Hosts, h => h.Name == "localhost");
        Assert.Equal(Host("localhost", HostTypes.Primary, https: true), Plan(Production(), Primary("Site B", "localhost:443")).Sites.Single().After.Hosts[^1]);
        // Asked for explicitly, https applies: then SiteUrl is https://localhost/, and the CMS adds localhost too.
        var https = Plan(after, Primary("Site B", "localhost:443", https: HostHttps.True)).Sites.Single().After;
        Assert.Equal(Host("localhost:443", HostTypes.Primary, https: true), https.Hosts.Single(h => h.Name == "localhost:443"));
        Assert.Equal(Host("localhost"), https.Hosts[^1]);
    }

    [Theory]
    // The site has localhost; a default port typed with it says the scheme, as a typed scheme does.
    [InlineData(false, "localhost:443", true, "https://localhost/")]
    [InlineData(true, "localhost:80", false, "http://localhost/")]
    [InlineData(false, "localhost:80", false, "http://localhost/")]
    public void A_default_port_for_a_host_the_site_has_without_it_sets_that_hosts_scheme(bool https, string typed, bool expected, string siteUrl)
    {
        var sites = Production();
        sites[1] = sites[1] with { Hosts = [.. sites[1].Hosts, Host("localhost", https: https)] };

        var b = Plan(sites, Primary("Site B", typed)).Sites.Single().After;

        Assert.Equal(Host("localhost", HostTypes.Primary, https: expected), b.Hosts.Single(h => h.Name == "localhost"));
        Assert.Equal(siteUrl, b.SiteUrl);
        Assert.Equal(sites[1].Hosts.Count, b.Hosts.Count);
    }

    [Fact]
    public void A_default_port_that_is_part_of_the_stored_name_keeps_the_hosts_setting()
    {
        var sites = Production();
        sites[1] = sites[1] with { Hosts = [.. sites[1].Hosts, Host("localhost:443", https: false)] };

        var b = Plan(sites, Primary("Site B", "localhost:443")).Sites.Single().After;

        Assert.Equal(Host("localhost:443", HostTypes.Primary, https: false), b.Hosts.Single(h => h.Name == "localhost:443"));
        Assert.Equal("http://localhost:443/", b.SiteUrl);
    }

    private static readonly Guid D = Guid.Parse("0b1c2d3e-0000-4000-8000-00000000000d");

    /// <summary>A site whose hosts are all bound to one language, with no primary host for every language.</summary>
    private static List<SiteState> OneLanguage() =>
    [
        new(D, "Site D", "https://site-d.example/", [Host("site-d.example", HostTypes.Primary, "nb", true), Host("www.site-d.example", language: "nb", https: true)]),
    ];

    /// <summary>A site with primary hosts for two languages only; SiteUrl on the nb one.</summary>
    private static List<SiteState> TwoLanguages() =>
    [
        new(D, "Site D", "https://site-d.no/", [Host("site-d.no", HostTypes.Primary, "nb", true), Host("site-d.se", HostTypes.Primary, "sv", true)]),
    ];

    [Fact]
    public void A_pair_without_a_language_replaces_a_sites_only_primary_host_in_its_language()
    {
        var plan = Plan(OneLanguage(), Primary("Site D", "localhost:5001"));
        var d = plan.Sites.Single();

        Assert.Equal(Host("localhost:5001", HostTypes.Primary, "nb"), d.After.Hosts[^1]);
        Assert.Equal(Host("site-d.example", language: "nb", https: true), d.After.Hosts[0]);
        Assert.Equal("https://localhost:5001/", d.After.SiteUrl);
        Assert.Contains("localhost:5001 is the primary host for nb: on this site a pair without @lang replaces the primary host for nb", d.Changes);
        Assert.Empty(plan.Warnings);

        var again = Plan([d.After], Primary("Site D", "localhost:5001")).Sites.Single();
        Assert.False(again.Changed);
    }

    [Fact]
    public void With_primary_hosts_for_several_languages_only_a_pair_without_one_is_for_every_language_and_warns_with_the_pairs()
    {
        var plan = Plan(TwoLanguages(), Primary("Site D", "localhost:5001"));

        Assert.Equal(Host("localhost:5001", HostTypes.Primary), plan.Sites.Single().After.Hosts[^1]);
        Assert.Equal(
            ["Site D's nb URLs still use site-d.no; add `Site D@nb=localhost:<port>`.", "Site D's sv URLs still use site-d.se; add `Site D@sv=localhost:<port>`."],
            plan.Warnings);
    }

    [Fact]
    public void A_language_pair_moves_site_url_when_it_replaces_the_primary_host_site_url_is_on()
    {
        Assert.Equal("https://localhost:5004/", Plan(TwoLanguages(), Primary("Site D", "localhost:5004", "nb")).Sites.Single().After.SiteUrl);
        Assert.Equal("https://site-d.no/", Plan(TwoLanguages(), Primary("Site D", "localhost:5005", "sv")).Sites.Single().After.SiteUrl);
        Assert.Equal("https://site-d.no/", Plan(TwoLanguages(), Primary("Site D", "localhost:5004", "nb", keepSiteUrl: true)).Sites.Single().After.SiteUrl);
    }

    [Fact]
    public void A_pair_without_a_language_on_a_languages_primary_host_beside_one_for_every_language_leaves_site_url()
    {
        var sites = new List<SiteState>
        {
            new(A, "Site A", "https://site-a.example/", [Host("site-a.example", HostTypes.Primary), Host("site-a.se", HostTypes.Primary, "sv"), Host("edit.site-a.example", HostTypes.Edit)]),
        };

        var plan = Plan(sites, Primary("Site A", "site-a.se"));
        var a = plan.Sites.Single();

        Assert.Equal("https://site-a.example/", a.After.SiteUrl);
        Assert.Equal(Host("site-a.example", HostTypes.Primary), a.After.Hosts[0]);
        Assert.Equal(Host("site-a.se", HostTypes.Primary, "sv"), a.After.Hosts[1]);
        // As for Site A@sv=site-a.se: every primary pair makes the (language-invariant) Edit host undefined.
        Assert.Equal(["edit.site-a.example: edit → undefined"], a.Changes);
        var note = "Site A=site-a.se was read as `Site A@sv=site-a.se`: site-a.se is Site A's primary host for sv, beside its primary host for every language, so SiteUrl stays https://site-a.example/, as for that pair.";
        Assert.Contains(plan.Warnings, w => w.StartsWith(note, StringComparison.Ordinal));
        Assert.True(SiteHostPlanner.Same(Plan(sites, Primary("Site A", "site-a.se", "sv")).Sites.Single().After, a.After));

        var again = Plan([a.After], Primary("Site A", "site-a.se"));
        Assert.False(again.Sites.Single().Changed);
        Assert.Contains(again.Warnings, w => w.StartsWith(note, StringComparison.Ordinal));
    }

    [Fact]
    public void A_pair_without_a_language_still_moves_site_url_on_a_site_whose_hosts_are_all_for_one_language()
    {
        // SiteUrl on the language's undefined host, not its primary one: the pair replaces the site's primary host all the same.
        var sites = OneLanguage();
        sites[0] = sites[0] with { SiteUrl = "https://www.site-d.example/" };

        var d = Plan(sites, Primary("Site D", "localhost:5001")).Sites.Single();

        Assert.Equal("https://localhost:5001/", d.After.SiteUrl);
        Assert.False(Plan([d.After], Primary("Site D", "localhost:5001")).Sites.Single().Changed);
    }

    [Fact]
    public void A_pair_without_a_language_is_for_the_same_language_whatever_comes_before_it_in_the_batch()
    {
        SiteHostChange[] changes = [Primary("Site D", "localhost:5882"), Primary("Site D", "localhost:5881", "sv")];

        var first = Plan(OneLanguage(), changes).Sites.Single().After;
        var reversed = Plan(OneLanguage(), [changes[1], changes[0]]).Sites.Single().After;

        Assert.Equal(first.Hosts.OrderBy(h => h.Name), reversed.Hosts.OrderBy(h => h.Name));
        Assert.Equal(first.SiteUrl, reversed.SiteUrl);
        Assert.Equal(Host("localhost:5882", HostTypes.Primary, "nb"), first.Hosts.Single(h => h.Name == "localhost:5882"));
        Assert.Equal(HostTypes.Undefined, first.Hosts.Single(h => h.Name == "site-d.example").Type);

        // Run again on the result, which now has primary hosts for nb and sv: localhost:5882 keeps nb.
        Assert.False(Plan([first], changes).Sites.Single().Changed);
        Assert.False(Plan([first], [changes[1], changes[0]]).Sites.Single().Changed);
        Assert.False(Plan([first], changes[0]).Sites.Single().Changed);
    }

    [Fact]
    public void The_warning_for_a_language_still_on_production_names_the_pair_that_works()
    {
        // A production name made primary: the site's only primary host is for nb, so the pair to fix it has no @lang.
        var plan = Plan(OneLanguage(), Primary("Site D", "www.site-d.example"));

        Assert.Equal("Site D's nb URLs still use www.site-d.example; add `Site D=localhost:<port>`.", Assert.Single(plan.Warnings));
    }

    [Fact]
    public void A_sites_only_primary_host_in_a_language_that_isnt_enabled_gets_a_primary_host_for_every_language()
    {
        var sites = new List<SiteState> { new(D, "Site D", "https://site-d.de/", [Host("site-d.de", HostTypes.Primary, "de", true)]) };

        var plan = Plan(sites, Primary("Site D", "localhost:5001"));

        Assert.Equal(Host("localhost:5001", HostTypes.Primary), plan.Sites.Single().After.Hosts[^1]);
        Assert.Contains("Site D's only primary host is for 'de', which isn't an enabled language: the new primary host is for every language.", plan.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pair_without_a_language_and_one_for_the_language_it_covers_are_refused_together(bool languageFirst)
    {
        SiteHostChange[] changes = [Primary("Site D", "localhost:5001"), Primary("Site D", "localhost:5001", "nb")];

        var error = Fails(OneLanguage(), languageFirst ? [changes[1], changes[0]] : changes);

        Assert.Contains("without @lang a pair is for nb here", Messages(error));
        Assert.Contains("give `Site=<host>` or `Site@<lang>=<host>`, not both", error.Hint);
    }

    [Fact]
    public void A_new_site_url_keeps_the_sites_path()
    {
        var sites = Production();
        sites[0] = sites[0] with { SiteUrl = "https://site-a.example/app/" };

        Assert.Equal("https://localhost:5001/app/", Plan(sites, Primary("Site A", "localhost:5001")).Sites.Single().After.SiteUrl);
    }

    [Fact]
    public void Two_primary_hosts_for_one_site_and_language_in_one_batch_are_refused()
    {
        var error = Fails(Production(), Primary("Site A", "localhost:5001"), Primary("Site A", "localhost:5011"));
        var languages = Plan(Production(), Primary("Site A", "localhost:5001"), Primary("Site A", "localhost:5004", "nb"));

        Assert.Contains("Site A=localhost:5011: an earlier change already makes a primary host of Site A for every language", Messages(error));
        Assert.True(languages.Sites.Single().Changed);
    }

    [Fact]
    public void An_ipv6_host_is_refused_as_the_cms_cant_store_it()
    {
        Assert.Contains("IPv6", Messages(Fails(Production(), Primary("Site A", "[::1]:5001"))));
    }

    // CMS 13: the sites are applications. The planner runs the same rules, without SiteUrl or the * host or an unset https.

    /// <summary>
    /// Applications as a restored CMS 13 production database has them: named by their application name (an upgraded
    /// site's reads <c>Site_&lt;GUID&gt;</c>), hosts listed by name and always http or https, the default one with <c>*</c>.
    /// </summary>
    private static List<SiteState> Applications() =>
    [
        new("Site_0B1C2D3E_0000_4000_8000_00000000000A", "Site A", "https://site-a.example/", [Host("*"), Host("site-a.example", HostTypes.Primary, https: true), Host("site-a.se", HostTypes.Primary, "sv", true)]),
        new("siteB", "Site B", "http://site-b.example/", [Host("site-b.example", HostTypes.Primary, https: false), Host("www.site-b.example", https: false)]),
    ];

    private static SiteHostPlan PlanApplications(IReadOnlyList<SiteState> sites, params SiteHostChange[] changes) =>
        SiteHostPlanner.Plan(sites, changes, Languages, sharedDatabase: false, applications: true);

    private static AgentException FailsApplications(IReadOnlyList<SiteState> sites, params SiteHostChange[] changes) =>
        Assert.Throws<AgentException>(() => PlanApplications(sites, changes));

    [Fact]
    public void An_application_is_found_by_its_display_name_or_its_application_name()
    {
        Assert.Equal("Site A", PlanApplications(Applications(), Primary("site_0b1c2d3e_0000_4000_8000_00000000000a", "localhost:5001")).Sites.Single().After.Name);
        Assert.Equal("siteB", PlanApplications(Applications(), Primary("site b", "localhost:5002")).Sites.Single().After.Key);
        Assert.Contains("a CMS 13 application's name", FailsApplications(Applications(), Primary("Nowhere", "localhost:5001")).Hint);
    }

    [Fact]
    public void A_primary_pair_on_an_application_gives_the_new_host_https_and_its_url_follows_the_hosts()
    {
        var plan = PlanApplications(Applications(), Primary("Site A", "localhost:5001"));
        var a = plan.Sites.Single();

        // By name, as the CMS lists them; the * (default application) stays.
        Assert.Equal(
            [Host("*"), Host("localhost:5001", HostTypes.Primary, https: true), Host("site-a.example", https: true), Host("site-a.se", HostTypes.Primary, "sv", true)],
            a.After.Hosts);
        // The first primary host by name: localhost:5001, before site-a.se.
        Assert.Equal("https://localhost:5001/", a.After.SiteUrl);
        Assert.Equal(
            ["added localhost:5001 (primary, https)", "site-a.example: primary, https → undefined, https", "URL: https://site-a.example/ → https://localhost:5001/"],
            a.Changes);
        Assert.Contains(plan.Warnings, w => w.StartsWith("Site A's sv URLs still use site-a.se", StringComparison.Ordinal));

        var again = PlanApplications(Applications().Select(s => s.Key == a.After.Key ? a.After : s).ToList(), Primary("Site A", "localhost:5001"));
        Assert.False(again.Sites.Single().Changed);
        Assert.Empty(again.Sites.Single().Changes);
    }

    [Fact]
    public void A_new_host_on_an_application_gets_the_scheme_of_its_url_unless_one_is_given()
    {
        // Site B's URL is http: a language's primary host (which the URL doesn't follow) and an added host are http too.
        var b = PlanApplications(Applications(), Primary("Site B", "localhost:5004", "nb"), Add("Site B", "localhost:5005"), Add("Site B", "https://localhost:5006/")).Sites.Single().After;

        Assert.Equal(Host("localhost:5004", HostTypes.Primary, "nb", false), b.Hosts.Single(h => h.Name == "localhost:5004"));
        Assert.Equal(Host("localhost:5005", https: false), b.Hosts.Single(h => h.Name == "localhost:5005"));
        Assert.Equal(Host("localhost:5006", https: true), b.Hosts.Single(h => h.Name == "localhost:5006"));
        // The CMS's rule: the first primary host by name, whatever its language.
        Assert.Equal("http://localhost:5004/", b.SiteUrl);
    }

    [Fact]
    public void Keep_site_url_and_an_unset_https_are_refused_on_applications()
    {
        var keep = FailsApplications(Applications(), Primary("Site A", "localhost:5001", keepSiteUrl: true));
        var unset = FailsApplications(Applications(), Add("Site A", "localhost:5001", https: HostHttps.Unset));

        Assert.Equal((AgentErrorCodes.Usage, AgentErrorCodes.Usage), (keep.Code, unset.Code));
        Assert.StartsWith("keepSiteUrl doesn't apply on CMS 13", keep.Message, StringComparison.Ordinal);
        Assert.StartsWith("https unset doesn't apply on CMS 13", unset.Message, StringComparison.Ordinal);
        // CMS 12 takes both.
        Assert.True(Plan(Production(), Primary("Site A", "localhost:5001", keepSiteUrl: true), Add("Site B", "localhost:5002", https: HostHttps.Unset)).Sites.All(s => s.Changed));
    }

    [Fact]
    public void The_host_of_an_applications_url_can_be_removed_and_the_url_moves_to_the_next_one()
    {
        var b = PlanApplications(Applications(), Remove("Site B", "site-b.example")).Sites.Single();

        Assert.Equal([Host("www.site-b.example", https: false)], b.After.Hosts);
        Assert.Equal("http://www.site-b.example/", b.After.SiteUrl);
        Assert.Equal(["removed site-b.example (primary, http)", "URL: http://site-b.example/ → http://www.site-b.example/"], b.Changes);
        // CMS 12 refuses it: the CMS adds SiteUrl's host back.
        Assert.Contains("SiteUrl", Messages(Fails(Production(), Remove("Site B", "site-b.example"))));
    }

    [Fact]
    public void The_star_host_of_an_application_is_its_being_the_default_one()
    {
        var moved = PlanApplications(Applications(), Remove("Site A", "*"), Add("Site B", "*"));

        Assert.Equal(["no longer the default application (*)"], moved.Sites[0].Changes);
        Assert.Equal(["made it the default application (*), which answers host names no application has"], moved.Sites[1].Changes);
        Assert.True(moved.Sites[0].RemovesHosts);
        Assert.Contains(moved.Warnings, w => w.StartsWith("Site A is no longer the default application", StringComparison.Ordinal));

        // Only one at a time, a * with settings is refused, and the default application keeps a host of its own.
        Assert.Contains("Site A is the default application (*) already", Messages(FailsApplications(Applications(), Add("Site B", "*"))));
        var again = FailsApplications(Applications(), Add("Site A", "*"));
        Assert.Equal((AgentErrorCodes.Conflict, "Site A is the default application (*) already."), (again.Code, again.Message));
        var noDefault = Applications().Select(s => s with { Hosts = s.Hosts.Where(h => h.Name != "*").ToList() }).ToList();
        Assert.Contains("on CMS 13, * stands for the default application", Messages(FailsApplications(noDefault, Add("Site B", "*", HostTypes.Primary))));
        Assert.Contains("on CMS 13, * stands for the default application", Messages(FailsApplications(noDefault, Add("Site B", "*", language: "nb"))));
        Assert.Contains("on CMS 13, * stands for the default application", Messages(FailsApplications(noDefault, Add("Site B", "*", https: HostHttps.True))));
        var last = FailsApplications([new("siteC", "Site C", "https://site-c.example/", [Host("site-c.example", HostTypes.Primary, https: true), Host("*")])], Remove("Site C", "site-c.example"));
        Assert.Equal(AgentErrorCodes.Refused, last.Code);
        // Without a host for every language it is still allowed (CMS 13 links with any of the application's hosts).
        var c = new SiteState("siteC", "Site C", "https://site-c.example/", [Host("site-c.example", HostTypes.Primary, "nb", true)]);
        Assert.True(PlanApplications([c], Add("Site C", "*")).Sites.Single().Changed);
    }
}
