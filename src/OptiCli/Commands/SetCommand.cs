using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class SetCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var write = new WriteOptions();
        var name = new Option<string?>("--name") { Description = "Rename the content (in this language).", HelpName = "name" };
        var command = new Command("set", """
            Change properties (or the name) on a new version, a draft unless --publish. Needs `opticli serve`.
            Values are parsed by the CMS like imported values; property names are checked against the content type first.
            --publish puts the whole new version live: if someone else saved changes after the published version, it asks
            on a terminal and elsewhere fails with a conflict listing them, unless --include-draft.
            Structured values (ContentArea items, links, lists) go in --values as JSON. Prints ref, the new version,
            baseVersion, status and every changed property (before/after).
            Example: opticli set 123 Heading="New title" MainBody=@body.html --dry-run
            Also:    opticli set 123 --values '{"MainArea":[{"ref":"456"},{"ref":"789","displayOption":"wide"}]}'
            """);
        content.AddTo(command);
        write.AddProperties(command);
        command.Options.Add(name);
        write.AddCommon(command);
        write.AddIncludeDraft(command);
        write.AddConcurrency(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new SetOperation(
                parse.GetValue(content.Ref)!,
                write.ParseProperties(context),
                parse.GetValue(name),
                parse.GetValue(content.Lang),
                parse.GetValue(write.Publish),
                write.ParseBaseVersion(context),
                parse.GetValue(write.Force))
            {
                IncludeDraft = parse.GetValue(write.IncludeDraft),
            };
            await using var session = await context.OpenContentAsync(cancellationToken);
            var outcome = await context.Writes(session, parse.GetValue(content.Site)).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken);
            return WriteOptions.Result(outcome);
        });
        return command;
    }
}
