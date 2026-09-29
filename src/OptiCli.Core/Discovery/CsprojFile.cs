using System.Xml;
using System.Xml.Linq;

namespace OptiCli.Core.Discovery;

/// <summary>The few facts opticli needs from a .csproj, read as plain XML (no MSBuild evaluation).</summary>
public sealed record CsprojFile(
    string Path,
    string? Sdk,
    string? UserSecretsId,
    string? TargetFramework,
    string? AssemblyName,
    IReadOnlyDictionary<string, string?> PackageReferences)
{
    /// <summary>The package that makes a project a CMS 12 web site.</summary>
    public const string CmsPackage = "EPiServer.CMS.AspNetCore";

    /// <summary>The CMS meta package, which brings <see cref="CmsPackage"/> in transitively.</summary>
    private const string CmsMetaPackage = "EPiServer.CMS";

    public bool IsCmsProject => PackageReferences.ContainsKey(CmsPackage) || PackageReferences.ContainsKey(CmsMetaPackage);

    public bool IsWebProject => Sdk?.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) == true;

    /// <returns>Null when the file is not readable XML.</returns>
    public static CsprojFile? TryLoad(string path)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var root = document.Root;
        if (root is null)
        {
            return null;
        }

        string? Property(string name) =>
            root.Descendants().FirstOrDefault(e => e.Name.LocalName == name && !string.IsNullOrWhiteSpace(e.Value))?.Value.Trim();

        var packages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in root.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var id = (string?)reference.Attribute("Include") ?? (string?)reference.Attribute("Update");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            var version = (string?)reference.Attribute("Version")
                ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
            packages[id.Trim()] = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }

        return new CsprojFile(
            System.IO.Path.GetFullPath(path),
            (string?)root.Attribute("Sdk"),
            Property("UserSecretsId"),
            Property("TargetFramework") ?? Property("TargetFrameworks"),
            Property("AssemblyName"),
            packages);
    }
}
