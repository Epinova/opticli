using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Refs;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class CreateCommand
{
    public static Command Create(GlobalOptions options)
    {
        var parent = new Argument<string>("parent-ref") { Description = $"Where to create it. {ContentRefParser.Syntax}" };
        var type = new Option<string>("--type") { Description = "Content type name or GUID (see `opticli types`).", Required = true, HelpName = "type" };
        var name = new Option<string>("--name") { Description = "Content name.", Required = true, HelpName = "name" };
        var lang = new Option<string?>("--lang") { Description = "Language of the new content. Default: the parent's master language.", HelpName = "code" };
        var guid = new Option<Guid?>("--guid") { Description = "The new content's GUID (default: a new one). Fails with a conflict if it exists.", HelpName = "guid" };
        var write = new WriteOptions();
        var command = new Command("create", """
            Create a page, block or folder under a parent, as a draft unless --publish. Needs `opticli serve`.
            The type must be allowed below the parent's type; required properties must be set before it can be published (a draft
            may leave them empty). Prints the new content's ref, version and every property set.
            Example: opticli create 123 --type ArticlePage --name "News" Heading="Hello" --dry-run
            """);
        command.Arguments.Add(parent);
        command.Options.Add(type);
        command.Options.Add(name);
        command.Options.Add(lang);
        command.Options.Add(guid);
        write.AddProperties(command);
        write.AddCommon(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new CreateOperation(parse.GetValue(parent)!, parse.GetValue(type)!, parse.GetValue(name)!, write.ParseProperties(context), parse.GetValue(lang), parse.GetValue(write.Publish))
            {
                ContentGuid = parse.GetValue(guid),
            };
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
