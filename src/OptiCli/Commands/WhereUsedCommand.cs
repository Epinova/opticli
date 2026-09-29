using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class WhereUsedCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var list = new ListOptions(options);
        var pages = new Option<bool>("--pages")
        {
            Description = $"Follow owners that are blocks (nested blocks, list blocks) up to the pages and other content using them, newest saved first; each row's via lists the blocks in between (at most {PageUsageReader.MaxDepth} deep).",
        };
        var command = new Command("where-used", """
            List everything that references a content item (a block, page or media file), with each owner's last save.
            Covers ContentAreas, ContentReference/PageReference properties and lists, link items and collections, rich-text
            links and embedded blocks, including values inside local and inline blocks. Combines the CMS link index
            (tblContentSoftlink, all saved versions) with a scan of each branch's primary (published, else latest) values;
            each usage gives the owner (ref, type, name, language, status, url, saved, changedBy), the property path, the
            kind and which source found it. Owners that are blocks: add --pages to follow them up to the pages.
            Example: opticli where-used 123 --pages
            """);
        content.AddTo(command, withLang: false);
        command.Options.Add(pages);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            var located = await content.LocateAsync(context, session, cancellationToken);
            var target = await session.HeaderAsync(located.Id, cancellationToken);
            if (context.Parse.GetValue(pages))
            {
                var reached = await new PageUsageReader(session).FindAsync(target, cancellationToken);
                return CommandResult.From(list.Apply(context.Parse, reached.Select(Flatten).ToList()));
            }
            var usages = await new WhereUsedReader(session).FindAsync(target, cancellationToken);
            return CommandResult.From(list.Apply(context.Parse, usages));
        });
        return command;
    }

    /// <summary>The usage's own fields plus <c>via</c>, so rows read like plain where-used rows.</summary>
    private static System.Text.Json.Nodes.JsonObject Flatten(PageUsage usage)
    {
        var row = Core.Output.JsonOutput.ToNode(usage.Usage)!.AsObject();
        if (usage.Via.Count > 0)
        {
            row["via"] = Core.Output.JsonOutput.ToNode(usage.Via);
        }
        return row;
    }
}
