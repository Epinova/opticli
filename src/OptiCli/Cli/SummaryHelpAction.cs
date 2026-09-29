using System.CommandLine;
using System.CommandLine.Invocation;

namespace OptiCli.Cli;

/// <summary>
/// Help where a command's list of subcommands shows only the first line of each description (its summary),
/// while the command's own help keeps its full description. Keeps <c>opticli --help</c> to one screen.
/// </summary>
internal sealed class SummaryHelpAction(SynchronousCommandLineAction inner) : SynchronousCommandLineAction
{
    public override bool ClearsParseErrors => true;

    public static void Install(RootCommand root)
    {
        var help = root.Options.OfType<System.CommandLine.Help.HelpOption>().Single();
        help.Action = new SummaryHelpAction((SynchronousCommandLineAction)help.Action!);
    }

    public override int Invoke(ParseResult parseResult)
    {
        // The process only ever shows one help text, so shortening the listed commands in place is safe.
        foreach (var subcommand in parseResult.CommandResult.Command.Subcommands)
        {
            subcommand.Description = subcommand.Description?.Split('\n', 2)[0].TrimEnd();
        }
        return inner.Invoke(parseResult);
    }
}
