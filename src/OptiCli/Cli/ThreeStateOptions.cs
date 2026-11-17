using System.CommandLine;
using System.Runtime.CompilerServices;

namespace OptiCli.Cli;

/// <summary>
/// Flags whose absence means something other than false: <c>serve --https</c> and <c>--scheduler</c> take the user
/// config's setting unless given, so <c>--scheduler false</c> is a third state. Their handlers read them with
/// <c>ParseResult.GetResult(option) is { Implicit: false }</c>; marking them here lets <c>opticli schema</c> say so
/// (<c>explicitFalse</c>), so a generated method passes <c>--scheduler false</c> rather than leaving the flag out.
/// </summary>
internal static class ThreeStateOptions
{
    private static readonly ConditionalWeakTable<Option, object> Marked = new();

    public static Option<bool> ThreeState(this Option<bool> option)
    {
        Marked.AddOrUpdate(option, option);
        return option;
    }

    /// <summary>A marked flag, or an <c>Option&lt;bool?&gt;</c>.</summary>
    public static bool IsThreeState(Option option) => Marked.TryGetValue(option, out _) || option.ValueType == typeof(bool?);
}
