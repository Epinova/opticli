using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Drift;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Protocol;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Drift;

/// <summary>Writes in shared mode stop while the code and the database differ, until the caller confirms the fingerprint.</summary>
public class DriftGateTests
{
    private static readonly DriftReport Drifted = DriftReport.Create(
        [new DriftItem("NewsPage", DriftAhead.Local, ContentModelComparison.OnlyInCode)], [], [], [], [], []);

    private static readonly DriftReport Clean = DriftReport.Create([], [], [], [], [], []);

    private static AgentRequest Request(DriftReport report, bool shared = true, string? accepted = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(Settings(pinned: shared ? Remote : Local, approvedRemote: shared ? RemoteApproval : null))
            .AddSingleton(new DriftCheck(() => report))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        if (accepted is not null)
        {
            context.Request.Headers[AgentProtocol.AcceptDriftHeader] = accepted;
        }
        return new AgentRequest(context, null);
    }

    [Fact]
    public void A_write_stops_with_the_report_and_its_fingerprint()
    {
        var ex = Assert.Throws<AgentException>(() => DriftGate.Check(Request(Drifted), dryRun: false));

        Assert.Equal(AgentErrorCodes.Drift, ex.Code);
        Assert.Equal(409, ex.Status);
        Assert.Same(Drifted, ex.ToError().Drift);
        Assert.Contains("NewsPage (only in the code)", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Drifted.Fingerprint!, ex.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_current_fingerprint_confirms_it()
    {
        DriftGate.Check(Request(Drifted, accepted: Drifted.Fingerprint), dryRun: false);
        DriftGate.Check(Request(Drifted, accepted: $" {Drifted.Fingerprint!.ToUpperInvariant()} "), dryRun: false);
    }

    [Fact]
    public void A_fingerprint_of_other_differences_does_not()
    {
        var before = DriftReport.Create([new DriftItem("EventPage", DriftAhead.Local, ContentModelComparison.OnlyInCode)], [], [], [], [], []);

        var ex = Assert.Throws<AgentException>(() => DriftGate.Check(Request(Drifted, accepted: before.Fingerprint), dryRun: false));

        Assert.Contains($"confirmed was {before.Fingerprint}, which no longer matches", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dry_runs_and_writes_without_drift_pass()
    {
        DriftGate.Check(Request(Drifted), dryRun: true);
        DriftGate.Check(Request(Clean), dryRun: false);
    }

    [Fact]
    public void A_local_database_is_never_compared()
    {
        var computed = false;
        var services = new ServiceCollection()
            .AddSingleton(Settings(pinned: Local))
            .AddSingleton(new DriftCheck(() => { computed = true; return Drifted; }))
            .BuildServiceProvider();

        DriftGate.Check(new AgentRequest(new DefaultHttpContext { RequestServices = services }, null), dryRun: false);

        Assert.False(computed);
        Assert.False(DriftCheck.Compute(services, Settings(pinned: Local)).Checked);
    }

    [Fact]
    public void A_comparison_that_fails_stops_writes_too()
    {
        // No CMS in a unit test: comparing the content model throws, which must not let writes through.
        var report = CaptureCompute();

        Assert.True(report.Checked);
        Assert.NotNull(report.Fingerprint);
        Assert.Contains(report.ContentTypes, i => i.Name == "(content model)" && i.Ahead == DriftAhead.Unknown && i.Difference.StartsWith("couldn't be compared", StringComparison.Ordinal));
        Assert.Contains(DriftCheck.NoStartupNote, report.Notes);
    }

    private static DriftReport CaptureCompute()
    {
        DriftReport? report = null;
        Stderr(() => report = DriftCheck.Compute(new ServiceCollection().BuildServiceProvider(), Settings(pinned: Remote, approvedRemote: RemoteApproval)));
        return report!;
    }

    [Fact]
    public void What_serve_compared_before_the_start_is_read_from_its_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"opticli-drift-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"migrations":[{"name":"20260101000000_AddEvents","ahead":"local","difference":"in this build (EventsContext), not applied to the database"}],"notes":["n"]}""");
        try
        {
            var notes = new List<string>();
            var startup = DriftCheck.ReadStartup(path, notes);

            Assert.Equal("20260101000000_AddEvents", Assert.Single(startup.Migrations).Name);
            Assert.Equal(["n"], startup.Notes);
            Assert.Empty(notes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Without_that_file_a_note_says_what_was_not_compared()
    {
        var notes = new List<string>();

        Assert.Empty(DriftCheck.ReadStartup(null, notes).Migrations);
        Assert.Equal([DriftCheck.NoStartupNote], notes);

        notes.Clear();
        DriftCheck.ReadStartup(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"), notes);
        Assert.StartsWith("What `opticli serve` compared before the start", Assert.Single(notes), StringComparison.Ordinal);
    }
}
