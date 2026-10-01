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
        Assert.Null(OutputLocator.NewerSource(_site.Project(), dll));

        var changed = Output("Features/Article/ArticlePage.cshtml");
        Assert.Equal(changed, OutputLocator.NewerSource(_site.Project(), dll));
    }

    [Fact]
    public void A_runtime_identifier_build_is_found_below_the_framework_folder_but_publish_output_is_not()
    {
        Output("bin/Release/net8.0/publish/Web.dll", DateTime.UtcNow);
        var rid = Output("bin/Debug/net8.0/linux-x64/Web.dll", DateTime.UtcNow.AddMinutes(-5));
        Output("bin/Debug/net8.0/en/Web.resources.dll");

        Assert.Equal(rid, OutputLocator.Locate(_site.Project(), null, null, _site.ProjectPath).Dll);
    }

    [Theory]
    [InlineData(@"<OutputPath>build\$(Configuration)\</OutputPath>", "build/Debug/net8.0/Web.dll")]
    [InlineData("<BaseOutputPath>../out/</BaseOutputPath>", "../out/Debug/net8.0/Web.dll")]
    [InlineData("<OutputPath>build/</OutputPath><AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>", "build/Web.dll")]
    public void Output_and_base_output_paths_are_followed(string property, string expected)
    {
        _site.WriteProjectFile("Web.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework>{property}</PropertyGroup>
              <ItemGroup><PackageReference Include="EPiServer.CMS.AspNetCore" Version="12.0.0" /></ItemGroup>
            </Project>
            """);
        Output("bin/Debug/net8.0/Web.dll", DateTime.UtcNow.AddDays(-1));
        var dll = Output(expected);

        Assert.Equal(Path.GetFullPath(dll), OutputLocator.Locate(_site.Project(), null, null, _site.ProjectPath).Dll);
    }

    [Fact]
    public void Artifacts_output_goes_next_to_the_directory_build_props()
    {
        _site.Write("repo/Directory.Build.props", "<Project><PropertyGroup><UseArtifactsOutput>true</UseArtifactsOutput></PropertyGroup></Project>");

        var missing = Assert.Throws<NotFoundException>(() => OutputLocator.Locate(_site.Project(), null, null, _site.ProjectPath));
        Assert.Contains(Path.Combine("artifacts", "bin", "Web", "debug", "Web.dll"), missing.Message);

        var debug = _site.Write("repo/artifacts/bin/Web/debug/Web.dll", "dll");
        File.SetLastWriteTimeUtc(debug, DateTime.UtcNow.AddHours(-1));
        Assert.Equal(debug, OutputLocator.Locate(_site.Project(), null, null, _site.ProjectPath).Dll);

        var rid = _site.Write("repo/artifacts/bin/Web/release_linux-x64/Web.dll", "dll");
        Assert.Equal(rid, OutputLocator.Locate(_site.Project(), null, null, _site.ProjectPath).Dll);
    }

    [Fact]
    public void Sources_of_referenced_projects_count_too()
    {
        _site.WriteProjectFile("Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="EPiServer.CMS.AspNetCore" Version="12.0.0" />
                <ProjectReference Include="..\Core\Core.csproj" />
              </ItemGroup>
            </Project>
            """);
        _site.Write("repo/src/Core/Core.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Shared/Shared.csproj" /></ItemGroup></Project>""");
        _site.Write("repo/src/Shared/Shared.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Core/Core.csproj" /></ItemGroup></Project>""");
        _site.Write("repo/src/Unrelated/Unrelated.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");
        var built = DateTime.UtcNow.AddHours(-1);
        foreach (var file in Directory.GetFiles(Path.Combine(_site.Root, "repo", "src"), "*.csproj", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, built.AddHours(-1));
        }
        var dll = Output("bin/Debug/net8.0/Web.dll", built);
        _site.Write("repo/src/Unrelated/Newer.cs", "class Newer;");
        Assert.Null(OutputLocator.NewerSource(_site.Project(), dll));

        // Shared is referenced through Core (which Shared references back).
        var changed = _site.Write("repo/src/Shared/Helper.cs", "class Helper;");
        Assert.Equal(changed, OutputLocator.NewerSource(_site.Project(), dll));
    }
}
