using System.Net;
using System.Text;
using System.Text.Json;
using OptiCli.Core.Drift;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Drift;

public class BuildScannerTests
{
    private static string Output => Path.GetDirectoryName(typeof(BuildScannerTests).Assembly.Location)!;

    [Fact]
    public void Migrations_are_read_from_the_build_output_with_their_context()
    {
        var migrations = BuildScanner.Migrations(Output);

        Assert.Equal(
            [
                new EfMigration("20260101000000_AddEvents", "OptiCli.Core.Tests.Drift.Fixtures.EventsContext", "OptiCli.Core.Tests.dll"),
                new EfMigration("20260201000000_AddVenues", "OptiCli.Core.Tests.Drift.Fixtures.EventsContext", "OptiCli.Core.Tests.dll"),
            ],
            migrations.OrderBy(m => m.Id, StringComparer.Ordinal));
    }

    [Fact]
    public void A_folder_without_assemblies_has_none()
    {
        using var empty = new TempDirectory();
        File.WriteAllText(Path.Combine(empty.Path, "native.dll"), "not a .NET assembly");

        Assert.Empty(BuildScanner.Migrations(empty.Path));
        Assert.Empty(BuildScanner.Migrations(Path.Combine(empty.Path, "missing")));
        Assert.Null(BuildScanner.RequiredSchemaVersion(Path.Combine(empty.Path, "native.dll")));
    }

    [Fact]
    public void The_schema_version_the_packages_need_is_read_from_the_cms_constant()
    {
        Assert.Equal(8023, BuildScanner.RequiredSchemaVersion(typeof(BuildScannerTests).Assembly.Location));
        Assert.Null(BuildScanner.RequiredSchemaVersion(typeof(StartupDriftCheck).Assembly.Location));
    }

    [Fact]
    public void The_package_version_is_read_from_the_assembly()
    {
        Assert.Equal(typeof(StartupDriftCheck).Assembly.GetName().Version, BuildScanner.AssemblyVersion(typeof(StartupDriftCheck).Assembly.Location));
    }
}

public class StartupDriftCheckTests
{
    private static readonly EfMigration AddEvents = new("20260101000000_AddEvents", "Site.Data.EventsContext", "Site.dll");
    private static readonly EfMigration AddVenues = new("20260201000000_AddVenues", "Site.Data.EventsContext", "Site.dll");

    [Fact]
    public void A_migration_in_the_build_only_is_ahead_locally_and_one_in_the_history_only_in_the_database()
    {
        var notes = new List<string>();

        var items = StartupDriftCheck.Migrations([AddEvents, AddVenues], ["20260101000000_AddEvents", "20250101000000_Init"], notes);

        Assert.Equal(
            [
                new DriftItem("20260201000000_AddVenues", DriftAhead.Local, "in this build (Site.Data.EventsContext), not applied to the database"),
                new DriftItem("20250101000000_Init", DriftAhead.Database, "applied to the database, not in this build"),
            ],
            items);
        Assert.Empty(notes);
    }

    [Fact]
    public void Without_a_history_table_migrations_are_not_compared_but_noted()
    {
        var notes = new List<string>();

        Assert.Empty(StartupDriftCheck.Migrations([AddEvents], null, notes));
        Assert.Contains("no __EFMigrationsHistory table", Assert.Single(notes), StringComparison.Ordinal);

        notes.Clear();
        Assert.Empty(StartupDriftCheck.Migrations([], null, notes));
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData(8023, 8023, "12.21.2.0", null, false)]
    [InlineData(8024, 8023, "12.21.2.0", DriftAhead.Database, false)]
    [InlineData(8024, 8023, "12.17.0.0", DriftAhead.Database, false)]
    [InlineData(8022, 8023, "12.21.2.0", DriftAhead.Local, true)]
    [InlineData(8030, 8023, "12.21.2.0", DriftAhead.Database, true)]
    // Before 12.17 the CMS doesn't accept a newer schema at all; an unknown version is taken to be such a one.
    [InlineData(8001, 8000, "12.0.3.0", DriftAhead.Database, true)]
    [InlineData(8010, 8009, "12.15.0.0", DriftAhead.Database, true)]
    [InlineData(8010, 8009, "12.16.0.0", DriftAhead.Database, true)]
    [InlineData(8024, 8023, null, DriftAhead.Database, true)]
    public void The_cms_schema_starts_when_equal_or_one_newer_on_packages_that_accept_it(int database, int required, string? framework, string? ahead, bool refused)
    {
        var (items, refusal, hint) = StartupDriftCheck.Schema(database, required, framework is null ? null : Version.Parse(framework), []);

        Assert.Equal(ahead, items.SingleOrDefault()?.Ahead);
        Assert.Equal(refused, refusal is not null);
        Assert.Equal(refused, hint is not null);
    }

    [Fact]
    public void A_schema_version_that_cant_be_read_is_noted()
    {
        var notes = new List<string>();

        var (items, refusal, _) = StartupDriftCheck.Schema(8023, null, new Version(12, 21, 2, 0), notes);

        Assert.Empty(items);
        Assert.Null(refusal);
        Assert.Contains("EPiServer.Data.dll", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_migrations_refuse_the_start_unless_allowed()
    {
        var pending = new DriftItem(AddVenues.Id, DriftAhead.Local, "in this build");
        var check = new StartupCheck(new StartupDrift { Migrations = [pending] }, [pending], null, null);

        var refused = Assert.Throws<RefusedException>(() => check.ThrowIfBlocked(allowPendingMigrations: false));
        Assert.Contains("20260201000000_AddVenues", refused.Message, StringComparison.Ordinal);
        Assert.Contains("--allow-pending-migrations", refused.Hint, StringComparison.Ordinal);

        check.ThrowIfBlocked(allowPendingMigrations: true);
    }

    [Fact]
    public void A_schema_the_cms_wont_start_with_refuses_even_with_migrations_allowed()
    {
        var check = new StartupCheck(new StartupDrift(), [], "The shared database's CMS schema is version 8022.", "Check out what is deployed.");

        Assert.Throws<RefusedException>(() => check.ThrowIfBlocked(allowPendingMigrations: true));
    }
}

public class DriftReportTests
{
    private static readonly DriftItem NewsPage = new("NewsPage", DriftAhead.Local, "only in the code");
    private static readonly DriftItem Teaser = new("ArticlePage.Teaser", DriftAhead.Database, "only in the database");

    private static DriftReport Report(DriftItem[] types, DriftItem[] properties) => DriftReport.Create(types, properties, [], [], [], ["a note"]);

    [Fact]
    public void The_fingerprint_is_stable_whatever_the_order()
    {
        var one = DriftReport.Create([NewsPage, new DriftItem("EventPage", DriftAhead.Local, "only in the code")], [Teaser], [], [], [], []);
        var other = DriftReport.Create([new DriftItem("EventPage", DriftAhead.Local, "only in the code"), NewsPage], [Teaser], [], [], [], ["notes don't count"]);

        Assert.Equal(one.Fingerprint, other.Fingerprint);
        Assert.Matches("^[0-9a-f]{12}$", one.Fingerprint);
    }

    [Fact]
    public void The_fingerprint_changes_with_the_differences()
    {
        var before = Report([NewsPage], [Teaser]);

        Assert.NotEqual(before.Fingerprint, Report([NewsPage], []).Fingerprint);
        Assert.NotEqual(before.Fingerprint, Report([NewsPage with { Difference = "only in the database", Ahead = DriftAhead.Database }], [Teaser]).Fingerprint);
        // The same item in another list is another difference.
        Assert.NotEqual(Report([NewsPage], []).Fingerprint, Report([], [NewsPage]).Fingerprint);
    }

    [Fact]
    public void Nothing_differing_has_no_fingerprint_and_no_direction()
    {
        var report = Report([], []);

        Assert.True(report.Checked);
        Assert.Null(report.Fingerprint);
        Assert.Null(report.Ahead);
        Assert.Equal(0, report.Differences);
    }

    [Fact]
    public void The_direction_is_the_one_every_item_that_knows_agrees_on()
    {
        var changed = new DriftItem("ArticlePage.Heading", DriftAhead.Unknown, "required in the code, not in the database");

        Assert.Equal(DriftAhead.Local, Report([NewsPage], [changed]).Ahead);
        Assert.Equal(DriftAhead.Database, Report([], [Teaser]).Ahead);
        Assert.Equal(DriftAhead.Both, Report([NewsPage], [Teaser]).Ahead);
        Assert.Equal(DriftAhead.Unknown, Report([], [changed]).Ahead);
    }

    [Fact]
    public void Describe_lists_the_first_items_and_counts_the_rest()
    {
        var types = Enumerable.Range(1, 7).Select(i => new DriftItem($"Page{i}", DriftAhead.Local, "only in the code")).ToArray();

        var text = Report(types, []).Describe(max: 2);

        Assert.StartsWith("7 differences (local is ahead", text, StringComparison.Ordinal);
        Assert.Contains("content type Page1 (only in the code); content type Page2 (only in the code); and 5 more", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_survives_the_agents_json()
    {
        var report = Report([NewsPage], [Teaser]);

        var read = JsonSerializer.Deserialize<DriftReport>(JsonSerializer.Serialize(report, AgentJson.Options), AgentJson.Options)!;

        Assert.Equal(report.Fingerprint, read.Fingerprint);
        Assert.Equal(report.ContentTypes, read.ContentTypes);
        Assert.Equal(2, read.Differences);
        Assert.Contains("\"differences\":2", JsonSerializer.Serialize(report, AgentJson.Options), StringComparison.Ordinal);
    }
}

public class DriftConfirmationTests
{
    private static readonly DriftReport Drifted = DriftReport.Create([new DriftItem("NewsPage", DriftAhead.Local, "only in the code")], [], [], [], [], []);

    [Fact]
    public void A_drift_error_from_the_agent_becomes_exit_5_with_the_report_in_details()
    {
        var envelope = JsonSerializer.Serialize(new AgentResponse<object>(false, null, new AgentMeta("agent", "0.6.0", AgentProtocol.Version),
            new AgentError(AgentErrorCodes.Drift, "The site's code and the shared database differ.") { Drift = Drifted }), AgentJson.Options);

        var ex = Assert.Throws<DriftException>(() => AgentClient.Parse<WriteResult>(envelope, 409));

        Assert.Equal(ExitCodes.Conflict, ExitCodes.For(ex.Code));
        Assert.Equal("drift", ExitCodes.Name(ex.Code));
        Assert.Equal(Drifted.Fingerprint, Assert.IsType<DriftReport>(ex.Details).Fingerprint);
        Assert.Equal(AgentErrors.DriftHint, ex.Hint);
    }

    [Fact]
    public async Task Without_a_confirmation_a_write_stops_before_anything_is_sent()
    {
        var agent = new FakeAgent(Drifted);
        var executor = Executor(agent);

        var ex = await Assert.ThrowsAsync<DriftException>(() => executor.RequireDriftAcceptedAsync(CancellationToken.None));

        Assert.Contains("NewsPage", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Drifted.Fingerprint, ((DriftReport)ex.Details!).Fingerprint);
        Assert.Equal(["GET v1/drift"], agent.Requests.Select(r => r.Route));
    }

    [Fact]
    public async Task The_fingerprint_confirms_it_and_goes_with_every_later_request()
    {
        var agent = new FakeAgent(Drifted);
        var executor = Executor(agent, accept: Drifted.Fingerprint);

        await executor.RequireDriftAcceptedAsync(CancellationToken.None);
        await executor.RequireDriftAcceptedAsync(CancellationToken.None);
        await agent.Client.SendAsync<WriteResult>(HttpMethod.Post, AgentRoutes.Publish("123"), new PublishRequest(), CancellationToken.None);

        // Asked once per run.
        Assert.Single(agent.Requests, r => r.Route == "GET v1/drift");
        Assert.Equal(Drifted.Fingerprint, agent.Requests.Last().AcceptedDrift);
    }

    [Fact]
    public async Task A_fingerprint_that_no_longer_matches_says_so()
    {
        var executor = Executor(new FakeAgent(Drifted), accept: "0123456789ab");

        var ex = await Assert.ThrowsAsync<DriftException>(() => executor.RequireDriftAcceptedAsync(CancellationToken.None));

        Assert.Contains("--accept-drift 0123456789ab no longer matches", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_a_terminal_the_answer_decides()
    {
        DriftReport? asked = null;
        var yes = Executor(new FakeAgent(Drifted), confirm: r => { asked = r; return true; });
        await yes.RequireDriftAcceptedAsync(CancellationToken.None);
        Assert.Equal(Drifted.Fingerprint, asked?.Fingerprint);

        var no = Executor(new FakeAgent(Drifted), confirm: _ => false);
        var ex = await Assert.ThrowsAsync<DriftException>(() => no.RequireDriftAcceptedAsync(CancellationToken.None));
        Assert.StartsWith("Not written, as answered", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_differences_nothing_is_asked()
    {
        var agent = new FakeAgent(new DriftReport { Checked = false });

        await Executor(agent, confirm: _ => throw new InvalidOperationException("asked")).RequireDriftAcceptedAsync(CancellationToken.None);

        Assert.Null(agent.Requests.Single().AcceptedDrift);
    }

    [Fact]
    public async Task Against_a_local_database_the_agent_is_not_asked()
    {
        var agent = new FakeAgent(Drifted);

        await Executor(agent, shared: false).RequireDriftAcceptedAsync(CancellationToken.None);

        Assert.Empty(agent.Requests);
    }

    [Fact]
    public async Task An_agent_too_old_to_compare_refuses_shared_writes()
    {
        var agent = new FakeAgent(Drifted) { WithoutDriftRoute = true };

        var ex = await Assert.ThrowsAsync<RefusedException>(() => Executor(agent).RequireDriftAcceptedAsync(CancellationToken.None));

        Assert.Contains("older than this opticli", ex.Message, StringComparison.Ordinal);
        Assert.Equal(AgentErrors.OutOfDateHint, ex.Hint);
    }

    // RequireDriftAcceptedAsync talks to the agent only; the content session isn't used.
    private static WriteExecutor Executor(FakeAgent agent, string? accept = null, Func<DriftReport, bool>? confirm = null, bool shared = true) =>
        new(null!, _ => Task.FromResult(agent.Client), acceptDrift: accept, confirmDrift: confirm, sharedDatabase: shared);

    private sealed class FakeAgent : HttpMessageHandler
    {
        private readonly DriftReport _report;

        public FakeAgent(DriftReport report)
        {
            _report = report;
            Client = new AgentClient(new Uri("http://127.0.0.1:5199"), "token", this);
        }

        public AgentClient Client { get; }

        public List<(string Route, string? AcceptedDrift)> Requests { get; } = [];

        /// <summary>Answers as an agent from before drift checks: no such route.</summary>
        public bool WithoutDriftRoute { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/')[(AgentProtocol.BasePath.Length)..];
            Requests.Add(($"{request.Method} {path}", request.Headers.TryGetValues(AgentProtocol.AcceptDriftHeader, out var values) ? values.Single() : null));
            var meta = new AgentMeta("agent", "0.6.0", AgentProtocol.Version);
            var drift = path.EndsWith("drift", StringComparison.Ordinal);
            var body = drift && WithoutDriftRoute
                ? JsonSerializer.Serialize(new AgentResponse<object>(false, null, meta, new AgentError(AgentErrorCodes.NotFound, $"No agent route for GET {AgentProtocol.BasePath}/{path}.")), AgentJson.Options)
                : JsonSerializer.Serialize(new AgentResponse<object>(true, drift ? _report : new WriteResult(), meta), AgentJson.Options);
            var status = drift && WithoutDriftRoute ? HttpStatusCode.NotFound : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
