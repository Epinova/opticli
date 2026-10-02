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
            changes that aren't deployed there) or database (that environment runs newer code than this checkout).
            While anything differs, writes stop until the user confirms with --accept-drift <fingerprint>; the fingerprint
            changes when the differences do. Against a local database nothing is compared: the sync commits there.
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
            var report = await agent.DriftAsync(cancellationToken);
            var warnings = report.Fingerprint is null
                ? null
                : new[] { $"{DriftText.Prefix}{DriftText.Short(report.Ahead)}. {DriftText.Advice(report.Ahead)} Writes stop until the user confirms with --accept-drift {report.Fingerprint}." };
            return new CommandResult(report, Text: Text(report), Warnings: warnings, Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    /// <summary>One row per difference, for tables.</summary>
    public static IEnumerable<object> Rows(DriftReport report) =>
        report.Sections().Select(s => new { What = Label(s.Section), s.Item.Name, s.Item.Ahead, s.Item.Difference });

    private static string Text(DriftReport report)
    {
        using var text = new StringWriter();
        if (report.Fingerprint is null)
        {
            text.WriteLine(report.Checked ? "Nothing differs between this build and the database." : "Not compared.");
        }
        else
        {
            text.WriteLine($"{report.Differences} difference{(report.Differences == 1 ? "" : "s")}, {DriftAhead.Describe(report.Ahead)}. Fingerprint: {report.Fingerprint}");
            text.WriteLine($"{DriftText.Advice(report.Ahead)} Writes stop until the user confirms with --accept-drift {report.Fingerprint}.");
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
