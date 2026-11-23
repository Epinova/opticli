using OptiCli.Core.Discovery;

namespace OptiCli.Integration;

/// <summary>
/// A fact that needs a Commerce site: <see cref="SiteSettings.ProjectVariable"/> names a site whose restored project has
/// EPiServer.Commerce.Core (tests/fixtures/commerce/setup.sh builds two), with its agent running (<c>opticli serve</c>).
/// Skipped elsewhere, also on a CMS-only site. Run them alone on a Commerce site, where the CMS tests' fixtures aren't:
/// <c>--filter "FullyQualifiedName~Commerce"</c>.
/// </summary>
public sealed class CommerceSiteFactAttribute : FactAttribute
{
    public CommerceSiteFactAttribute()
    {
        if (SiteSettings.ProjectDirectory is not { } directory)
        {
            Skip = $"Needs a Commerce site: set {SiteSettings.ProjectVariable} to one (tests/fixtures/commerce/setup.sh builds them) and start it with `opticli serve`.";
        }
        else if (!IsCommerce(directory))
        {
            Skip = $"{directory} has no Optimizely Commerce (no {PackageVersions.CommerceCorePackage} in its restored project); these tests run on the Commerce test sites (tests/fixtures/commerce/setup.sh).";
        }
    }

    internal static bool IsCommerce(string directory)
    {
        try
        {
            return PackageVersions.FindCommerce(ProjectLocator.Locate(directory, directory).Project) is not null;
        }
        catch (Core.Errors.OptiCliException)
        {
            return false;
        }
    }
}
