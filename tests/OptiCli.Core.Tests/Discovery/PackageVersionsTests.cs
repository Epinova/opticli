using OptiCli.Core.Discovery;

namespace OptiCli.Core.Tests.Discovery;

public class PackageVersionsTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    /// <summary>A restore output whose graph has the meta package and, transitively, the CMS at a version of its own.</summary>
    private const string MetaPackageAssets = """
        {
          "version": 3,
          "targets": {
            "net8.0": {
              "EPiServer.CMS/12.29.0": { "type": "package" },
              "EPiServer.CMS.AspNetCore/12.21.2": { "type": "package" },
              "EPiServer.CMS.AspNetCore.Mvc/12.21.2": { "type": "package" }
            }
          },
          "libraries": {
            "EPiServer.CMS/12.29.0": { "type": "package" },
            "EPiServer.CMS.AspNetCore.Mvc/12.21.2": { "type": "package" },
            "EPiServer.CMS.AspNetCore/12.21.2": { "type": "package" }
          }
        }
        """;

    private CsprojFile Project(string csproj = """
        <Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="EPiServer.CMS" Version="12.29.0" /></ItemGroup></Project>
        """) => CsprojFile.TryLoad(_root.Write("repo/src/Web/Web.csproj", csproj))!;

    [Fact]
    public void Direct_reference_version_is_the_cms_version()
    {
        var project = Project(SiteFixture.Csproj());

        Assert.Equal("12.0.0", PackageVersions.FindCms(project));
    }

    [Fact]
    public void Meta_package_reads_the_resolved_cms_version_from_the_assets_file()
    {
        var project = Project();
        _root.Write("repo/src/Web/obj/project.assets.json", MetaPackageAssets);

        Assert.Equal("12.21.2", PackageVersions.FindCms(project));
    }

    [Fact]
    public void Meta_package_without_an_assets_file_has_no_cms_version()
    {
        var project = Project();

        Assert.Null(PackageVersions.FindCms(project));
    }

    [Fact]
    public void Meta_package_ignores_a_central_version_the_project_does_not_reference()
    {
        _root.Write("repo/Directory.Packages.props", """
            <Project><ItemGroup>
              <PackageVersion Include="EPiServer.CMS" Version="12.29.0" />
              <PackageVersion Include="EPiServer.CMS.AspNetCore" Version="12.9.0" />
            </ItemGroup></Project>
            """);
        var project = Project("""
            <Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="EPiServer.CMS" /></ItemGroup></Project>
            """);
        _root.Write("repo/src/Web/obj/project.assets.json", MetaPackageAssets);

        Assert.Null(PackageVersions.Find(project, CsprojFile.CmsPackage));
        Assert.Equal("12.21.2", PackageVersions.FindCms(project));
    }

    [Fact]
    public void Declared_version_wins_over_the_assets_file()
    {
        var project = Project(SiteFixture.Csproj());
        _root.Write("repo/src/Web/obj/project.assets.json", MetaPackageAssets);

        Assert.Equal("12.0.0", PackageVersions.FindCms(project));
    }

    [Fact]
    public void Resolved_version_matches_the_whole_package_id()
    {
        var project = Project();
        _root.Write("repo/src/Web/obj/project.assets.json", """
            { "version": 3, "libraries": { "EPiServer.CMS.AspNetCore.Mvc/12.21.2": { "type": "package" } } }
            """);

        Assert.Null(PackageVersions.FindResolved(project, CsprojFile.CmsPackage));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "version": 3 }""")]
    [InlineData("""{ "libraries": [] }""")]
    public void Unreadable_assets_file_has_no_resolved_version(string content)
    {
        var project = Project();
        _root.Write("repo/src/Web/obj/project.assets.json", content);

        Assert.Null(PackageVersions.FindResolved(project, CsprojFile.CmsPackage));
    }
}
