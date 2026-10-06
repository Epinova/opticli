using OptiCli.Agent.Hosting;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Hosting;

/// <summary>The hosting tests capture <see cref="Console.Error"/>, where the agent reports its checks, so they run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostingCollection
{
    public const string Name = "Hosting";
}

internal static class HostingFixture
{
    public const string Local = "Server=localhost;Database=ExampleDb;Integrated Security=true";
    public const string OtherLocal = "Server=(localdb)\\MSSQLLocalDB;Database=OtherDb;Integrated Security=true";
    public const string Remote = "Server=tcp:dev.example.net,1433;Database=ExampleDb;User Id=app;Password=secret";
    public const string OtherRemote = "Server=tcp:prod.example.net,1433;Database=ExampleDb;User Id=app;Password=secret";

    /// <param name="scheduler">As <c>serve --scheduler</c>: leave the site's scheduler as it is.</param>
    public static AgentSettings Settings(string? pinned = null, string? approvedRemote = null, bool scheduler = false) =>
        new(AgentProtocol.DefaultConnectionName, pinned, "token", AgentProtocol.ParseRemote(approvedRemote), Scheduler: scheduler);

    /// <summary>The approval the CLI passes for <see cref="Remote"/>.</summary>
    public const string RemoteApproval = "tcp:dev.example.net,1433|ExampleDb";

    /// <summary>Runs <paramref name="action"/> and returns what it wrote to stderr.</summary>
    public static string Stderr(Action action)
    {
        var previous = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            action();
        }
        finally
        {
            Console.SetError(previous);
        }
        return captured.ToString();
    }
}
