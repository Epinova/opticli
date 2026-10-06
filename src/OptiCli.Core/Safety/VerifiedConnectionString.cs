namespace OptiCli.Core.Safety;

/// <summary>
/// A connection string that was checked and chosen: local (<see cref="ConnectionSafety.Verify"/>), or remote and
/// approved by the connection resolver. Only those can create one, and <see cref="Data.CmsDatabase"/> only accepts
/// this type, so there is no code path that opens a connection to a server nobody chose.
/// </summary>
public sealed class VerifiedConnectionString
{
    internal VerifiedConnectionString(string value, string server, string? database, bool isLocal)
    {
        Value = value;
        Server = server;
        Database = database;
        IsLocal = isLocal;
    }

    public string Server { get; }

    public string? Database { get; }

    /// <summary>A SQL Server on this machine; otherwise a remote (usually shared) one.</summary>
    public bool IsLocal { get; }

    /// <summary>The normalised connection string opticli connects with, credentials included. Never printed.</summary>
    internal string Value { get; }

    /// <summary>
    /// The string to give the site: <see cref="Value"/>, except that a <c>|DataDirectory|</c> the site expands itself
    /// (<see cref="ConnectionSafety.ResolveDataDirectory"/>) stays as configured.
    /// </summary>
    internal string ForSite { get => _forSite ?? Value; init => _forSite = value; }

    private readonly string? _forSite;

    public override string ToString() => ConnectionStringRedactor.Redact(Value);
}
