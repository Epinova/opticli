using Microsoft.Data.SqlClient;
using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Safety;

namespace OptiCli.Core.Seals;

/// <summary>How the sealed run signs in to production to write.</summary>
public enum WriteLogin
{
    /// <summary>A login with a password (SQL authentication, Entra ID password or service principal): the user types both, every run.</summary>
    Password,

    /// <summary>The user's own identity (integrated security, Microsoft Entra ID interactive, default, managed identity, …): nothing is typed.</summary>
    Identity,
}

/// <summary>The user and password typed at the sealed run's terminal. Never written anywhere, and never printed.</summary>
public sealed class WriteCredentials(string user, string password)
{
    public string User { get; } = user;

    internal string Password { get; } = password;

    public override string ToString() => $"{User} (password not shown)";
}

/// <summary>
/// The one way past opticli's refusal to write to production: a verified seal, the database's name typed by the user at
/// a terminal, and the write credentials they typed (or their own identity). Only <see cref="Authorize"/> makes one, and
/// only the sealed site launcher (<see cref="Serve.SealedSite"/>) takes one. Everything else that writes or starts the site
/// keeps refusing a <see cref="VerifiedConnectionString"/> with <see cref="VerifiedConnectionString.IsProduction"/>.
/// </summary>
public sealed class ProductionWrite
{
    private ProductionWrite(VerifiedSeal seal, string server, string? database, bool isLocal, string value)
    {
        Seal = seal;
        Server = server;
        Database = database;
        IsLocal = isLocal;
        Value = value;
    }

    public VerifiedSeal Seal { get; }

    public string Server { get; }

    public string? Database { get; }

    public bool IsLocal { get; }

    /// <summary>The saved production string with the write login in it and the read-write intent. Never printed or written.</summary>
    internal string Value { get; }

    /// <summary>
    /// The write connection for opticli's own reads during the run (production's primary, not a lagging read-only replica).
    /// Still production for opticli: nothing that writes or starts the site takes it.
    /// </summary>
    public VerifiedConnectionString ForReads() => new(Value, Server, Database, IsLocal) { IsProduction = true };

    /// <summary>How the saved (read-only) production connection signs in, and so what the run asks for.</summary>
    public static WriteLogin LoginOf(VerifiedConnectionString production)
    {
        var builder = new SqlConnectionStringBuilder(production.Value);
#pragma warning disable CS0618 // ActiveDirectoryPassword is deprecated, but a saved string may still use it: it takes a password.
        return !builder.IntegratedSecurity && builder.Authentication is SqlAuthenticationMethod.NotSpecified or SqlAuthenticationMethod.SqlPassword
            or SqlAuthenticationMethod.ActiveDirectoryPassword or SqlAuthenticationMethod.ActiveDirectoryServicePrincipal
            ? WriteLogin.Password
            : WriteLogin.Identity;
#pragma warning restore CS0618
    }

    /// <param name="production">The saved production connection the run's <c>--db</c> named (read-only login).</param>
    /// <param name="typedDatabase">What the user typed when asked for the database's name.</param>
    /// <param name="credentials">What the user typed for <see cref="WriteLogin.Password"/>; null for <see cref="WriteLogin.Identity"/>.</param>
    /// <exception cref="RefusedException">Not production, not the seal's database, or a failover partner in the string.</exception>
    /// <exception cref="UsageException">The name typed isn't the database's, or the credentials don't fit the login.</exception>
    public static ProductionWrite Authorize(VerifiedSeal seal, VerifiedConnectionString production, string? typedDatabase, WriteCredentials? credentials)
    {
        if (!production.IsProduction || DatabaseKey.Of(production.Server, production.Database) != seal.Seal.Target.Key)
        {
            throw new RefusedException($"'{production.Database}' on '{production.Server}' isn't the production database seal {seal.Seal.Id} is for.") { Details = new { reason = SealCheck.TargetReason } };
        }
        if (!string.Equals(typedDatabase?.Trim(), production.Database, StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException("Not confirmed (the database's name wasn't typed); nothing was written.");
        }
        var builder = new SqlConnectionStringBuilder(production.Value);
        if (!string.IsNullOrWhiteSpace(builder.FailoverPartner))
        {
            throw new RefusedException($"The production connection has a failover partner ('{builder.FailoverPartner}'), where SqlClient may write instead: a sealed apply writes only to the server it was sealed for.",
                "Remove Failover Partner from the saved connection string (`opticli db add <name> --role prod`).") { Details = new { reason = SealCheck.TargetReason } };
        }
        switch (LoginOf(production), credentials)
        {
            case (WriteLogin.Password, { } typed) when typed.User.Trim().Length > 0 && typed.Password.Length > 0:
                builder.UserID = typed.User.Trim();
                builder.Password = typed.Password;
                break;
            case (WriteLogin.Password, _):
                throw new UsageException("No write login was typed; nothing was written.");
            case (WriteLogin.Identity, null):
                break;
            default:
                throw new UsageException("This production connection signs in with the user's own identity: no credentials are typed for it.");
        }
        builder.ApplicationIntent = ApplicationIntent.ReadWrite;
        builder.PersistSecurityInfo = false;
        return new ProductionWrite(seal, production.Server, production.Database, production.IsLocal, builder.ConnectionString);
    }

    public override string ToString() => $"{Database} on {Server} (write)";
}
