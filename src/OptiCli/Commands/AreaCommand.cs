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
            Description = "add <block-ref> | remove <index|ref:id> | move <from-index|ref:id> <to-index>. Positions are zero-based; a plain number is a position, ref:123 (or a GUID) names the item by the content it shows.",
            Arity = new ArgumentArity(1, 2),
        };
        var at = new Option<int?>("--at") { Description = "add: insert at this zero-based position. Default: the end.", HelpName = "n" };
        var display = new Option<string?>("--display") { Description = "add: display option (an id like 'wide', as the site registers them; an unknown one is refused).", HelpName = "option" };
        var write = new WriteOptions();
        var command = new Command("area", """
            Add, remove or reorder ContentArea items on a new version, a draft unless --publish. Needs `opticli serve`.
            Positions are zero-based; remove and move take a position (2) or the item's content (ref:456). Prints the new version
            and the area's items before/after. Read the current items with `opticli get <ref> --fields <Prop>`.
            Example: opticli area 123 MainArea add 456 --at 0 --display wide --dry-run
            Also:    opticli area 123 MainArea remove ref:456    opticli area 123 MainArea move 0 3
            """);
        content.AddTo(command);
        command.Arguments.Add(property);
        command.Arguments.Add(action);
        command.Arguments.Add(items);
        command.Options.Add(at);
        command.Options.Add(display);
        write.AddCommon(command);
        write.AddIncludeDraft(command);
        write.AddConcurrency(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var verb = parse.GetValue(action)!;
            var values = parse.GetValue(items) ?? [];
            var expected = verb == "move" ? 2 : 1;
            if (values.Length != expected)
            {
                throw new UsageException($"area {verb} takes {expected} value(s), got {values.Length}.", "Usage: area <ref> <Prop> add <block-ref> | remove <index|ref:id> | move <from> <to>.");
            }
            if (verb != "add" && (parse.GetValue(at) is not null || parse.GetValue(display) is not null))
            {
                throw new UsageException("--at and --display only apply to add.", verb == "move" ? "The target position is move's second value." : null);
            }

            var (index, item) = verb == "add" ? (null, values[0]) : AreaItemArgument.Parse(values[0]);
            var operation = new AreaEdit(
                parse.GetValue(content.Ref)!,
                parse.GetValue(property)!,
                verb,
                item,
                index,
                parse.GetValue(at),
                verb == "move" ? AreaItemArgument.Position(values[1]) : null,
                parse.GetValue(display),
                parse.GetValue(content.Lang),
                parse.GetValue(write.Publish),
                write.ParseBaseVersion(context),
                parse.GetValue(write.Force))
            {
                IncludeDraft = parse.GetValue(write.IncludeDraft),
            };
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(operation, parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
