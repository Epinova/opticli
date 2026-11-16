using System.Reflection;
using EPiServer.DataAbstraction;
using OptiCli.Protocol;

namespace OptiCli.Cms.Compat;

/// <summary>
/// Which project UI the site's CMS edit UI gives editors: project mode (the project bar), the Projects gadget, or neither.
/// Read from the edit UI's own options (<c>ProjectUIOptions</c>, bound from <c>EPiServer:CmsUI:ProjectUI</c>), by
/// reflection: they are in <c>EPiServer.Cms.Shell.UI</c> (package <c>EPiServer.CMS.UI.Core</c> on CMS 12,
/// <c>EPiServer.CMS.Shell.UI</c> on 13), which neither the agent nor <c>OptiCli.Cms</c> references.
/// </summary>
/// <remarks>
/// <para><c>ProjectModeEnabled</c> is a nullable bool. On CMS 12 it is unset by default, and the edit UI then reads the
/// site configuration value <c>ProjectModeEnabled</c> (<c>ISiteConfigurationRepository</c>), else turns project mode on.
/// CMS 13 defaults it to false and has no site configuration fallback (that repository is obsolete as an error, and not
/// registered). <c>ProjectGadgetEnabled</c> (default on) gives editors the Projects gadget where project mode is off.</para>
/// <para>A site without the edit UI's assembly has no editors to hide projects from: <see cref="Of"/> is null there, and
/// projects are allowed.</para>
/// </remarks>
internal static class CmsProjectUi
{
    private const string OptionsType = "EPiServer.Cms.Shell.UI.Rest.Projects.ProjectUIOptions, EPiServer.Cms.Shell.UI";

    /// <summary>Neither project mode nor the gadget: editors have no projects.</summary>
    public const string None = "none";

    /// <summary>
    /// <see cref="ProjectEditorUi.ProjectMode"/>, <see cref="ProjectEditorUi.Gadget"/> or <see cref="None"/>; null when it
    /// can't be told (no CMS edit UI, or options of another shape).
    /// </summary>
    public static string? Of(IServiceProvider services)
    {
        try
        {
            var type = Type.GetType(OptionsType, throwOnError: false);
            var options = type is null ? null : services.GetService(type);
            if (options is null)
            {
                return null;
            }
            var projectMode = type!.GetProperty("ProjectModeEnabled", BindingFlags.Public | BindingFlags.Instance)?.GetValue(options) as bool?;
            var gadget = type.GetProperty("ProjectGadgetEnabled", BindingFlags.Public | BindingFlags.Instance)?.GetValue(options) as bool? ?? true;
            return Decide(projectMode ?? SiteConfiguration(services), gadget);
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or TypeLoadException or FileLoadException)
        {
            return null;
        }
    }

    /// <param name="projectMode">The project mode setting as the edit UI resolves it; null: unset, which is on.</param>
    /// <param name="gadget">The Projects gadget setting.</param>
    internal static string Decide(bool? projectMode, bool gadget) =>
        projectMode != false ? ProjectEditorUi.ProjectMode : gadget ? ProjectEditorUi.Gadget : None;

    /// <summary>CMS 12's fallback for an unset <c>ProjectModeEnabled</c>: the site configuration value; null when it has none.</summary>
    private static bool? SiteConfiguration(IServiceProvider services)
    {
#if CMS13
        _ = services;
        return false;
#else
        return services.GetService(typeof(ISiteConfigurationRepository)) is ISiteConfigurationRepository configuration
            && bool.TryParse(configuration.GetValue(null, "ProjectModeEnabled"), out var enabled)
            ? enabled
            : null;
#endif
    }
}
