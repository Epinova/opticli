using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace OptiCli.Core.Discovery;

public static class PackageVersions
{
    /// <summary>
    /// The CMS version the project builds against: as declared on a direct <see cref="CsprojFile.CmsPackage"/> reference,
    /// else as restore resolved it. The EPiServer.CMS meta package brings the CMS in transitively at a version of its
    /// own (12.29.0 can mean CMS 12.21.2), so its version is never reported. Null when neither is known, typically
    /// because the project has not been restored.
    /// </summary>
    public static string? FindCms(CsprojFile project) =>
        Find(project, CsprojFile.CmsPackage) ?? FindResolved(project, CsprojFile.CmsPackage);

    /// <summary>The major version of a package version (<c>13.3.0</c> → 13); null when there is none or it doesn't parse.</summary>
    public static int? Major(string? version) =>
        version?.Trim().Split('.', '-', '+')[0] is { Length: > 0 } major && int.TryParse(major, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// The version of <paramref name="packageId"/> as the project declares it: on its PackageReference,
    /// or, with central package management, in the nearest Directory.Packages.props above it.
    /// Null when the project has no PackageReference to it.
    /// </summary>
    public static string? Find(CsprojFile project, string packageId)
    {
        if (!project.PackageReferences.TryGetValue(packageId, out var version))
        {
            return null;
        }
        if (version is not null)
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

    /// <summary>The restore output a project's resolved package versions are read from.</summary>
    public static string AssetsFile(CsprojFile project) =>
        Path.Combine(Path.GetDirectoryName(project.Path)!, "obj", "project.assets.json");

    /// <summary>
    /// The version of <paramref name="packageId"/> restore resolved for the project, directly or transitively,
    /// from its obj/project.assets.json. Null when the project has not been restored or the package is not in its graph.
    /// </summary>
    public static string? FindResolved(CsprojFile project, string packageId)
    {
        var path = AssetsFile(project);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Keys are "<id>/<version>", one per resolved package.
            var prefix = packageId + "/";
            return libraries.EnumerateObject()
                .Where(library => library.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(library => library.Name[prefix.Length..])
                .FirstOrDefault(v => v.Length > 0);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
