using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Drift;
using OptiCli.Core.Output;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Commands;

/// <summary>What differs between the local build and the shared database it runs against; read-only.</summary>
internal static class DriftCommand
{
    public const string LocalNote = "The database is local: the site's content type sync commits this build's models to it, so there is nothing to compare.";

    public static Command Create(GlobalOptions options)
    {
        var command = new Command("drift", """
            What differs between this build and the shared (remote) development database; read-only. Needs `opticli serve`.
            Against a shared database the site doesn't sync its content types into it, so the database keeps what the deployed
            code made. drift compares them as the sync would (content types and properties), plus EF Core migrations, Dynamic
            Data Store types and the CMS schema version. Each difference says which side is ahead: local (this branch has
            changes that aren't deployed there) or database (the database has what this build lacks: that environment runs
            newer code than this checkout, or a class or property was removed from code and the CMS kept it for the content
            that uses it; `opticli types --orphaned` lists such types). For a content type or property only in the database,
            removedFromCode names the commit in this checkout's git history that removed it from code, when git finds one
            (looked up in a git work tree only, for up to 50 items, within a few seconds).
            While anything differs, writes stop until the user confirms with --accept-drift <fingerprint>; the fingerprint
            changes when the differences do. On CMS 13, types of unknown origin (no class or version on record: made in admin
            mode, or imported) are listed with informational: true and don't stop writes. New job classes the site registers at
            startup aren't drift (the deployed site registers them too). Against a local database nothing is compared: the
            sync commits there.
            Example: opticli drift
            """);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            if (context.UseConnection().IsLocal)
            {
                var local = new DriftReport { Checked = false, Notes = [LocalNote] };
                return new CommandResult(local, Text: Text(local), Source: CommandResult.CliSource);
            }
            var agent = await context.ConnectAgentAsync(cancellationToken);
            var report = await WithHistoryAsync(context, await agent.DriftAsync(cancellationToken), cancellationToken);
            var warnings = report.Fingerprint is null
                ? null
                : new[] { $"{DriftText.Prefix}{DriftText.Short(report.Ahead)}. {DriftText.Advice(report)} Writes stop until the user confirms with --accept-drift {report.Fingerprint}." };
            return new CommandResult(report, Text: Text(report), Warnings: warnings, Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    /// <summary>
    /// The report with the commits that removed from code what only the database has (<see cref="DriftHistory"/>); the
    /// database gives the types' GUIDs, and without it the lookup goes by name only.
    /// </summary>
    internal static async Task<DriftReport> WithHistoryAsync(CliContext context, DriftReport report, CancellationToken cancellationToken)
    {
        if (!DriftHistory.Applies(report) || context.TryGetProject(out _) is not { } project)
        {
            return report;
        }
        IReadOnlyList<Core.Cms.ContentTypeInfo> types;
        try
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            types = await Core.Cms.ContentTypeReader.ListAsync(db, cancellationToken);
        }
        catch (Core.Errors.OptiCliException)
        {
            types = [];
        }
        return DriftHistory.Annotate(report, project.Directory, types);
    }

    /// <summary>One row per difference, for tables.</summary>
    public static IEnumerable<object> Rows(DriftReport report) =>
        report.Sections().Select(s => new { What = Label(s.Section), s.Item.Name, s.Item.Ahead, Difference = DriftReport.Shown(s.Section, s.Item) });

    private static string Text(DriftReport report)
    {
        using var text = new StringWriter();
        if (report.Fingerprint is null)
        {
            text.WriteLine(report.Checked ? "Nothing differs between this build and the database." : "Not compared.");
            if (report.Sections().Any())
            {
                // Informational items only (CMS 13 types of unknown origin): listed, but they don't stop writes.
                text.WriteLine("Listed, but they don't stop writes:");
                text.WriteLine();
                TextRenderer.Render(JsonOutput.ToNode(Rows(report)), text);
            }
        }
        else
        {
            text.WriteLine($"{report.Differences} difference{(report.Differences == 1 ? "" : "s")}, {report.DescribeAhead()}. Fingerprint: {report.Fingerprint}");
            text.WriteLine($"{DriftText.Advice(report)} Writes stop until the user confirms with --accept-drift {report.Fingerprint}.");
            text.WriteLine();
            TextRenderer.Render(JsonOutput.ToNode(Rows(report)), text);
        }
        foreach (var note in report.Notes)
        {
            text.WriteLine($"note: {note}");
        }
        return text.ToString();
    }

    private static string Label(string section) => section switch
    {
        "contentTypes" => "content type",
        "properties" => "property",
        "migrations" => "EF Core migration",
        "stores" => "DDS store",
        _ => "CMS schema",
    };
}
