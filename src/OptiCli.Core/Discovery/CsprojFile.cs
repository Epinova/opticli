using System.Xml;
using System.Xml.Linq;

namespace OptiCli.Core.Discovery;

/// <summary>
/// The few facts opticli needs from a .csproj, read as plain XML without MSBuild. Properties come from the nearest
/// <c>Directory.Build.props</c> and its imports, then the project file, as <see cref="MsBuildProperties"/> evaluates them.
/// </summary>
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

    /// <summary><c>OutputPath</c> as set (MSBuild's default when null); <c>$(Configuration)</c> is left in.</summary>
    public string? OutputPath { get; init; }

    /// <summary><c>BaseOutputPath</c> as set (<c>bin\</c> when null); <c>$(Configuration)</c> is left in.</summary>
    public string? BaseOutputPath { get; init; }

    /// <summary><c>UseArtifactsOutput</c> as set: <c>true</c> puts the output under <see cref="ArtifactsPath"/>.</summary>
    public string? UseArtifactsOutput { get; init; }

    /// <summary><c>ArtifactsPath</c> as set, for <see cref="UseArtifactsOutput"/>.</summary>
    public string? ArtifactsPath { get; init; }

    /// <summary>The files read for the properties besides the project file: the nearest <c>Directory.Build.props</c> and what it imports, then what the project imports, in that order.</summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>Properties opticli couldn't work out (left as written, or ignored), for <c>doctor</c>.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <returns>Null when the file is not readable XML.</returns>
    public static CsprojFile? TryLoad(string path) => TryLoad(path, evaluateBuildProps: true);

    /// <param name="evaluateBuildProps">False reads the project file alone: enough to tell a CMS web project apart when scanning many.</param>
    /// <returns>Null when the file is not readable XML.</returns>
    internal static CsprojFile? TryLoad(string path, bool evaluateBuildProps)
    {
        path = System.IO.Path.GetFullPath(path);
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

        var properties = MsBuildProperties.Evaluate(path, root, evaluateBuildProps);
        return new CsprojFile(
            path,
            (string?)root.Attribute("Sdk"),
            properties["UserSecretsId"],
            properties["TargetFramework"] ?? properties["TargetFrameworks"],
            properties["AssemblyName"],
            packages)
        {
            OutputPath = properties["OutputPath"],
            BaseOutputPath = properties["BaseOutputPath"],
            UseArtifactsOutput = properties["UseArtifactsOutput"],
            ArtifactsPath = properties["ArtifactsPath"],
            Imports = properties.Imported,
            Warnings = properties.Warnings,
        };
    }
}
