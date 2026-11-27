using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Commerce;

/// <summary>
/// A Commerce removal (a relation, a price, a market with its prices) is for good: Commerce has no recycle bin. On a shared
/// database (shared mode), where colleagues' and the environment's data is, it needs the developer's
/// <c>--allow-destructive</c>, as content there is only ever moved to the recycle bin.
/// </summary>
internal static class SharedRemovals
{
    public const string Reason = "sharedDatabase";

    /// <exception cref="AgentException">A removal on a shared database without <paramref name="allowDestructive"/> (also in a dry run).</exception>
    public static void Check(AgentRequest request, bool remove, bool allowDestructive, string what)
    {
        if (remove && !allowDestructive && request.Service<AgentSettings>().SharedDatabase)
        {
            throw new AgentException(AgentErrorCodes.Refused,
                $"Removing {what} on a shared database is for good (Commerce has no recycle bin), and it is your colleagues' data too.",
                "Check with them, then run it again with --allow-destructive (a plan step's \"allowDestructive\": true).")
            {
                Reason = Reason,
            };
        }
    }
}
