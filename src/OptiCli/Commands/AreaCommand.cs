using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class AreaCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var property = new Argument<string>("property") { Description = "The ContentArea property (see `opticli type <type>`), or a path to one inside a block: 'MainArea[0].Area' (the inline block at position 0), Hero.Area (a local block)." };
        var action = new Argument<string>("action") { Description = "add, remove, move or set." };
        action.AcceptOnlyFromAmong("add", "remove", "move", "set");
        var items = new Argument<string[]>("items")
        {
            Description = "add <block-ref> | add --type T [Prop=value ...] | remove <index|ref:id> | move <from-index|ref:id> <to-index> | set <index> [Prop=value ...]. Positions are zero-based; a plain number is a position, ref:123 (or a GUID) names the item by the content it shows.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var at = new Option<int?>("--at") { Description = "add: insert at this zero-based position. Default: the end.", HelpName = "n" };
        var display = new Option<string?>("--display") { Description = "add: display option (an id like 'wide', as the site registers them; an unknown one is refused).", HelpName = "option" };
        var type = new Option<string?>("--type") { Description = "add: a new block of this block type, with Prop=value arguments as its values: an inline block stored in the area (CMS 12.20+), or, where the site's edit UI makes those (`opticli serve --status`: inlineBlocks shared), a shared block in the content's \"For this page\" folder (sharedBlocks in the result gives its ref).", HelpName = "type" };
        var values = new Option<string?>("--values") { Description = "add --type: the new block's values; set: the inline block's. A JSON object, merged over the Prop=value arguments, as for `opticli set`.", HelpName = "json" };
        var name = new Option<string?>("--name") { Description = "add --type: the new block's name: an inline block's name in the area (the edit UI shows it; default: none, shown as its type's name), or a shared block's (default: \"<content name> <type> <n>\", as the edit UI names one). set: the inline block's name; set --name \"\" removes it.", HelpName = "name" };
        var write = new WriteOptions();
        var command = new Command("area", """
            Add, remove, reorder or change ContentArea items on a new version, a draft unless --publish. Needs `opticli serve`.
            add takes a shared block (its ref), or --type T for a new inline block (CMS 12.20+) stored in the area itself, with
            its values as Prop=value (and --values); where the site's edit UI makes a new block a shared one in the content's
            "For this page" folder instead, so does --type (sharedBlocks in the result gives its ref; change it by that ref).
            Positions are zero-based; remove and move take a position (2) or the item's content (ref:456); an inline block
            has no ref, so only its position names it. set <position> changes an
            inline block's values (Prop=value, --values) and --name, the rest of it staying as it is (as
            `opticli set <ref> 'MainArea[2].Heading=New'` does). Prints the new version and the area's items before/after.
            Read the positions in the version you change: `opticli get <ref> --version latest --fields <Prop>`. An area inside an
            inline block is named by its path: opticli area 123 'MainArea[0].Area' remove 1.
            --from published edits the published version's area instead of the latest draft's (which is left out).
            Example: opticli area 123 MainArea add 456 --at 0 --display wide --dry-run
            Also:    opticli area 123 MainArea add --type TeaserBlock Heading=Hi --at 0 --dry-run
                     opticli area 123 MainArea set 2 Heading=New --name Intro
                     opticli area 123 MainArea remove ref:456    opticli area 123 MainArea move 0 3
            """);
        content.AddTo(command);
        command.Arguments.Add(property);
        command.Arguments.Add(action);
        command.Arguments.Add(items);
        command.Options.Add(at);
        command.Options.Add(display);
        command.Options.Add(type);
        command.Options.Add(values);
        command.Options.Add(name);
        write.AddCommon(command);
        write.AddProject(command);
        write.AddPublishAt(command);
        write.AddIncludeDraft(command);
        write.AddConcurrency(command);

        CommandRunner.SetHandler<WriteOutput>(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var verb = parse.GetValue(action)!;
            var given = parse.GetValue(items) ?? [];
            var inlineType = parse.GetValue(type);
            const string usage = "Usage: area <ref> <Prop> add <block-ref> | add --type T [Prop=value ...] | remove <index|ref:id> | move <from> <to> | set <index> [Prop=value ...] [--name N].";
            if (inlineType is not null && verb != "add")
            {
                throw new UsageException("--type only applies to add (a new block).", "remove, move and set name an inline block by its position.");
            }
            if (inlineType is null && verb != "set" && (parse.GetValue(values) is not null || parse.GetValue(name) is not null))
            {
                throw new UsageException("--values and --name are a new block's: give --type too.", usage);
            }
            var expected = verb == "move" ? 2 : 1;
            if (inlineType is null && verb != "set" && given.Length != expected)
            {
                throw new UsageException($"area {verb} takes {expected} value(s), got {given.Length}.", usage);
            }
            if (verb != "add" && (parse.GetValue(at) is not null || parse.GetValue(display) is not null))
            {
                throw new UsageException("--at and --display only apply to add.", verb == "move" ? "The target position is move's second value." : null);
            }
            if (inlineType is not null && given.FirstOrDefault(g => !g.Contains('=')) is { } reference)
            {
                throw new UsageException($"area add takes a block ref or --type (a new block), not both ('{reference}').", "After --type T, give the new block's values as Prop=value.");
            }
            if (verb == "set" && (given.Length == 0 || given[0].Contains('=') || given.Skip(1).Any(g => !g.Contains('='))))
            {
                throw new UsageException("area set takes the inline block's position, then its values as Prop=value.", usage);
            }

            var (index, item) = verb switch
            {
                "add" => (null, inlineType is null ? given[0] : null),
                "set" => (AreaItemArgument.Position(given[0]), null),
                _ => AreaItemArgument.Parse(given[0]),
            };
            var assignments = verb == "set" ? given[1..] : given;
            var inlineValues = inlineType is null && verb != "set" ? null : PropertyArguments.Parse(assignments, parse.GetValue(values), context.Environment.CurrentDirectory);
            var operation = new AreaEdit(
                parse.GetValue(content.Ref)!,
                parse.GetValue(property)!,
                verb,
                item,
                index,
                parse.GetValue(at),
                verb == "move" ? AreaItemArgument.Position(given[1]) : null,
                parse.GetValue(display),
                parse.GetValue(content.Lang),
                parse.GetValue(write.Publish),
                write.ParseBaseVersion(context),
                parse.GetValue(write.Force))
            {
                IncludeDraft = parse.GetValue(write.IncludeDraft),
                From = write.ParseFrom(context),
                Type = inlineType,
                Values = inlineValues is { Count: > 0 } ? inlineValues : null,
                Name = parse.GetValue(name),
            };
            // Before the database is opened: --in-project and --ready are checked with the other options.
            var applied = write.WithApproval(operation, parse);
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(applied, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
