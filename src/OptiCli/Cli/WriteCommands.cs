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
    /// <param name="When">Null: the command always writes. Else it writes when this holds, or with <paramref name="Unless"/> when it doesn't.</param>
    private sealed record WriteMark(ModeCondition? When, bool Unless);

    private static readonly ConditionalWeakTable<Command, WriteMark> Marked = new();

    /// <summary>A command that always writes.</summary>
    public static void Mark(Command command) => Marked.AddOrUpdate(command, new WriteMark(null, false));

    /// <summary>
    /// A command that writes only when <paramref name="when"/> holds (<c>access</c> with a change option): one of its
    /// <see cref="ResultModes"/>' conditions, so the schema can say which of its modes write.
    /// </summary>
    public static void MarkWhen(Command command, ModeCondition when) => Marked.AddOrUpdate(command, new WriteMark(when, false));

    /// <summary>A command that writes unless <paramref name="when"/> holds (<c>apply</c>, but not <c>apply --seal</c>): one of its modes' conditions.</summary>
    public static void MarkUnless(Command command, ModeCondition when) => Marked.AddOrUpdate(command, new WriteMark(when, true));

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
        Marked.TryGetValue(parse.CommandResult.Command, out var mark) && (mark.When is null || mark.When.Matches(parse) != mark.Unless);

    /// <summary>
    /// For <c>opticli schema</c>: whether the command writes in each of its modes (<paramref name="modes"/>' conditions in
    /// the order they are tried, the last null for <see cref="ResultModes.Otherwise{T}"/>). A command marked
    /// <see cref="MarkWhen"/> or <see cref="MarkUnless"/> writes in that mode (or in every other one); a mode tried before
    /// it could hold together with it, so its condition must come first.
    /// </summary>
    /// <exception cref="InvalidOperationException">The write's condition is none of the modes', or not the first.</exception>
    public static IReadOnlyList<bool> WritesPerMode(Command command, IReadOnlyList<ModeCondition?> modes)
    {
        if (!Marked.TryGetValue(command, out var mark) || LocalWrite(command) is not null)
        {
            return [.. modes.Select(_ => LocalWrite(command) is not null)];
        }
        if (mark.When is null)
        {
            return [.. modes.Select(_ => true)];
        }
        var index = modes.ToList().FindIndex(m => ReferenceEquals(m, mark.When));
        if (index != 0)
        {
            throw new InvalidOperationException($"'{command.Name}' writes on a condition that isn't its first mode's: the schema can't say which of its modes write.");
        }
        return [.. modes.Select((_, i) => i == index ? !mark.Unless : mark.Unless)];
    }
}
