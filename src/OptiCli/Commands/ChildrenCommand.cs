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
        var blueprints = BlueprintsOption.Create();
        var command = new Command("children", """
            List the direct children of a content item, in the order the CMS lists them.
            Each child: ref, type, name, status, languages, URL, child count, and sortIndex when the parent sorts by it.
            CMS 13: Visual Builder content has a kind (experience, section, element); blueprints are left out unless --blueprints.
            Example: opticli children 123 --lang en
            """);
        content.AddTo(command);
        list.AddTo(command);
        command.Options.Add(blueprints);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            var located = await content.LocateAsync(context, session, cancellationToken);
            var language = content.Language(context, session, located);
            var reader = new TreeReader(session, context.Parse.GetValue(blueprints));
            var page = list.Apply(context.Parse, await reader.ChildIdsAsync(located.Id, language, cancellationToken));
            var headers = await reader.HeadersAsync(page.Items, cancellationToken);
            var leftOut = await reader.BlueprintsLeftOutAsync(located.Id, cancellationToken);
            return new CommandResult(await reader.NodesAsync(headers, language, cancellationToken, sortIndex: true), page.Next,
                Warnings: leftOut > 0 ? [$"{leftOut} blueprint(s) left out (Visual Builder templates, not content): --blueprints lists them."] : null);
        });
        return command;
    }
}
