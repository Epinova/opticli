namespace OptiCli.Integration;

/// <summary>
/// A fact that needs a real site: it runs only when <see cref="SiteSettings.ProjectVariable"/> names a CMS
/// site project whose agent is running (<c>opticli serve</c>), and is reported as skipped otherwise, so a
/// plain <c>dotnet test</c> stays green and fast.
/// </summary>
public sealed class SiteFactAttribute : FactAttribute
{
    public SiteFactAttribute()
    {
        if (SiteSettings.ProjectDirectory is null)
        {
            Skip = $"Needs a site: set {SiteSettings.ProjectVariable} to a CMS 12 site project directory and start its agent with `opticli serve` there.";
        }
    }
}
