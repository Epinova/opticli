using System.CommandLine;

namespace OptiCli.Cli;

/// <summary><c>--blueprints</c>, shared by the content listings that leave CMS 13's Visual Builder blueprints out.</summary>
internal static class BlueprintsOption
{
    public static Option<bool> Create() => new("--blueprints")
    {
        Description = "CMS 13: also list Visual Builder blueprints (templates new content is made from, not content visitors see), which are left out by default.",
    };
}
