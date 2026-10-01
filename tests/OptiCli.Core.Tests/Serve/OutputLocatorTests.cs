using OptiCli.Core.Errors;
using OptiCli.Core.Serve;

namespace OptiCli.Core.Tests.Serve;

public class OutputLocatorTests : IDisposable
{
    private readonly SiteFixture _site = new();

    public void Dispose() => _site.Dispose();

    private string Output(string relative, DateTime? time = null)
    {
        var path = _site.Write($"{SiteFixture.ProjectDirectory}/{relative}", "dll");
        File.SetLastWriteTimeUtc(path, time ?? DateTime.UtcNow);
        return path;
    }

    [Fact]
    public void Finds_the_debug_build_named_after_the_csproj()
    {
        var dll = Output("bin/Debug/net8.0/Web.dll");

        var found = OutputLocator.Locate(_site.Project(), null, null, _site.ProjectPath);

        Assert.Equal(dll, found.Dll);
        Assert.Contains("bin/Debug/net8.0/Web.dll".Replace('/', Path.DirectorySeparatorChar), found.HowFound);
    }

    [Fact]
    public void Uses_the_assembly_name_and_the_newest_configuration()
    {
        _site.WriteProjectFile("Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks><AssemblyName>Example.Site</AssemblyName></PropertyGroup>
              <ItemGroup><PackageReference Include="EPiServer.CMS.AspNetCore" Version="12.0.0" /></ItemGroup>
            </Project>
            """);
        Output("bin/Debug/net8.0/Example.Site.dll", DateTime.UtcNow.AddHours(-2));
        var release = Output("bin/Release/net9.0/Example.Site.dll", DateTime.UtcNow.AddHours(-1));
        Output("bin/Debug/net8.0/Web.dll");

        Assert.Equal(release, OutputLocator.Locate(_site.Project(), null, null, _site.ProjectPath).Dll);
    }

    [Fact]
    public void An_assembly_name_expression_falls_back_to_the_file_name_and_a_missing_framework_scans_bin()
    {
        _site.WriteProjectFile("Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><AssemblyName>$(SolutionName).Custom</AssemblyName></PropertyGroup>
              <ItemGroup><PackageReference Include="EPiServer.CMS.AspNetCore" Version="12.0.0" /></ItemGroup>
            </Project>
            """);
        var dll = Output("bin/Debug/net10.0/Web.dll");

        Assert.Equal(dll, OutputLocator.Locate(_site.Project(), null, null, _site.ProjectPath).Dll);
    }

    [Fact]
    public void An_assembly_name_built_from_the_project_name_is_expanded()
    {
        _site.WriteProjectFile("Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework><AssemblyName>$(MSBuildProjectName).Custom</AssemblyName></PropertyGroup>
              <ItemGroup><PackageReference Include="EPiServer.CMS.AspNetCore" Version="12.0.0" /></ItemGroup>
            </Project>
            """);
        var dll = Output("bin/Debug/net8.0/Web.Custom.dll");

        Assert.Equal(dll, OutputLocator.Locate(_site.Project(), null, null, _site.ProjectPath).Dll);
    }

    [Fact]
    public void Explicit_and_configured_outputs_win_and_must_exist()
    {
        Output("bin/Debug/net8.0/Web.dll");
        var custom = Output("out/Web.dll");

        Assert.Equal(custom, OutputLocator.Locate(_site.Project(), "out/Web.dll", "ignored.dll", _site.ProjectPath).Dll);
        Assert.Equal(custom, OutputLocator.Locate(_site.Project(), null, "out/Web.dll", _site.Root).Dll);
        Assert.Throws<NotFoundException>(() => OutputLocator.Locate(_site.Project(), null, "missing.dll", _site.ProjectPath));
    }

    [Fact]
    public void No_build_output_is_not_found_with_a_hint_to_build()
    {
        var error = Assert.Throws<NotFoundException>(() => OutputLocator.Locate(_site.Project(), null, null, _site.ProjectPath));

        Assert.Contains("bin", error.Message);
        Assert.Contains("--build", error.Hint);
    }

    [Fact]
    public void Newer_sources_are_reported_but_build_output_and_packages_are_ignored()
    {
        var built = DateTime.UtcNow.AddHours(-1);
        var dll = Output("bin/Debug/net8.0/Web.dll", built);
        File.SetLastWriteTimeUtc(_site.Project().ProjectFile, built.AddHours(-1));
        foreach (var ignored in new[] { "obj/Generated.cs", "node_modules/pkg/x.cs", "bin/Debug/net8.0/Views.cshtml", "wwwroot/app.js" })
        {
            Output(ignored);
        }
        Assert.Null(OutputLocator.NewerSource(_site.ProjectPath, dll));

        var changed = Output("Features/Article/ArticlePage.cshtml");
        Assert.Equal(changed, OutputLocator.NewerSource(_site.ProjectPath, dll));
    }
}
