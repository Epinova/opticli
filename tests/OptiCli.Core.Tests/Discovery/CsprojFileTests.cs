using OptiCli.Core.Discovery;

namespace OptiCli.Core.Tests.Discovery;

public class CsprojFileTests : IDisposable
{
    private const string ProjectPath = "repo/src/Web/Web.csproj";

    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private CsprojFile Load(string properties = "", string extra = "")
    {
        var path = _root.Write(ProjectPath, $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              {extra}
              <PropertyGroup>
                {properties}
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="EPiServer.CMS.AspNetCore" Version="12.0.0" />
              </ItemGroup>
            </Project>
            """);
        return CsprojFile.TryLoad(path)!;
    }

    private string Props(string relativePath, string body) => _root.Write(relativePath, $"""
        <Project>
          {body}
        </Project>
        """);

    [Fact]
    public void Reads_properties_from_directory_build_props_at_the_repository_root()
    {
        var props = Props("repo/Directory.Build.props", """
            <PropertyGroup>
              <UserSecretsId>from-props</UserSecretsId>
              <TargetFramework>net8.0</TargetFramework>
              <AssemblyName>Example.Site</AssemblyName>
            </PropertyGroup>
            """);

        var project = Load();

        Assert.Equal("from-props", project.UserSecretsId);
        Assert.Equal("net8.0", project.TargetFramework);
        Assert.Equal("Example.Site", project.AssemblyName);
        Assert.Equal([props], project.Imports);
        Assert.Empty(project.Warnings);
    }

    [Fact]
    public void Directory_build_targets_is_read_after_the_project_and_wins()
    {
        var props = Props("repo/Directory.Build.props", "<PropertyGroup><AssemblyName>From.Props</AssemblyName></PropertyGroup>");
        var targets = Props("repo/Directory.Build.targets", "<PropertyGroup><UserSecretsId>from-targets</UserSecretsId></PropertyGroup>");

        var project = Load("<UserSecretsId>from-project</UserSecretsId>");

        Assert.Equal(("from-targets", "From.Props"), (project.UserSecretsId, project.AssemblyName));
        Assert.Equal([props, targets], project.Imports);
    }

    [Fact]
    public void The_project_file_wins_over_directory_build_props()
    {
        Props("repo/Directory.Build.props", "<PropertyGroup><UserSecretsId>from-props</UserSecretsId></PropertyGroup>");

        Assert.Equal("from-project", Load("<UserSecretsId>from-project</UserSecretsId>").UserSecretsId);
    }

    [Fact]
    public void Only_the_nearest_props_file_is_read_unless_it_imports_its_parent()
    {
        Props("repo/Directory.Build.props", "<PropertyGroup><UserSecretsId>outer</UserSecretsId><AssemblyName>Outer</AssemblyName></PropertyGroup>");
        Props("repo/src/Directory.Build.props", "<PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>");

        var alone = Load();

        Assert.Null(alone.UserSecretsId);
        Assert.Equal("net8.0", alone.TargetFramework);
        Assert.Single(alone.Imports);
    }

    [Fact]
    public void A_nested_props_file_that_imports_its_parent_overrides_it()
    {
        var outer = Props("repo/Directory.Build.props", "<PropertyGroup><UserSecretsId>outer</UserSecretsId><AssemblyName>Outer</AssemblyName></PropertyGroup>");
        var inner = Props("repo/src/Directory.Build.props", """
            <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
            <PropertyGroup><AssemblyName>Inner</AssemblyName></PropertyGroup>
            """);

        var project = Load();

        Assert.Equal("outer", project.UserSecretsId);
        Assert.Equal("Inner", project.AssemblyName);
        Assert.Equal([inner, outer], project.Imports);
        Assert.Empty(project.Warnings);
    }

    [Fact]
    public void Follows_relative_imports_with_either_slash_and_import_conditions()
    {
        Props("repo/build/common.props", "<PropertyGroup><UserSecretsId>common</UserSecretsId></PropertyGroup>");
        Props("repo/Directory.Build.props", """
            <Import Project="build\common.props" Condition="Exists('build\common.props')" />
            <Import Project="build/missing.props" Condition="Exists('build/missing.props')" />
            """);

        var project = Load();

        Assert.Equal("common", project.UserSecretsId);
        Assert.Equal(2, project.Imports.Count);
        Assert.Empty(project.Warnings);
    }

    [Fact]
    public void Expands_references_to_properties_read_so_far()
    {
        Props("repo/Directory.Build.props", """
            <PropertyGroup>
              <SiteKey>example</SiteKey>
              <UserSecretsId>$(SiteKey)-$(MSBuildProjectName)</UserSecretsId>
              <ArtifactsPath>$(MSBuildThisFileDirectory)artifacts</ArtifactsPath>
              <UseArtifactsOutput>true</UseArtifactsOutput>
            </PropertyGroup>
            """);

        var project = Load("<AssemblyName>$(SiteKey).Site</AssemblyName>");

        Assert.Equal("example-Web", project.UserSecretsId);
        Assert.Equal("example.Site", project.AssemblyName);
        Assert.Equal(_root.Combine("repo") + Path.DirectorySeparatorChar + "artifacts", project.ArtifactsPath);
        Assert.Equal("true", project.UseArtifactsOutput);
        Assert.Empty(project.Warnings);
    }

    [Fact]
    public void An_unexpandable_reference_is_left_as_written_with_a_warning()
    {
        var project = Load("<UserSecretsId>$(SolutionName)-web</UserSecretsId>");

        Assert.Equal("$(SolutionName)-web", project.UserSecretsId);
        var warning = Assert.Single(project.Warnings);
        Assert.Contains("UserSecretsId is '$(SolutionName)-web' (Web.csproj)", warning, StringComparison.Ordinal);
        Assert.Contains("User secrets are not read", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_paths_keep_the_configuration_for_their_reader()
    {
        var project = Load("<BaseOutputPath>build\\</BaseOutputPath><OutputPath>$(BaseOutputPath)$(Configuration)\\</OutputPath>");

        Assert.Equal("build\\", project.BaseOutputPath);
        Assert.Equal("build\\$(Configuration)\\", project.OutputPath);
        Assert.Empty(project.Warnings);
    }

    [Fact]
    public void Conditioned_groups_apply_only_for_debug()
    {
        var project = Load(extra: """
            <PropertyGroup Condition="'$(Configuration)' == 'Release'"><UserSecretsId>release</UserSecretsId></PropertyGroup>
            <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' "><AssemblyName>Debug.Site</AssemblyName></PropertyGroup>
            <PropertyGroup><TargetFramework Condition="'$(Configuration)' != 'Debug'">net9.0</TargetFramework></PropertyGroup>
            """);

        Assert.Null(project.UserSecretsId);
        Assert.Equal("Debug.Site", project.AssemblyName);
        Assert.Null(project.TargetFramework);
        Assert.Empty(project.Warnings);
    }

    [Fact]
    public void A_set_if_empty_default_in_props_gives_way_to_the_project()
    {
        Props("repo/Directory.Build.props", """
            <PropertyGroup><UserSecretsId Condition="'$(UserSecretsId)' == ''">shared</UserSecretsId></PropertyGroup>
            <PropertyGroup Condition="Exists('$(MSBuildThisFileDirectory)src') and '$(Configuration)' == 'Debug'"><AssemblyName>Shared.Site</AssemblyName></PropertyGroup>
            """);

        Assert.Equal("shared", Load().UserSecretsId);
        Assert.Equal("Shared.Site", Load().AssemblyName);
        Assert.Equal("own", Load("<UserSecretsId>own</UserSecretsId>").UserSecretsId);
    }

    [Fact]
    public void A_condition_that_cannot_be_evaluated_is_skipped_with_a_warning()
    {
        var project = Load(extra: """
            <PropertyGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))"><UserSecretsId>windows-only</UserSecretsId></PropertyGroup>
            <PropertyGroup Condition="$([MSBuild]::IsOSPlatform('Linux'))"><LangVersion>latest</LangVersion></PropertyGroup>
            """);

        Assert.Null(project.UserSecretsId);
        var warning = Assert.Single(project.Warnings);
        Assert.Contains("sets UserSecretsId under the condition", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_definition_wins_and_targets_are_ignored()
    {
        var project = Load(
            "<UserSecretsId>first</UserSecretsId><UserSecretsId>second</UserSecretsId>",
            extra: "<Target Name=\"Late\"><PropertyGroup><UserSecretsId>in-target</UserSecretsId></PropertyGroup></Target>");

        Assert.Equal("second", project.UserSecretsId);
    }

    [Fact]
    public void An_import_cycle_is_read_once()
    {
        Props("repo/Directory.Build.props", """
            <Import Project="$(MSBuildThisFileFullPath)" />
            <PropertyGroup><UserSecretsId>cycle</UserSecretsId></PropertyGroup>
            """);

        var project = Load();

        Assert.Equal("cycle", project.UserSecretsId);
        Assert.Contains(project.Warnings, w => w.Contains("imports itself", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanning_reads_the_project_file_alone_and_the_located_project_gets_its_props()
    {
        Props("repo/Directory.Build.props", "<PropertyGroup><UserSecretsId>from-props</UserSecretsId></PropertyGroup>");
        var path = _root.Write(ProjectPath, SiteFixture.Csproj());

        Assert.Null(CsprojFile.TryLoad(path, evaluateBuildProps: false)!.UserSecretsId);
        Assert.Equal("from-props", ProjectLocator.Locate(null, _root.Combine("repo")).Project.UserSecretsId);
    }
}
