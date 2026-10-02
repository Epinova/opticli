using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Drift;

/// <summary>
/// Stops writes in shared mode while the site's code and the database differ, until the caller confirms the current
/// differences by their fingerprint. A write runs this build's models, validators and event handlers against content the
/// deployed site serves, so the user decides, not the coding agent. Dry runs save nothing and always pass.
/// </summary>
internal static class DriftGate
{
    /// <exception cref="AgentException"><c>drift</c>, with the report.</exception>
    public static void Check(AgentRequest request, bool dryRun)
    {
        if (dryRun || !request.Service<AgentSettings>().SharedDatabase)
        {
            return;
        }
        var report = request.Service<DriftCheck>().Report;
        var accepted = request.Context.Request.Headers[AgentProtocol.AcceptDriftHeader].ToString().Trim();
        if (Require(report, accepted.Length > 0 ? accepted : null) is { } refusal)
        {
            throw refusal;
        }
    }

    /// <returns>The error when <paramref name="report"/> has differences that <paramref name="accepted"/> doesn't confirm; null when the write may go ahead.</returns>
    public static AgentException? Require(DriftReport report, string? accepted)
    {
        if (report.Fingerprint is not { } fingerprint || string.Equals(accepted, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var changed = accepted is null ? "" : $" (confirmed was {accepted}, which no longer matches: the differences changed)";
        return new AgentException(
            AgentErrorCodes.Drift,
            $"The site's code and the shared database differ{changed}: {report.Describe()}. A write would run this build's code against content the deployed site serves.",
            $"Show the user the differences and ask; only if they agree, send {AgentProtocol.AcceptDriftHeader}: {fingerprint}.")
        {
            Drift = report,
        };
    }
}
