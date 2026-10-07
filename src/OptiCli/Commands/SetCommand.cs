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
        var variation = new Option<string?>("--variation") { Description = "CMS 13: change this content variation (its key, as versions lists it) instead of the content itself; a variation without a version yet is made from the published version.", HelpName = "key" };
        var command = new Command("set", """
            Change properties (or the name) on a new version, a draft unless --publish. Needs `opticli serve`.
            Values are parsed by the CMS like imported values; property names are checked against the content type first.
            --publish puts the whole new version live: if someone else saved changes after the published version, it asks
            on a terminal and elsewhere fails with a conflict listing them, unless --include-draft. To leave such a draft
            out, base the change on the published version instead of the latest: --from published (or --from <version>).
            Structured values (ContentArea items, links, lists) go in --values as JSON. Prints ref, the new version,
            baseVersion, status and every changed property (before/after).
            CMS 13: composition (in --values, or composition=@file.json) is a Visual Builder experience's whole composition, as
            get shows it: nodes with a key keep their block (given properties are set, others kept), nodes without one are new,
            nodes left out are removed. `opticli composition` changes one node at a time.
            Example: opticli set 123 Heading="New title" MainBody=@body.html --dry-run
            Also:    opticli set 123 --values '{"MainArea":[{"ref":"456"},{"ref":"789","displayOption":"wide"}]}'
            """);
        content.AddTo(command);
        write.AddProperties(command);
        command.Options.Add(name);
        command.Options.Add(variation);
        write.AddCommon(command);
        write.AddPublishAt(command);
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
                From = write.ParseFrom(context),
                Variation = parse.GetValue(variation),
            };
            await using var session = await context.OpenContentAsync(cancellationToken);
            var outcome = await context.Writes(session, parse.GetValue(content.Site)).RunAsync(write.WithApproval(operation, parse), parse.GetValue(write.DryRun), cancellationToken);
            return WriteOptions.Result(outcome);
        });
        return command;
    }
}
