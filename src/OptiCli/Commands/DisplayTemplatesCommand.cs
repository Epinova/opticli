using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class DisplayTemplatesCommand
{
    public static Command Create(GlobalOptions options)
    {
        var type = new Option<string?>("--type")
        {
            Description = "Only the templates content of this type (an experience, section or element type) can use in a composition.",
            HelpName = "type",
        };
        var list = new ListOptions(options);
        var command = new Command("display-templates", """
            CMS 13: list Visual Builder display templates, the styles a composition's experience, sections, rows, columns and
            elements can use (get's displayTemplate and displaySettings): key, name, what it is for (nodeType, baseType,
            contentType; absent: any), isDefault, and its settings (key, name, editor select or checkbox, and a select's
            choices). They are data the site's editors or code saved, not code. CMS 12 has none (an empty list).
            Example: opticli display-templates --type VbTextElement
            """);
        command.Options.Add(type);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            IEnumerable<DisplayTemplateInfo> templates = await DisplayTemplateReader.ListAsync(session.Db, session.Model, cancellationToken);
            if (context.Parse.GetValue(type) is { } name)
            {
                var contentType = session.Model.RequireType(name);
                var nodeType = DisplayTemplateInfo.NodeTypeOf(contentType)
                    ?? throw new UsageException(
                        $"{contentType.Name} is a {contentType.Kind.ToString().ToLowerInvariant()} type: only experiences, sections and blocks with a composition behaviour (elements) sit in a composition.",
                        "Rows and columns use templates too: run it without --type to see them all.");
                templates = templates.Where(t => t.AppliesTo(contentType, nodeType));
            }
            return CommandResult.From(list.Apply(context.Parse, templates.ToList()));
        });
        return command;
    }
}
