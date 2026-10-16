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

    /// <summary>What <c>serve</c> says once the site answers; when the git history accounts for what only the database has, that too.</summary>
    public static string Started(DriftReport report) =>
        $"{Prefix}{report.Describe()}. {(DriftHistory.AllRemovedFromCode(report) ? Advice(report) + " " : "")}Writes stop until the user confirms (--accept-drift); `opticli drift` lists every difference.";

    /// <summary>"local is ahead", "the database is ahead", ...</summary>
    public static string Short(string? ahead) => ahead switch
    {
        DriftAhead.Local => "local is ahead",
        DriftAhead.Database => "the database is ahead",
        DriftAhead.Both => "both are ahead",
        _ => "direction unknown",
    };

    /// <summary>What to do about it, by direction.</summary>
    /// <param name="contentModel">
    /// A content type or property is on the database's side, which may be a class removed from code; false when only EF
    /// Core migrations or the CMS schema version are, which only newer code makes.
    /// </param>
    public static string Advice(string? ahead, bool contentModel = true) => ahead switch
    {
        DriftAhead.Local => "This branch has changes that aren't deployed there: check out what is deployed, or deploy first.",
        DriftAhead.Database when contentModel => "The database has what this build lacks: newer code in that environment (pull and build), or classes or "
            + "properties removed from code that the CMS kept for their content (`opticli types --orphaned`; pulling won't change those).",
        DriftAhead.Database => "The environment runs newer code than this checkout: pull and build.",
        DriftAhead.Both when contentModel => "This branch has changes that aren't deployed there, and the database has what this build lacks (newer code "
            + "there, or classes or properties removed from code that the CMS kept for their content: `opticli types --orphaned`): check out what is "
            + "deployed, or pull and deploy.",
        DriftAhead.Both => "This branch and the environment each have changes the other lacks: check out what is deployed, or pull and deploy.",
        _ => "Compare this checkout with what is deployed there.",
    };

    /// <summary>
    /// What to do about it: by direction and what is on the database's side, unless this checkout's git history removed
    /// all of it (<see cref="DriftItem.RemovedFromCode"/>), which pulling won't change.
    /// </summary>
    public static string Advice(DriftReport report)
    {
        if (!DriftHistory.AllRemovedFromCode(report))
        {
            return Advice(report.Ahead, report.ContentModelInDatabase);
        }
        const string Removed = "this checkout's git history removed everything only the database has (removedFromCode names the commit): the CMS kept it "
            + "for its content (`opticli types --orphaned`), or that environment doesn't run the removal yet. Pulling won't change that, nor will writing "
            + "with this build.";
        return report.Ahead == DriftAhead.Both
            ? $"This branch has changes that aren't deployed there (check out what is deployed, or deploy first), and {Removed}"
            : $"{char.ToUpperInvariant(Removed[0])}{Removed[1..]}";
    }
}
