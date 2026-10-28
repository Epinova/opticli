using System.Reflection;

namespace OptiCli.Mcp;

/// <summary>
/// The CMS major this build of the module is for, and the refusal of a site that runs another: OptiCli.Mcp is built
/// against CMS 12, OptiCli.Mcp.Cms13 against CMS 13 (<c>CMS13</c> defined), and NuGet doesn't always refuse the wrong
/// one (with some CMS 13 packages it only warns, and a site can force it). Checked before anything else is registered
/// (<see cref="OptiCliMcpExtensions.AddOptiCliMcp"/>).
/// </summary>
internal static class CmsMajor
{
#if CMS13
    /// <summary>The major this build is for.</summary>
    public const int Supported = 13;

    /// <summary>This build's package.</summary>
    public const string Package = "OptiCli.Mcp.Cms13";
#else
    /// <summary>The major this build is for.</summary>
    public const int Supported = 12;

    /// <summary>This build's package.</summary>
    public const string Package = "OptiCli.Mcp";
#endif

    /// <summary>
    /// The major of the CMS the site loaded, from its <c>EPiServer</c> assembly (the one with <c>IContent</c>), whose
    /// version is the package's (12.24.3.0 in EPiServer.CMS.Core 12.24.3, 13.3.0.0 in EPiServer 13.3.0). Loaded by name,
    /// not through <c>typeof(IContent)</c>: this build references the assembly at its own major's version, and on an
    /// older major (OptiCli.Mcp.Cms13 on CMS 12) that reference can't be loaded, so the refusal would never be reached.
    /// </summary>
    public static int Loaded => Assembly.Load(new AssemblyName("EPiServer")).GetName().Version?.Major ?? 0;

    /// <exception cref="InvalidOperationException">The site runs another CMS major than <see cref="Supported"/>.</exception>
    public static void Check(int major)
    {
        if (Refusal(major) is { } message)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Why this build can't run on CMS <paramref name="major"/>, naming the package that can; null if it can.</summary>
    public static string? Refusal(int major) =>
        major == Supported ? null
        : major is 12 or 13 ? $"This site runs CMS {major}: use the {(major == 13 ? "OptiCli.Mcp.Cms13" : "OptiCli.Mcp")} package instead of {Package}."
        : $"This site runs CMS {major}, which the opticli MCP module doesn't support: OptiCli.Mcp is for CMS 12, OptiCli.Mcp.Cms13 for CMS 13.";
}
