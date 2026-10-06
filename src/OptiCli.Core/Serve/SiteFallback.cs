using OptiCli.Core.Errors;

namespace OptiCli.Core.Serve;

/// <summary>
/// One rule for the reads that ask the running site when they can (<c>trash</c>'s stored parents,
/// <c>types --orphaned</c>) and read the database otherwise: the site's answer when it gives one, else the reason it
/// didn't, for the warning that says the answer is the database's.
/// </summary>
public static class SiteFallback
{
    /// <param name="Value">The site's answer; null when it wasn't asked or didn't answer.</param>
    /// <param name="WhyNot">Why not: <c>serve</c> isn't running, the site's agent is older than this opticli, or what the site said.</param>
    public sealed record Answer<T>(T? Value, string? WhyNot) where T : class;

    public const string NotRunning = "`opticli serve` isn't running";

    public const string OutOfDate = "the running site's agent is older than this opticli";

    /// <param name="connect">Connects to the site's agent (<c>CliContext.ConnectAgentAsync</c>).</param>
    /// <param name="ask">The question; an <see cref="OptiCliException"/> from it is the site's answer that it can't.</param>
    public static async Task<Answer<T>> AskAsync<T>(Func<CancellationToken, Task<AgentClient>> connect, Func<AgentClient, Task<T>> ask, CancellationToken cancellationToken)
        where T : class
    {
        AgentClient agent;
        try
        {
            agent = await connect(cancellationToken);
        }
        catch (OptiCliException ex)
        {
            return new Answer<T>(null, ex is UnreachableException && ex.Message.StartsWith("No opticli agent is running", StringComparison.Ordinal)
                ? NotRunning
                : $"the site couldn't be asked: {ex.Message.TrimEnd('.')}");
        }
        try
        {
            return new Answer<T>(await ask(agent), null);
        }
        catch (NotFoundException ex) when (ex.Message.StartsWith("No agent route", StringComparison.Ordinal))
        {
            return new Answer<T>(null, OutOfDate);
        }
        catch (OptiCliException ex)
        {
            return new Answer<T>(null, $"the site answered with an error: {ex.Message.TrimEnd('.')}");
        }
    }
}
