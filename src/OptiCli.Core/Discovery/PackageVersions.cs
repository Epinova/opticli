using System.Xml;
using System.Xml.Linq;

namespace OptiCli.Core.Discovery;

public static class PackageVersions
{
    /// <summary>
    /// The version of <paramref name="packageId"/> as the project declares it: on its PackageReference,
    /// or, with central package management, in the nearest Directory.Packages.props above it.
    /// </summary>
    public static string? Find(CsprojFile project, string packageId)
    {
        if (project.PackageReferences.GetValueOrDefault(packageId) is { } version)
        {
            return version;
        }

        for (var directory = Path.GetDirectoryName(project.Path); directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var props = Path.Combine(directory, "Directory.Packages.props");
            if (File.Exists(props))
            {
                return FromCentralFile(props, packageId);
            }
        }
        return null;
    }

    private static string? FromCentralFile(string path, string packageId)
    {
        try
        {
            return XDocument.Load(path).Descendants()
                .Where(e => e.Name.LocalName == "PackageVersion")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("Include"), packageId, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("Version")?.Value;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
