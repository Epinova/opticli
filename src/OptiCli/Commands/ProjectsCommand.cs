using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class ProjectsCommand
{
    public static Command Create(GlobalOptions options)
    {
        var id = new Argument<int?>("id") { Description = "A project's id: list its items instead.", Arity = ArgumentArity.ZeroOrOne };
        var list = new ListOptions(options);
        var command = new Command("projects", """
            List projects (edit mode's Projects: versions of several items published together): id, name, status, who
            created it and when, publishAt when scheduled, and how many items it has. With an id, the project's items:
            the content, the version in the project, its status and language. Read-only.
            Example: opticli projects 3
            """);
        command.Arguments.Add(id);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            if (context.Parse.GetValue(id) is not { } projectId)
            {
                return CommandResult.From(list.Apply(context.Parse, await ProjectReader.ListAsync(session.Db, cancellationToken)));
            }
            var projects = await ProjectReader.ListAsync(session.Db, cancellationToken);
            if (projects.All(p => p.Id != projectId))
            {
                throw new NotFoundException($"No project {projectId}.", projects.Count == 0 ? "The site has no projects." : "List them with `opticli projects`.");
            }
            return CommandResult.From(list.Apply(context.Parse, await ProjectReader.ItemsAsync(session, projectId, cancellationToken)));
        });
        return command;
    }
}
