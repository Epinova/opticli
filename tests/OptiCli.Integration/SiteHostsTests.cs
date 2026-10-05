using OptiCli.Core.Cms;
using OptiCli.Core.Configuration;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Refs;
using OptiCli.Core.Sites;
using OptiCli.Core.Urls;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// <c>sites primary</c> and <c>sites host</c> on the edge-case site's two hosts sites, through the running site. Every test
/// puts the hosts and SiteUrl back as they were and checks that it did.
/// </summary>
public sealed class SiteHostsTests
{
    private const string HostA = "localhost:5871";
    private const string HostB = "localhost:5872";

    [SiteFact]
    public async Task Primary_on_two_sites_points_them_at_localhost_and_a_second_run_changes_nothing()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await HostsSitesAsync(site, cancellationToken) is not { } original)
        {
            return;
        }
        var (a, b, _) = original;
        var changes = new[] { Primary(a, HostA), Primary(b, $"https://{HostB}/") };
        try
        {
            var first = await RunAsync(site, original, changes, dryRun: false, cancellationToken);

            Assert.True(first.Saved);
            Assert.Equal([SiteHostStatus.Changed, SiteHostStatus.Changed], first.Sites.Select(s => s.Status));
            var viewA = first.Sites[0];
            Assert.Equal((a.Id, $"https://{HostA}/"), (viewA.Id, viewA.Url));
            Assert.Equal(new HostInfo(HostA, HostType.Primary, null, null), viewA.Hosts[^1]);
            Assert.Equal(HostType.Undefined, viewA.Hosts.Single(h => h.Name == "edit.hosts-a.localhost").Type);
            Assert.Contains("hosts-a.localhost: primary → undefined", viewA.Changes);
            Assert.Equal(new HostInfo(HostB, HostType.Primary, null, true), first.Sites[1].Hosts[^1]);
            Assert.Contains(first.Warnings, w => w.StartsWith($"{a.Name}'s sv URLs still use sv.hosts-a.example", StringComparison.Ordinal));
            Assert.Contains(first.Warnings, w => w.Contains("restart the site", StringComparison.Ordinal));

            // The database has the new hosts, so URLs on them resolve: `opticli resolve https://localhost:<port>/`.
            var model = await CmsModel.LoadAsync(site.Session.Db, cancellationToken);
            var locator = new ContentLocator(site.Session.Db, model);
            Assert.Equal(SiteMap.StartPageId(a), (await locator.LocateAsync(ContentRefParser.Parse($"https://{HostA}/"), null, cancellationToken)).Id);
            Assert.Equal(SiteMap.StartPageId(b), (await locator.LocateAsync(ContentRefParser.Parse($"https://{HostB}/"), null, cancellationToken)).Id);
            // The production names stay, and resolve as before.
            Assert.Equal(SiteMap.StartPageId(a), (await locator.LocateAsync(ContentRefParser.Parse("https://hosts-a.localhost/"), null, cancellationToken)).Id);

            var second = await RunAsync(site, original, changes, dryRun: false, cancellationToken);
            Assert.False(second.Saved);
            Assert.All(second.Sites, s => Assert.Equal((SiteHostStatus.Unchanged, 0), (s.Status, s.Changes.Count)));
            Assert.DoesNotContain(second.Warnings, w => w.Contains("restart the site", StringComparison.Ordinal));
        }
        finally
        {
            await RestoreAsync(site, original, cancellationToken);
        }
    }

    [SiteFact]
    public async Task A_dry_run_shows_the_end_state_and_saves_nothing()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await HostsSitesAsync(site, cancellationToken) is not { } original)
        {
            return;
        }
        try
        {
            var dry = await RunAsync(site, original, [Primary(original.A, HostA), Primary(original.A, "localhost:5874", "sv")], dryRun: true, cancellationToken);

            var view = Assert.Single(dry.Sites);
            Assert.False(dry.Saved);
            Assert.Equal((SiteHostStatus.Changed, true), (view.Status, view.DryRun));
            Assert.Equal(new HostInfo("localhost:5874", HostType.Primary, "sv", null), view.Hosts[^1]);
            Assert.Empty(dry.Warnings);
            await AssertUnchangedAsync(site, original, cancellationToken);
        }
        finally
        {
            await RestoreAsync(site, original, cancellationToken);
        }
    }

    [SiteFact]
    public async Task A_host_another_site_has_fails_validation_and_nothing_is_saved()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await HostsSitesAsync(site, cancellationToken) is not { } original)
        {
            return;
        }
        var (a, b, _) = original;
        try
        {
            var twice = await Assert.ThrowsAsync<ContentValidationException>(() => RunAsync(site, original, [Primary(a, HostA), Primary(b, HostA)], dryRun: false, cancellationToken));
            var taken = await Assert.ThrowsAsync<ContentValidationException>(() => RunAsync(site, original, [Primary(b, "hosts-a.localhost")], dryRun: false, cancellationToken));

            Assert.Contains($"{b.Name}={HostA}: {HostA} belongs to {a.Name}", twice.Message);
            Assert.Equal(SiteHostsRequestHint, twice.Hint);
            Assert.Contains("belongs to", taken.Message);
            await AssertUnchangedAsync(site, original, cancellationToken);
        }
        finally
        {
            await RestoreAsync(site, original, cancellationToken);
        }
    }

    [SiteFact]
    public async Task Host_add_and_remove_change_one_host()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await HostsSitesAsync(site, cancellationToken) is not { } original)
        {
            return;
        }
        var (a, b, _) = original;
        try
        {
            var added = await RunAsync(site, original, [Add(a, "localhost:5873")], dryRun: false, cancellationToken);
            Assert.Equal([$"added localhost:5873 (undefined)"], Assert.Single(added.Sites).Changes);
            Assert.Contains((await SiteAsync(site, a, cancellationToken)).Hosts, h => h.Name == "localhost:5873");

            await Assert.ThrowsAsync<ConflictException>(() => RunAsync(site, original, [Add(a, "localhost:5873")], dryRun: false, cancellationToken));

            var removed = await RunAsync(site, original, [Remove(a, "localhost:5873")], dryRun: false, cancellationToken);
            Assert.Equal([$"removed localhost:5873 (undefined)"], Assert.Single(removed.Sites).Changes);
            await AssertUnchangedAsync(site, original, cancellationToken);

            await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(site, original, [Remove(a, "localhost:5873")], dryRun: false, cancellationToken));
            await Assert.ThrowsAsync<RefusedException>(() => RunAsync(site, original, [Remove(b, "hosts-b.localhost")], dryRun: false, cancellationToken));
            var siteUrl = await Assert.ThrowsAsync<ContentValidationException>(() => RunAsync(site, original, [Remove(a, "hosts-a.localhost")], dryRun: false, cancellationToken));
            Assert.Contains("SiteUrl", siteUrl.Message);
            await AssertUnchangedAsync(site, original, cancellationToken);
        }
        finally
        {
            await RestoreAsync(site, original, cancellationToken);
        }
    }

    [SiteFact]
    public async Task A_default_port_is_stored_as_the_cms_stores_it_so_a_rerun_changes_nothing()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await HostsSitesAsync(site, cancellationToken) is not { } original)
        {
            return;
        }
        try
        {
            var first = await RunAsync(site, original, [Primary(original.B, "localhost:443")], dryRun: false, cancellationToken);
            var b = await SiteAsync(site, original.B, cancellationToken);

            Assert.Equal("https://localhost/", b.Url);
            Assert.Equal(first.Sites[0].Hosts, b.Hosts);
            Assert.Equal(new HostInfo("localhost", HostType.Primary, null, true), b.Hosts[^1]);
            var second = await RunAsync(site, original, [Primary(original.B, "localhost:443")], dryRun: false, cancellationToken);
            Assert.Equal(SiteHostStatus.Unchanged, second.Sites[0].Status);
            Assert.False(second.Saved);

            // With localhost (written localhost:80) on A, B can't have localhost:443, which is the same host: the batch
            // fails before A is saved.
            await RestoreAsync(site, original, cancellationToken);
            await RunAsync(site, original, [Add(original.A, "localhost:80")], dryRun: false, cancellationToken);
            Assert.Contains(new HostInfo("localhost", HostType.Undefined, null, false), (await SiteAsync(site, original.A, cancellationToken)).Hosts);
            var conflict = await Assert.ThrowsAsync<ContentValidationException>(() => RunAsync(site, original, [Primary(original.A, HostA), Primary(original.B, "localhost:443")], dryRun: false, cancellationToken));
            Assert.Contains("localhost belongs to", conflict.Message);
            Assert.Equal(original.A.Url, (await SiteAsync(site, original.A, cancellationToken)).Url);
        }
        finally
        {
            await RestoreAsync(site, original, cancellationToken);
        }
    }

    [SiteFact]
    public async Task A_port_that_isnt_the_schemes_default_is_kept_end_to_end_and_a_host_is_removed_by_the_name_it_has()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await HostsSitesAsync(site, cancellationToken) is not { } original)
        {
            return;
        }
        var languages = site.Session.Model.Languages.Where(l => l.Enabled && !l.IsInvariant).Select(l => l.Code).ToList();
        var directory = Path.Combine(Path.GetTempPath(), "opticli-it", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(directory, "config.json");
        try
        {
            // sites primary "Edge hosts B=http://localhost:443" --save
            var pair = PrimaryPairs.Parse($"{original.B.Name}=http://localhost:443", original.All, languages);
            var first = await RunAsync(site, original, [ToChange(pair)], dryRun: false, cancellationToken);
            var b = await SiteAsync(site, original.B, cancellationToken);
            Assert.Equal("http://localhost:443/", b.Url);
            Assert.Equal(new HostInfo("localhost:443", HostType.Primary, null, false), b.Hosts[^1]);
            Assert.Equal(first.Sites[0].Hosts, b.Hosts);
            UserConfig.SavePrimaryHosts(config, site.ProjectDirectory, [original.B.Name], languages, [KeyValuePair.Create(pair.Key, pair.Saved)]);

            // The same host written without its scheme: it keeps its http, so nothing changes (no second host localhost).
            var unschemed = PrimaryPairs.Parse($"{original.B.Name}=localhost:443", original.All, languages);
            var again = await RunAsync(site, original, [ToChange(unschemed)], dryRun: false, cancellationToken);
            Assert.Equal((SiteHostStatus.Unchanged, false), (again.Sites[0].Status, again.Saved));
            Assert.Equal(b.Hosts, (await SiteAsync(site, original.B, cancellationToken)).Hosts);
            Assert.Equal(SiteHostStatus.Unchanged, (await RunAsync(site, original, [ToChange(unschemed)], dryRun: false, cancellationToken)).Sites[0].Status);

            // --from-config: the same, so unchanged.
            var mapping = UserConfig.ForProject(config, site.ProjectDirectory)!.PrimaryHosts!;
            var fromConfig = PrimaryMapping.Pairs(mapping, await SiteReader.ListAsync(site.Session.Db, cancellationToken), languages, null, false, false, []);
            Assert.Equal(SiteHostStatus.Unchanged, (await RunAsync(site, original, fromConfig.Select(ToChange).ToList(), dryRun: false, cancellationToken)).Sites[0].Status);

            // A host stored with a port opticli would leave out is removed by that name.
            await RestoreAsync(site, original, cancellationToken);
            await RunAsync(site, original, [Add(original.A, "hosts-a.localhost:443", new HostInfo("x", HostType.Undefined, null, false))], dryRun: false, cancellationToken);
            Assert.Contains((await SiteAsync(site, original.A, cancellationToken)).Hosts, h => h.Name == "hosts-a.localhost:443");
            var removed = await RunAsync(site, original, [Remove(original.A, "hosts-a.localhost:443")], dryRun: false, cancellationToken);
            Assert.Equal(["removed hosts-a.localhost:443 (undefined, http)"], removed.Sites[0].Changes);
        }
        finally
        {
            await RestoreAsync(site, original, cancellationToken);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [SiteFact]
    public async Task One_pair_replaces_the_only_primary_host_of_a_site_whose_hosts_are_all_for_one_language()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await HostsSitesAsync(site, cancellationToken) is not { } original)
        {
            return;
        }
        var c = original.C;
        var languages = site.Session.Model.Languages.Where(l => l.Enabled && !l.IsInvariant).Select(l => l.Code).ToList();
        var directory = Path.Combine(Path.GetTempPath(), "opticli-it", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(directory, "config.json");
        try
        {
            // sites primary "Edge hosts C=localhost:5873" --save
            var pair = PrimaryPairs.Parse($"{c.Name}=localhost:5873", original.All, languages);
            var first = await RunAsync(site, original, [ToChange(pair)], dryRun: false, cancellationToken);
            Assert.Equal(SiteHostStatus.Changed, first.Sites[0].Status);
            Assert.DoesNotContain(first.Warnings, w => w.Contains("URLs still use", StringComparison.Ordinal));
            var now = await SiteAsync(site, c, cancellationToken);
            Assert.Equal("https://localhost:5873/", now.Url);
            Assert.Equal(new HostInfo("localhost:5873", HostType.Primary, "en", null), now.Hosts[^1]);
            Assert.Equal(new HostInfo("hosts-c.localhost", HostType.Undefined, "en", true), now.Hosts[0]);
            var model = await CmsModel.LoadAsync(site.Session.Db, cancellationToken);
            Assert.Equal(SiteMap.StartPageId(c), (await new ContentLocator(site.Session.Db, model).LocateAsync(ContentRefParser.Parse("https://localhost:5873/"), null, cancellationToken)).Id);

            Assert.Equal(SiteHostStatus.Unchanged, (await RunAsync(site, original, [ToChange(pair)], dryRun: false, cancellationToken)).Sites[0].Status);

            // doctor and --from-config read the entry the same way: it matches, and applying it changes nothing.
            UserConfig.SavePrimaryHosts(config, site.ProjectDirectory, [c.Name], languages, [KeyValuePair.Create(pair.Key, pair.Saved)]);
            var mapping = UserConfig.ForProject(config, site.ProjectDirectory)!.PrimaryHosts!;
            var sites = await SiteReader.ListAsync(site.Session.Db, cancellationToken);
            Assert.Equal(PrimaryMapping.Matches, PrimaryMapping.Compare(mapping, sites, languages).Single().Status);
            var fromConfig = PrimaryMapping.Pairs(mapping, sites, languages, null, false, false, []);
            Assert.Equal(SiteHostStatus.Unchanged, (await RunAsync(site, original, fromConfig.Select(ToChange).ToList(), dryRun: false, cancellationToken)).Sites[0].Status);

            // With a pair for another language in the same batch, in either order: the same plan, and a rerun changes nothing.
            await RestoreAsync(site, original, cancellationToken);
            var sv = PrimaryPairs.Parse($"{c.Name}@sv=localhost:5881", original.All, languages);
            var other = PrimaryPairs.Parse($"{c.Name}=localhost:5882", original.All, languages);
            var svFirst = await RunAsync(site, original, [ToChange(sv), ToChange(other)], dryRun: true, cancellationToken);
            var svLast = await RunAsync(site, original, [ToChange(other), ToChange(sv)], dryRun: false, cancellationToken);
            // The same hosts and settings; only the order the new ones are added in follows the pairs.
            Assert.Equal(svFirst.Sites[0].Hosts.OrderBy(h => h.Name), svLast.Sites[0].Hosts.OrderBy(h => h.Name));
            Assert.Equal(svFirst.Sites[0].Url, svLast.Sites[0].Url);
            Assert.Equal(new HostInfo("localhost:5882", HostType.Primary, "en", null), svLast.Sites[0].Hosts.Single(h => h.Name == "localhost:5882"));
            Assert.Equal(SiteHostStatus.Unchanged, (await RunAsync(site, original, [ToChange(sv), ToChange(other)], dryRun: true, cancellationToken)).Sites[0].Status);
            UserConfig.SavePrimaryHosts(config, site.ProjectDirectory, [c.Name], languages, [KeyValuePair.Create(other.Key, other.Saved), KeyValuePair.Create(sv.Key, sv.Saved)]);
            mapping = UserConfig.ForProject(config, site.ProjectDirectory)!.PrimaryHosts!;
            sites = await SiteReader.ListAsync(site.Session.Db, cancellationToken);
            Assert.All(PrimaryMapping.Compare(mapping, sites, languages), e => Assert.Equal(PrimaryMapping.Matches, e.Status));
            fromConfig = PrimaryMapping.Pairs(mapping, sites, languages, null, false, false, []);
            Assert.Equal(SiteHostStatus.Unchanged, (await RunAsync(site, original, fromConfig.Select(ToChange).ToList(), dryRun: true, cancellationToken)).Sites[0].Status);
            await RestoreAsync(site, original, cancellationToken);

            // With the pair for its language too, it is refused: the first already covers English.
            var both = await Assert.ThrowsAsync<ContentValidationException>(() =>
                RunAsync(site, original, [Primary(c, "localhost:5874"), Primary(c, "localhost:5874", "en")], dryRun: true, cancellationToken));
            Assert.Contains("not both", both.Hint);
        }
        finally
        {
            await RestoreAsync(site, original, cancellationToken);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [SiteFact]
    public async Task A_saved_mapping_is_applied_again_from_the_config()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await HostsSitesAsync(site, cancellationToken) is not { } original)
        {
            return;
        }
        var (a, b, _) = original;
        var directory = Path.Combine(Path.GetTempPath(), "opticli-it", Guid.NewGuid().ToString("N"));
        var config = Path.Combine(directory, "config.json");
        var languages = site.Session.Model.Languages.Where(l => l.Enabled && !l.IsInvariant).Select(l => l.Code).ToList();
        try
        {
            // sites primary "A=localhost:5871" "A@sv=https://localhost:5874/" "B=localhost:5872" --save
            var pairs = new[] { $"{a.Name}={HostA}", $"{a.Name}@sv=https://localhost:5874/", $"{b.Name}={HostB}" }
                .Select(p => PrimaryPairs.Parse(p, [a, b], languages)).ToList();
            await RunAsync(site, original, pairs.Select(ToChange).ToList(), dryRun: false, cancellationToken);
            UserConfig.SavePrimaryHosts(config, site.ProjectDirectory, [a.Name, b.Name], languages, pairs.Select(p => KeyValuePair.Create(p.Key, p.Saved)).ToList());
            var mapping = UserConfig.ForProject(config, site.ProjectDirectory)!.PrimaryHosts!;
            Assert.Equal("https://localhost:5874", mapping[$"{a.Name}@sv"].Host);
            Assert.All(PrimaryMapping.Compare(mapping, await SiteReader.ListAsync(site.Session.Db, cancellationToken), languages), e => Assert.Equal(PrimaryMapping.Matches, e.Status));

            // A database restore later: the sites are back as they were, and doctor says so.
            await RestoreAsync(site, original, cancellationToken);
            var restored = PrimaryMapping.Compare(mapping, await SiteReader.ListAsync(site.Session.Db, cancellationToken), languages);
            Assert.All(restored, e => Assert.Equal(PrimaryMapping.Differs, e.Status));
            Assert.Equal(3, PrimaryMapping.Warnings(restored).Count(w => w.EndsWith($"Run `{PrimaryMapping.FromConfigCommand}`.", StringComparison.Ordinal)));

            // sites primary --from-config
            var fromConfig = PrimaryMapping.Pairs(mapping, await SiteReader.ListAsync(site.Session.Db, cancellationToken), languages, null, keepEdit: false, keepSiteUrl: false, []);
            var applied = await RunAsync(site, original, fromConfig.Select(ToChange).ToList(), dryRun: false, cancellationToken);
            Assert.All(applied.Sites, s => Assert.Equal(SiteHostStatus.Changed, s.Status));
            Assert.Equal(new HostInfo("localhost:5874", HostType.Primary, "sv", true), applied.Sites.Single(s => s.Guid == a.Guid).Hosts.Single(h => h.Language == "sv" && h.Type == HostType.Primary));
            Assert.All(PrimaryMapping.Compare(mapping, await SiteReader.ListAsync(site.Session.Db, cancellationToken), languages), e => Assert.Equal(PrimaryMapping.Matches, e.Status));
        }
        finally
        {
            await RestoreAsync(site, original, cancellationToken);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>What <c>SiteHostPlanner.ValidationHint</c> says, which the CLI passes on for these errors.</summary>
    private const string SiteHostsRequestHint = "Fix the listed changes and retry; nothing was saved. `opticli sites` lists every site's hosts.";

    private sealed record HostsSites(SiteInfo A, SiteInfo B, SiteInfo C)
    {
        public IReadOnlyList<SiteInfo> All => [A, B, C];
    }

    /// <returns>Null on a site without them (not the edge-case site, or set up before they were added: run setup.sh again).</returns>
    private static async Task<HostsSites?> HostsSitesAsync(SiteUnderTest site, CancellationToken cancellationToken)
    {
        var sites = await SiteReader.ListAsync(site.Session.Db, cancellationToken);
        return sites.FirstOrDefault(s => s.Name == EdgeFixture.HostsSiteA) is { } a
            && sites.FirstOrDefault(s => s.Name == EdgeFixture.HostsSiteB) is { } b
            && sites.FirstOrDefault(s => s.Name == EdgeFixture.HostsSiteC) is { } c
                ? new HostsSites(a, b, c)
                : null;
    }

    private static async Task<SiteInfo> SiteAsync(SiteUnderTest site, SiteInfo which, CancellationToken cancellationToken) =>
        (await SiteReader.ListAsync(site.Session.Db, cancellationToken)).Single(s => s.Guid == which.Guid);

    private static Task<SiteHostsOutcome> RunAsync(SiteUnderTest site, HostsSites sites, IReadOnlyList<SiteHostChange> changes, bool dryRun, CancellationToken cancellationToken) =>
        SiteHostsRunner.RunAsync(site.Agent, sites.All, changes, dryRun, sharedDatabase: false, cancellationToken);

    private static SiteHostChange Primary(SiteInfo site, string host, string? language = null, string? https = null, bool keepEdit = false) => new()
    {
        Site = site.Guid.ToString("D"),
        Host = host,
        Action = SiteHostActions.Primary,
        Language = language,
        Https = https,
        KeepEdit = keepEdit,
    };

    private static SiteHostChange Add(SiteInfo site, string host, HostInfo? like = null) => new()
    {
        Site = site.Guid.ToString("D"),
        Host = host,
        Action = SiteHostActions.Add,
        Type = like is null ? null : HostTypes.FromValue((int)like.Type),
        Language = like?.Language,
        Https = like is null ? null : HostHttps.Format(like.Https),
    };

    private static SiteHostChange Remove(SiteInfo site, string host) => new() { Site = site.Guid.ToString("D"), Host = host, Action = SiteHostActions.Remove };

    /// <summary>As <c>sites primary</c> sends a pair: the host as typed, with the https setting.</summary>
    private static SiteHostChange ToChange(PrimaryPair pair) =>
        Primary(pair.Site, pair.Typed, pair.Language, pair.Https, pair.KeepEdit) with { KeepSiteUrl = pair.KeepSiteUrl };

    /// <summary>
    /// Puts both sites back with the commands themselves: <c>primary</c> on each original primary host (which also sets
    /// SiteUrl back: the fixture's is https on the primary host), then removes the hosts that were added and removes and
    /// re-adds the ones whose type changed (the fixture keeps those last, so the order comes back too).
    /// </summary>
    private static async Task RestoreAsync(SiteUnderTest site, HostsSites original, CancellationToken cancellationToken)
    {
        var current = await SiteReader.ListAsync(site.Session.Db, cancellationToken);
        var changes = new List<SiteHostChange>();
        foreach (var before in original.All)
        {
            var now = current.Single(s => s.Guid == before.Guid);
            if (now.Url == before.Url && now.Hosts.SequenceEqual(before.Hosts))
            {
                continue;
            }
            changes.AddRange(before.Hosts.Where(h => h.Type == HostType.Primary).Select(h => Primary(before, h.Name, h.Language, HostHttps.Format(h.Https), keepEdit: true)));
            foreach (var host in now.Hosts.Where(h => before.Hosts.All(o => o.Name != h.Name)))
            {
                changes.Add(Remove(before, host.Name));
            }
            foreach (var host in before.Hosts.Where(h => h.Type != HostType.Primary && !now.Hosts.Contains(h)))
            {
                changes.Add(Remove(before, host.Name));
                changes.Add(Add(before, host.Name, host));
            }
        }
        if (changes.Count > 0)
        {
            await RunAsync(site, original, changes, dryRun: false, cancellationToken);
        }
        await AssertUnchangedAsync(site, original, cancellationToken);
    }

    private static async Task AssertUnchangedAsync(SiteUnderTest site, HostsSites original, CancellationToken cancellationToken)
    {
        var current = await SiteReader.ListAsync(site.Session.Db, cancellationToken);
        foreach (var before in original.All)
        {
            var now = current.Single(s => s.Guid == before.Guid);
            Assert.Equal(before.Url, now.Url);
            Assert.Equal(before.Hosts, now.Hosts);
        }
    }
}
