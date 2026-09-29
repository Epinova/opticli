using System.Reflection;

namespace OptiCli.Agent.Http;

internal static class AgentInfo
{
    /// <summary>The agent's version without the source-revision suffix.</summary>
    public static readonly string Version =
        typeof(AgentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
