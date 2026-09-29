using System.Reflection;

namespace OptiCli.Cli;

internal static class ToolInfo
{
    /// <summary>The package version, without the source-revision suffix the SDK appends.</summary>
    public static string Version { get; } =
        (typeof(ToolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}
