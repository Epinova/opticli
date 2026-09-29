using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class ChildrenCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var list = new ListOptions(options);
        var command = new Command("children", """
            List the direct children of a content item, in the order the CMS lists them.
            Each child: ref, type, name, status, languages, URL and child count.
            Example: opticli children 123 --lang en
            """);
        content.AddTo(command);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            var located = await content.LocateAsync(context, session, cancellationToken);
            var language = content.Language(context, session, located);
            var reader = new TreeReader(session);
            var page = list.Apply(context.Parse, await reader.ChildrenAsync(located.Id, language, cancellationToken));
            return new CommandResult(await reader.NodesAsync(page.Items, language, cancellationToken), page.Next);
        });
        return command;
    }
}
