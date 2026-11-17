using System.CommandLine;
using System.Runtime.CompilerServices;

namespace OptiCli.Cli;

/// <summary>
/// The commands that write (through the running site), so <see cref="CommandRunner"/> refuses a production database
/// before the command reads anything, asks anything or looks for the site. The write path itself refuses it again
/// (<see cref="CliContext.Writes"/>, and the agent connection in Core), so a command missing here is still
/// refused, only later.
/// </summary>
internal static class WriteCommands
{
    private static readonly ConditionalWeakTable<Command, Func<ParseResult, bool>> Marked = new();

    /// <param name="writes">For a command that only writes with some options (<c>access</c> shows the rights without them); null: always.</param>
    public static void Mark(Command command, Func<ParseResult, bool>? writes = null) =>
        Marked.AddOrUpdate(command, writes ?? (_ => true));

    public static bool IsMarked(Command command) => Marked.TryGetValue(command, out _);

    private static readonly ConditionalWeakTable<Command, string> LocalWrites = new();

    /// <summary>
    /// A command that changes nothing in the CMS, but writes state another command relies on (<c>env</c> registers a new
    /// serve token): not refused as a write, but <c>opticli schema</c> reports it as one, with <paramref name="note"/>.
    /// </summary>
    public static void MarkLocal(Command command, string note) => LocalWrites.AddOrUpdate(command, note);

    /// <summary>What <see cref="MarkLocal"/> said the command writes; null for any other command.</summary>
    public static string? LocalWrite(Command command) => LocalWrites.TryGetValue(command, out var note) ? note : null;

    /// <summary>True when this invocation writes.</summary>
    public static bool Writes(ParseResult parse) =>
        Marked.TryGetValue(parse.CommandResult.Command, out var writes) && writes(parse);
}
