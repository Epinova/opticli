using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class DiscardCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var version = new Option<string?>("--version")
        {
            Description = "Version to discard (id or 123_456, see `opticli versions`). Default: the ref's version, else the newest version in the language.",
            HelpName = "id",
        };
        var write = new WriteOptions();
        var command = new Command("discard", """
            Delete an unpublished version (a draft). Needs `opticli serve`. This can't be undone: the dry run shows what the
            version holds (changes, compared with the published version or else the one before). The published version,
            versions published before, the only version and a version in review are refused. A version someone else saved
            needs confirming: a prompt on a terminal, elsewhere a conflict listing its changes, unless --include-draft.
            Example: opticli discard 123_456 --dry-run
            """);
        content.AddTo(command);
        command.Options.Add(version);
        write.AddCommon(command, publish: false);
        write.IncludeDraft.Description = "Discard it even when someone other than opticli saved it. Without it that asks on a terminal, and elsewhere fails with a conflict (exit 5) that shows its changes.";
        write.AddIncludeDraft(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var versionId = parse.GetValue(version) is { } text ? WriteOptions.ParseVersion(text, "--version") : (int?)null;
            var operation = new DiscardOperation(parse.GetValue(content.Ref)!, versionId, parse.GetValue(content.Lang))
            {
                IncludeDraft = parse.GetValue(write.IncludeDraft),
            };
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
