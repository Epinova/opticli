using OptiCli.Core.Errors;

namespace OptiCli.Core.Safety;

/// <summary>
/// The refusals for a database with the role <c>prod</c>: opticli reads it when asked to for a run (<c>--db</c>), and
/// never writes to it, runs the site against it or makes it the development database. Every one is <c>refused</c>
/// (exit 3) with <c>details.reason</c> <see cref="Reason"/>.
/// </summary>
public static class ProductionDatabase
{
    public const string Reason = "prodDatabase";

    private const string NoWritesHint =
        "opticli doesn't write to a production database, or run the site against one: write to the development database "
        + "instead. Reading it for one run with --db <name|id> works. An agent tells the user and doesn't look for another way in.";

    /// <summary>A write, or <c>serve</c>/<c>env</c>, against it.</summary>
    /// <param name="action">What was refused, e.g. "write to" or "run the site against".</param>
    /// <param name="hint">Another hint than the one about writes.</param>
    public static RefusedException Refusal(string action, string? server, string? database, string? hint = null) =>
        new($"Refusing to {action} '{database}' on '{server}': it is a production database (role prod), which opticli only reads.", hint ?? NoWritesHint)
        {
            Details = new { reason = Reason, server, database },
        };

    /// <summary>What a refused <c>db use</c> says.</summary>
    public const string ChoiceHint =
        "A production database is never the development database, so a command without --db never reaches it. Read it for one "
        + "run with --db <name|id>. The user chooses another development database (ask them; don't choose for them).";

    /// <summary>The development database (chosen, configured or the site's default) turned out to be production.</summary>
    /// <param name="how">How it became the development database, e.g. "chosen with `opticli db use`".</param>
    public static RefusedException DefaultRefusal(string? server, string? database, string how, string hint) =>
        new($"The development database '{database}' on '{server}' ({how}) is a production database (role prod): opticli never uses one by default.", hint)
        {
            Details = new { reason = Reason, server, database },
        };

    /// <exception cref="RefusedException">The connection is to a production database.</exception>
    public static void RequireNot(VerifiedConnectionString connection, string action)
    {
        if (connection.IsProduction)
        {
            throw Refusal(action, connection.Server, connection.Database);
        }
    }
}
