using System.CommandLine;
using System.Text.Json.Nodes;
using OptiCli.Core.Output;

namespace OptiCli.Cli;

/// <summary>
/// The results of a command whose <c>data</c> depends on its options (<c>serve --status</c> is an <c>AgentStatus</c>,
/// <c>serve --logs</c> a <c>ServeLogs</c>): one handler per mode, each with its own declared type, and the condition that
/// picks it. The runner runs the first mode whose condition holds, else <see cref="Otherwise{T}"/>; the schema command
/// describes the same conditions, so what it reports and what runs can't drift apart.
/// </summary>
/// <remarks>
/// The compiler checks that each handler returns its declared type. It can't check that a condition says what the
/// handler's own code means by the mode (only that both use the same options); the schema tests and phase 4's validation
/// of real output against the schema cover that.
/// </remarks>
internal sealed class ResultModes
{
    private readonly List<Mode> _modes = [];
    private Action<CliContext>? _check;

    /// <param name="When">Null for the mode that runs when no other one's condition holds.</param>
    internal sealed record Mode(ModeCondition? When, Type Result, Func<CliContext, CancellationToken, Task<CommandResult>> Run);

    /// <summary>In order; the last is <see cref="Otherwise{T}"/>'s.</summary>
    public IReadOnlyList<Mode> Modes => _modes;

    /// <summary>Checks that hold for every mode (options that don't go together), run before one is picked.</summary>
    public ResultModes Check(Action<CliContext> check)
    {
        _check = check;
        return this;
    }

    public ResultModes When<T>(ModeCondition when, Func<CliContext, CancellationToken, Task<CommandResult<T>>> run) where T : notnull
    {
        if (_modes.LastOrDefault() is { When: null })
        {
            throw new InvalidOperationException("Otherwise comes last.");
        }
        _modes.Add(new Mode(when, typeof(T), async (context, cancellationToken) => await run(context, cancellationToken)));
        return this;
    }

    public ResultModes Otherwise<T>(Func<CliContext, CancellationToken, Task<CommandResult<T>>> run) where T : notnull
    {
        if (_modes.LastOrDefault() is { When: null })
        {
            throw new InvalidOperationException("A command has one Otherwise.");
        }
        _modes.Add(new Mode(null, typeof(T), async (context, cancellationToken) => await run(context, cancellationToken)));
        return this;
    }

    /// <summary>The mode the invocation picks: the first whose condition holds.</summary>
    public Mode Pick(ParseResult parse) =>
        _modes.FirstOrDefault(m => m.When is null || m.When.Matches(parse))
        ?? throw new InvalidOperationException("No mode applies, and there is no Otherwise.");

    public Task<CommandResult> RunAsync(CliContext context, CancellationToken cancellationToken)
    {
        _check?.Invoke(context);
        return Pick(context.Parse).Run(context, cancellationToken);
    }
}

/// <summary>
/// When a <see cref="ResultModes"/> mode applies, made only from the options (or argument) it reads, so the schema's
/// description (<see cref="Describe"/>) and the check at run time (<see cref="Matches"/>) are the same thing.
/// </summary>
internal sealed class ModeCondition
{
    private readonly Func<ParseResult, bool> _matches;
    private readonly Func<JsonObject> _describe;

    private ModeCondition(Func<ParseResult, bool> matches, Func<JsonObject> describe)
    {
        _matches = matches;
        _describe = describe;
    }

    public bool Matches(ParseResult parse) => _matches(parse);

    /// <summary>
    /// <c>{"option": "--status", "values": ["draft"]}</c> (the option has one of these values), <c>{"option": "--grant"}</c>
    /// (it is given: true, a value, at least one value), <c>{"argument": "project"}</c> (the argument is given), or
    /// <c>{"anyOf": [...]}</c>.
    /// </summary>
    public JsonObject Describe() => _describe();

    /// <summary>The option has one of these values (for a flag: <c>Is(stop, true)</c>).</summary>
    public static ModeCondition Is<TValue>(Option<TValue> option, params TValue[] values) =>
        new(parse => values.Contains(parse.GetValue(option)), () => new JsonObject
        {
            ["option"] = option.Name,
            ["values"] = new JsonArray([.. values.Select(v => JsonOutput.ToNode(v))]),
        });

    /// <summary>The flag is given (true).</summary>
    public static ModeCondition Set(Option<bool> option) =>
        new(parse => parse.GetValue(option), () => new JsonObject { ["option"] = option.Name });

    /// <summary>The option has a value.</summary>
    public static ModeCondition Set(Option<string?> option) =>
        new(parse => parse.GetValue(option) is not null, () => new JsonObject { ["option"] = option.Name });

    /// <summary>The option is given at least once.</summary>
    public static ModeCondition Set(Option<string[]> option) =>
        new(parse => parse.GetValue(option) is { Length: > 0 }, () => new JsonObject { ["option"] = option.Name });

    /// <summary>The argument is given.</summary>
    public static ModeCondition Set<TValue>(Argument<TValue> argument) =>
        new(parse => parse.GetValue(argument) is not null, () => new JsonObject { ["argument"] = argument.Name });

    public static ModeCondition AnyOf(params ModeCondition[] conditions) =>
        new(parse => conditions.Any(c => c.Matches(parse)), () => new JsonObject { ["anyOf"] = new JsonArray([.. conditions.Select(c => (JsonNode)c.Describe())]) });
}
