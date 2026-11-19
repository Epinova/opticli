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
            yet, without asking the site (except as below). Without --version, versions someone else saved after the published one need
            confirming: a prompt on a terminal, elsewhere a conflict listing them, unless --include-draft. A named version
            is published as it is. Content with an approval sequence is refused (exit 3); --request-approval sends the
            version for review instead.
            Where the site makes new area blocks shared (serve --status: inlineBlocks: shared), the drafts of the blocks of
            the content's own "For this page" folder that it shows and that were never published go live with it
            (sharedBlocks); a live block's newer draft stays behind with a warning unless --with-block names it, and
            --without-block keeps a new one back (neither with a review: usage). There --dry-run asks the site, which
            checks the publish and lists both. When the version is published already, only those blocks' drafts go
            live (a conflict when there are none).
            Example: opticli publish 123_456 --dry-run
            Also:    opticli publish 123 --with-block 456
            """);
        content.AddTo(command);
        command.Options.Add(version);
        write.AddCommon(command, publish: false);
        write.AddIncludeDraft(command);
        write.AddRequestApproval(command);
        write.AddPublishAt(command);
        write.AddBlockChoice(command);

        CommandRunner.SetHandler<WriteOutput>(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var versionId = parse.GetValue(version) is { } text ? WriteOptions.ParseVersion(text, "--version") : (int?)null;
            var operation = new PublishOperation(parse.GetValue(content.Ref)!, versionId, parse.GetValue(content.Lang))
            {
                IncludeDraft = parse.GetValue(write.IncludeDraft),
                WithBlocks = WriteOptions.Refs(parse, write.WithBlock),
                WithoutBlocks = WriteOptions.Refs(parse, write.WithoutBlock),
            };
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(write.WithApproval(operation, parse), parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
