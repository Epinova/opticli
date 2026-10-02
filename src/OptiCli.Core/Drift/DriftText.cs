using OptiCli.Protocol;

namespace OptiCli.Core.Drift;

/// <summary>How drift is put in warnings and prompts.</summary>
public static class DriftText
{
    /// <summary>Warnings that start with this are about drift; a response carries one at most.</summary>
    public const string Prefix = "drift: ";

    /// <summary>The short <c>meta.warnings</c> entry every response in shared mode with drift carries.</summary>
    public static string Warning(DriftReport report) =>
        $"{Prefix}this build and the shared database differ ({report.Differences} difference{(report.Differences == 1 ? "" : "s")}, {Short(report.Ahead)}). "
        + "Reads are unaffected; writes stop until the user confirms. `opticli drift` lists the differences.";

    /// <summary>What <c>serve</c> says once the site answers.</summary>
    public static string Started(DriftReport report) =>
        $"{Prefix}{report.Describe()}. Writes stop until the user confirms (--accept-drift); `opticli drift` lists every difference.";

    /// <summary>"local is ahead", "the database is ahead", ...</summary>
    public static string Short(string? ahead) => ahead switch
    {
        DriftAhead.Local => "local is ahead",
        DriftAhead.Database => "the database is ahead",
        DriftAhead.Both => "both are ahead",
        _ => "direction unknown",
    };

    /// <summary>What to do about it, by direction.</summary>
    public static string Advice(string? ahead) => ahead switch
    {
        DriftAhead.Local => "This branch has changes that aren't deployed there: check out what is deployed, or deploy first.",
        DriftAhead.Database => "The environment runs newer code than this checkout: pull and build.",
        DriftAhead.Both => "This branch and the environment each have changes the other lacks: check out what is deployed, or pull and deploy.",
        _ => "Compare this checkout with what is deployed there.",
    };
}
