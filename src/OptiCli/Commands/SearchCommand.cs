using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class SearchCommand
{
    public static Command Create(GlobalOptions options)
    {
        var text = new Argument<string>("text") { Description = "Text to look for (case-insensitive, anywhere in the value)." };
        var scope = new Option<string>("--in") { Description = "names, strings (text properties incl. rich text) or all.", DefaultValueFactory = _ => "all", HelpName = "names|strings|all" };
        scope.AcceptOnlyFromAmong("names", "strings", "all");
        var content = new ContentOptions();
        content.Lang.Description = "Only this language (code), with the shared values it shows from the master language. Default: all languages, shared values under the master language.";
        var list = new ListOptions(options);
        var blueprints = BlueprintsOption.Create();
        var command = new Command("search", $"""
            Search content names and text properties (incl. rich text) for a string, in every language (or --lang).
            One result per item and language, with a snippet per matching property. Deleted items and personal data (form
            submissions, users) are never searched. At most {SearchReader.MaxRows} matching values per source are read (meta.warnings says when).
            CMS 13: text inside a Visual Builder composition is found too: the match names its section and element (as `get`
            lists them) and the property in it. Blueprints are left out unless --blueprints.
            Example: opticli search "opening hours" --in strings --lang en
            """);
        command.Arguments.Add(text);
        command.Options.Add(scope);
        content.AddTo(command, withRef: false, withSite: false);
        list.AddTo(command);
        command.Options.Add(blueprints);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var needle = context.Parse.GetValue(text)!;
            if (needle.Trim().Length < 2)
            {
                throw new UsageException("Search text must be at least 2 characters.");
            }
            await using var session = await context.OpenContentAsync(cancellationToken);
            var (hits, capped) = await new SearchReader(session, context.Parse.GetValue(blueprints)).SearchAsync(
                needle, Enum.Parse<SearchScope>(context.Parse.GetValue(scope)!, ignoreCase: true), session.Language(context.Parse.GetValue(content.Lang)), cancellationToken);
            var page = list.Apply(context.Parse, hits);
            return new CommandResult(page.Items, page.Next, Warnings: capped
                ? [$"More than {SearchReader.MaxRows} values matched; results are incomplete. Use a more specific text, --in names, or --lang."]
                : null);
        });
        return command;
    }
}
