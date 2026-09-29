namespace OptiCli.Core.Configuration;

/// <param name="Name">Connection string name (<c>--connection-name</c>).</param>
/// <param name="Explicit">Value of <c>--connection</c>, if given.</param>
/// <param name="Profile">Launch profile name (<c>--profile</c>), if given.</param>
/// <param name="Database">Candidate id or database name (<c>--db</c>), if given.</param>
public sealed record ConnectionRequest(string Name = ConnectionRequest.DefaultName, string? Explicit = null, string? Profile = null, string? Database = null)
{
    public const string DefaultName = "EPiServerDB";

    public const string EnvironmentVariable = "OPTICLI_DB";
}
