using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class PublishCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var version = new Option<string?>("--version")
        {
            Description = "Version to publish (id or 123_456, see `opticli versions`). Default: the ref's version, else the latest version in the language.",
            HelpName = "id",
        };
        var write = new WriteOptions();
        var command = new Command("publish", """
            Publish a version, making it live (default: the latest version in the language). Needs `opticli serve`.
            The CMS validates it first (e.g. required properties). --dry-run checks that the version exists and isn't published
            yet, without asking the site. Without --version, versions someone else saved after the published one need
            confirming: a prompt on a terminal, elsewhere a conflict listing them, unless --include-draft. A named version
            is published as it is. Content with an approval sequence is refused (exit 3); --request-approval sends the
            version for review instead.
            Example: opticli publish 123_456 --dry-run
            """);
        content.AddTo(command);
        command.Options.Add(version);
        write.AddCommon(command, publish: false);
        write.AddIncludeDraft(command);
        write.AddRequestApproval(command);
        write.AddPublishAt(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var versionId = parse.GetValue(version) is { } text ? WriteOptions.ParseVersion(text, "--version") : (int?)null;
            var operation = new PublishOperation(parse.GetValue(content.Ref)!, versionId, parse.GetValue(content.Lang))
            {
                IncludeDraft = parse.GetValue(write.IncludeDraft),
            };
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(write.WithApproval(operation, parse), parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
