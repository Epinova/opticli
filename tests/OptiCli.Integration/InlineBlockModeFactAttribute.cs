using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// A <see cref="SiteFactAttribute"/> for one of the two ways a site makes a new block in a ContentArea, which opticli's
/// writes follow: <see cref="InlineBlockModes.Inline"/> (the edit UI's <c>UIOptions.InlineBlocksInContentAreaEnabled</c>
/// on, CMS 13's default) or <see cref="InlineBlockModes.Shared"/> (off, CMS 12's default: a shared block in the content's
/// "For this page" folder). Reported as skipped, saying how to restart the site in that mode, on a site running in the
/// other; the tests never restart the site themselves. The mode is the running agent's (<c>serve --status</c>'s
/// <c>inlineBlocks</c>), asked once per test run; a site that doesn't answer runs the test, which then fails as the
/// other site tests do.
/// </summary>
public sealed class InlineBlockModeFactAttribute : FactAttribute
{
    public InlineBlockModeFactAttribute(string mode)
    {
        if (SiteSettings.ProjectDirectory is not { } project)
        {
            Skip = $"Needs a site: set {SiteSettings.ProjectVariable} to a CMS site project directory and start its agent with `opticli serve` there.";
        }
        else if (RunningMode.Value is { } running && running != mode)
        {
            Skip = $"Needs the site serving new ContentArea blocks {mode} (it serves them {running}): restart it with `opticli serve --stop --project {project}` and `EPiServer__CmsUI__UI__InlineBlocksInContentAreaEnabled={(mode == InlineBlockModes.Inline ? "true" : "false")} opticli serve --project {project}`.";
        }
    }

    /// <summary>The running site's mode; null when it can't be asked (no site running, or an agent older than the field).</summary>
    internal static readonly Lazy<string?> RunningMode = new(() =>
    {
        try
        {
            return Task.Run(async () =>
            {
                await using var site = await SiteUnderTest.ConnectAsync(CancellationToken.None);
                return (await site.Agent.PingAsync(CancellationToken.None)).InlineBlocks;
            }).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    });
}
