using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Content;
using OptiCli.Core.Output;

namespace OptiCli.Commands;

internal static class VersionsCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        content.Lang.Description = "Only versions in this language (code). Default: all languages.";
        var list = new ListOptions(options);
        var command = new Command("versions", """
            List a content item's versions, newest first, in all languages unless --lang.
            Each version: version ref (pass it to get), language, status, name, saved (UTC), saved by, publish date;
            primary: true marks each branch's primary (published) version.
            Example: opticli versions 123 --lang en --limit 10
            """);
        content.AddTo(command);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            var located = await content.LocateAsync(context, session, cancellationToken);
            var language = session.Language(context.Parse.GetValue(content.Lang));
            var (offset, limit) = list.Window(context.Parse);
            var versions = await VersionReader.ListAsync(session.Db, session.Model, located.Id, language?.Id, offset, limit, cancellationToken);
            return CommandResult.From(Paging.FromWindow(versions, offset, limit));
        });
        return command;
    }
}
