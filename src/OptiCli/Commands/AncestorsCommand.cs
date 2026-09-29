using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Content;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class AncestorsCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var command = new Command("ancestors", """
            List the path from the root down to a content item's parent (root first).
            Example: opticli ancestors /en/about/team/
            """);
        content.AddTo(command);
        options.AddJsonLines(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            var located = await content.LocateAsync(context, session, cancellationToken);
            var language = content.Language(context, session, located);
            var header = await session.HeaderAsync(located.Id, cancellationToken);
            await session.Identities.LoadAsync(header.AncestorIds, [], cancellationToken);
            var ancestors = header.AncestorIds.Select(session.Identities.Header).OfType<ContentHeader>().ToList();
            return new CommandResult(await new TreeReader(session).NodesAsync(ancestors, language, cancellationToken));
        });
        return command;
    }
}
