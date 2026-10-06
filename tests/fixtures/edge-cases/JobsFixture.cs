// Copied into the edge-case site by setup.sh, for opticli's `jobs` integration tests: a scheduled job that runs for a
// few seconds, reports its progress, can be stopped and can be made to fail; and, on request, a scheduler that the site
// itself turns on, as a real site's is (Alloy turns it off in Development).
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.PlugIn;
using EPiServer.Scheduler;
using EPiServer.Security;
using EPiServer.ServiceLocation;

namespace OptiCliEdgeCases;

/// <summary>
/// Manual only, so it never runs on its own. Writes a status message per step, one a second (3 steps; the number in
/// <c>App_Data/opticli-job-steps</c> if that file exists), then returns a message naming the user it ran as. Fails with
/// an exception when <c>App_Data/opticli-job-fail</c> exists. Stoppable: <see cref="Stop"/> ends it within a step.
/// </summary>
[ScheduledPlugIn(
    DisplayName = "opticli test job",
    Description = "opticli edge-case fixture: runs a few seconds; App_Data/opticli-job-fail makes it fail",
    GUID = "5E0B1D2C-7A3F-4B6E-9D10-3C4B5A6F7E81")]
public class OptiCliTestJob : ScheduledJobBase
{
    public const string FailMarker = "opticli-job-fail";

    public const string StepsFile = "opticli-job-steps";

    private readonly IWebHostEnvironment _environment;
    private readonly IPrincipalAccessor _principal;
    private volatile bool _stopping;

    public OptiCliTestJob(IWebHostEnvironment environment, IPrincipalAccessor principal)
    {
        _environment = environment;
        _principal = principal;
        IsStoppable = true;
    }

    public override void Stop() => _stopping = true;

    public override string Execute()
    {
        var data = Path.Combine(_environment.ContentRootPath, "App_Data");
        var steps = File.Exists(Path.Combine(data, StepsFile)) && int.TryParse(File.ReadAllText(Path.Combine(data, StepsFile)).Trim(), out var n) && n > 0 ? n : 3;
        for (var step = 1; step <= steps; step++)
        {
            OnStatusChanged($"Step {step} of {steps}");
            for (var tick = 0; tick < 10; tick++)
            {
                if (_stopping)
                {
                    return $"Stopped at step {step} of {steps}.";
                }
                Thread.Sleep(100);
            }
        }
        if (File.Exists(Path.Combine(data, FailMarker)))
        {
            throw new InvalidOperationException($"The opticli test job failed on purpose (App_Data/{FailMarker} exists).");
        }
        return $"Done: {steps} steps, as {_principal.Principal?.Identity?.Name ?? "nobody"}.";
    }
}

/// <summary>
/// With <c>OPTICLI_FIXTURE_SCHEDULER=on</c> in the site's environment, turns the scheduler on in the site's own
/// configuration, after Alloy's Startup turns it off: the site then starts overdue jobs as a real site does, unless
/// opticli turns its scheduler off.
/// </summary>
[InitializableModule]
public class OptiCliSchedulerFixture : IConfigurableModule
{
    public void ConfigureContainer(ServiceConfigurationContext context)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("OPTICLI_FIXTURE_SCHEDULER"), "on", StringComparison.OrdinalIgnoreCase))
        {
            context.Services.Configure<SchedulerOptions>(options => options.Enabled = true);
        }
    }

    public void Initialize(InitializationEngine context)
    {
    }

    public void Uninitialize(InitializationEngine context)
    {
    }
}
