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
        var type = new Option<string?>("--type")
        {
            Description = "Instead of one item: every instance of this content type (name or GUID), each with its usages (count, usages), the most used first and unused ones last.",
            HelpName = "type",
        };
        content.Ref.Arity = ArgumentArity.ZeroOrOne;
        var command = new Command("where-used", """
            List everything that references a content item (a block, page or media file), with each owner's last save.
            Covers ContentAreas, ContentReference/PageReference properties and lists, link items and collections, rich-text
            links and embedded blocks, including values inside local and inline blocks. Combines the CMS link index
            (tblContentSoftlink, all saved versions) with a scan of each branch's primary (published, else latest) values;
            each usage gives the owner (ref, type, name, language, status, url, saved, changedBy), the property path, the
            kind and which source found it, and for rich text only some visitor groups see, their visitorGroups. Owners that
            are blocks: add --pages to follow them up to the pages. --type lists every instance of a type with its usages.
            CMS 13: inside a Visual Builder composition a usage names its section and element (as `get` lists them) and the
            property in it; a shared block placed in a composition has kind composition. --type of a block type adds a row
            (inline: true) with every inline block of it in the branches' current versions, sections and elements included.
            Example: opticli where-used 123 --pages
            """);
        content.AddTo(command, withLang: false);
        command.Options.Add(pages);
        command.Options.Add(type);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, new ResultModes()
            .Check(context =>
            {
                if ((context.Parse.GetValue(content.Ref) is null) == (context.Parse.GetValue(type) is null))
                {
                    throw new Core.Errors.UsageException("Give a ref, or --type for every instance of a type.");
                }
            })
            .When(ModeCondition.Set(type), async (context, cancellationToken) =>
            {
                await using var session = await context.OpenContentAsync(cancellationToken);
                if (context.Parse.GetValue(pages))
                {
                    throw new Core.Errors.UsageException("--pages works on one item, not with --type.");
                }
                var usageReader = new TypeUsageReader(session);
                var contentType = session.Model.RequireType(context.Parse.GetValue(type)!);
                var found = await usageReader.FindAsync(contentType.Id, cancellationToken);
                var instances = found.Where(i => i.Blueprint != true).ToList();
                var used = instances.Count(i => i.Count > 0);
                var blueprintCount = found.Count - instances.Count;
                var summary = $"{instances.Count} instance(s), {used} used, {instances.Count - used} unused{(blueprintCount > 0 ? $"; and {blueprintCount} blueprint(s) (blueprint: true), not counted" : "")}.";
                // CMS 13: inline blocks of the type (Visual Builder sections and elements, mostly) after the instances.
                if (await usageReader.InlineAsync(contentType.Id, cancellationToken) is { Count: > 0 } inline)
                {
                    var page = list.Apply(context.Parse, found.Cast<TypeUsageRow>().Append(inline).ToList());
                    return CommandResult.From(page) with
                    {
                        Warnings = [$"{summary} {inline.Count} inline use(s) in {inline.Usages.Select(u => u.Ref).Distinct().Count()} content item(s), in the row with inline: true."],
                    };
                }
                return CommandResult.From(list.Apply<TypeUsageRow>(context.Parse, found)) with
                {
                    Warnings = [summary],
                };
            })
            .When(ModeCondition.Set(pages), async (context, cancellationToken) =>
            {
                await using var session = await context.OpenContentAsync(cancellationToken);
                var located = await content.LocateAsync(context, session, cancellationToken);
                var target = await session.HeaderAsync(located.Id, cancellationToken);
                var reached = await new PageUsageReader(session).FindAsync(target, cancellationToken);
                return CommandResult.From(list.Apply(context.Parse, reached.Select(r => new PageUsageRow(r)).ToList()));
            })
            .Otherwise(async (context, cancellationToken) =>
            {
                await using var session = await context.OpenContentAsync(cancellationToken);
                var located = await content.LocateAsync(context, session, cancellationToken);
                var target = await session.HeaderAsync(located.Id, cancellationToken);
                var usages = await new WhereUsedReader(session).FindAsync(target, cancellationToken);
                return CommandResult.From(list.Apply(context.Parse, usages));
            }));
        return command;
    }
}
