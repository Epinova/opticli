using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class UnpublishCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var write = new WriteOptions();
        var command = new Command("unpublish", """
            Take a published language branch offline, as the edit UI's expiry does. Needs `opticli serve`. A copy of the
            published version that stops publishing now is published, so visitors no longer see it; drafts stay as they are.
            Undo with `opticli publish <ref> --version <previouslyPublished>`. Start pages, site and asset roots are refused,
            and so is content with an approval sequence (set StopPublish on a draft and send it with --request-approval).
            Example: opticli unpublish 123 --dry-run
            """);
        content.AddTo(command);
        write.AddCommon(command, publish: false);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new UnpublishOperation(parse.GetValue(content.Ref)!, parse.GetValue(content.Lang));
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
