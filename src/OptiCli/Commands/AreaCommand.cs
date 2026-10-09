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
        var property = new Argument<string>("property") { Description = "The ContentArea property (see `opticli type <type>`)." };
        var action = new Argument<string>("action") { Description = "add, remove or move." };
        action.AcceptOnlyFromAmong("add", "remove", "move");
        var items = new Argument<string[]>("items")
        {
            Description = "add <block-ref> | add --type T [Prop=value ...] | remove <index|ref:id> | move <from-index|ref:id> <to-index>. Positions are zero-based; a plain number is a position, ref:123 (or a GUID) names the item by the content it shows.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var at = new Option<int?>("--at") { Description = "add: insert at this zero-based position. Default: the end.", HelpName = "n" };
        var display = new Option<string?>("--display") { Description = "add: display option (an id like 'wide', as the site registers them; an unknown one is refused).", HelpName = "option" };
        var type = new Option<string?>("--type") { Description = "add: a new inline block of this block type (CMS 12.20+), stored in the area, with Prop=value arguments as its values.", HelpName = "type" };
        var values = new Option<string?>("--values") { Description = "add --type: the inline block's values as a JSON object, merged over the Prop=value arguments, as for `opticli set`.", HelpName = "json" };
        var name = new Option<string?>("--name") { Description = "add --type: the inline block's name in the area (the edit UI shows it; default: none, shown as its type's name).", HelpName = "name" };
        var write = new WriteOptions();
        var command = new Command("area", """
            Add, remove or reorder ContentArea items on a new version, a draft unless --publish. Needs `opticli serve`.
            add takes a shared block (its ref), or --type T for a new inline block (CMS 12.20+) stored in the area itself, with
            its values as Prop=value (and --values). Positions are zero-based; remove and move take a position (2) or the
            item's content (ref:456); an inline block has no ref, so only its position names it. Prints the new version and
            the area's items before/after. Read the current items with `opticli get <ref> --fields <Prop>`; change an inline
            block's values with `opticli set <ref> 'MainArea[2].Heading=New'`.
            --from published edits the published version's area instead of the latest draft's (which is left out).
            Example: opticli area 123 MainArea add 456 --at 0 --display wide --dry-run
            Also:    opticli area 123 MainArea add --type TeaserBlock Heading=Hi --at 0 --dry-run
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
        write.AddPublishAt(command);
        write.AddIncludeDraft(command);
        write.AddConcurrency(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var verb = parse.GetValue(action)!;
            var given = parse.GetValue(items) ?? [];
            var inlineType = parse.GetValue(type);
            const string usage = "Usage: area <ref> <Prop> add <block-ref> | add --type T [Prop=value ...] | remove <index|ref:id> | move <from> <to>.";
            if (inlineType is not null && verb != "add")
            {
                throw new UsageException("--type only applies to add (a new inline block).", "remove and move name an inline block by its position.");
            }
            if (inlineType is null && (parse.GetValue(values) is not null || parse.GetValue(name) is not null))
            {
                throw new UsageException("--values and --name are a new inline block's: give --type too.", usage);
            }
            var expected = verb == "move" ? 2 : 1;
            if (inlineType is null && given.Length != expected)
            {
                throw new UsageException($"area {verb} takes {expected} value(s), got {given.Length}.", usage);
            }
            if (verb != "add" && (parse.GetValue(at) is not null || parse.GetValue(display) is not null))
            {
                throw new UsageException("--at and --display only apply to add.", verb == "move" ? "The target position is move's second value." : null);
            }
            if (inlineType is not null && given.FirstOrDefault(g => !g.Contains('=')) is { } reference)
            {
                throw new UsageException($"area add takes a block ref or --type (a new inline block), not both ('{reference}').", "After --type T, give the inline block's values as Prop=value.");
            }

            var (index, item) = verb == "add" ? (null, inlineType is null ? given[0] : null) : AreaItemArgument.Parse(given[0]);
            var inlineValues = inlineType is null ? null : PropertyArguments.Parse(given, parse.GetValue(values), context.Environment.CurrentDirectory);
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
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(write.WithApproval(operation, parse), parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
