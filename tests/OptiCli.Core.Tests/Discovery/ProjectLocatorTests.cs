using OptiCli.Core.Discovery;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Tests.Discovery;

public class ProjectLocatorTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Finds_the_project_in_the_working_directory()
    {
        _root.Write("repo/Example.sln", "");
        _root.Write("repo/src/Web/Web.csproj", SiteFixture.Csproj("id-1"));

        var project = ProjectLocator.Locate(null, _root.Combine("repo/src/Web"));

        Assert.Equal(_root.Combine("repo/src/Web/Web.csproj"), project.ProjectFile);
        Assert.Equal(_root.Combine("repo"), project.SourceRoot);
        Assert.Equal(_root.Combine("repo/Example.sln"), project.SolutionFile);
        Assert.Equal("id-1", project.Project.UserSecretsId);
    }

    [Fact]
    public void Walks_up_from_a_subdirectory()
    {
        _root.Write("repo/src/Web/Web.csproj", SiteFixture.Csproj());
        Directory.CreateDirectory(_root.Combine("repo/src/Web/Features/Article"));

        var project = ProjectLocator.Locate(null, _root.Combine("repo/src/Web/Features/Article"));

        Assert.Equal(_root.Combine("repo/src/Web/Web.csproj"), project.ProjectFile);
        Assert.Equal(_root.Combine("repo/src/Web"), project.SourceRoot);
    }

    [Fact]
    public void Searches_the_solution_from_the_repository_root_skipping_copies_and_build_output()
    {
        _root.Write("repo/Example.slnx", "");
        _root.Write("repo/src/Web/Web.csproj", SiteFixture.Csproj());
        _root.Write("repo/src/Library/Library.csproj", SiteFixture.Csproj(sdk: "Microsoft.NET.Sdk", package: "Newtonsoft.Json"));
        _root.Write("repo/.claude/worktrees/branch/src/Web/Web.csproj", SiteFixture.Csproj());
        _root.Write("repo/.git/modules/Web.csproj", SiteFixture.Csproj());
        _root.Write("repo/src/Web/bin/Debug/Web.csproj", SiteFixture.Csproj());
        _root.Write("repo/src/Web/obj/Web.csproj", SiteFixture.Csproj());
        _root.Write("repo/src/Web/node_modules/pkg/Web.csproj", SiteFixture.Csproj());

        var project = ProjectLocator.Locate(null, _root.Combine("repo"));

        Assert.Equal(_root.Combine("repo/src/Web/Web.csproj"), project.ProjectFile);
    }

    [Fact]
    public void Starting_in_a_non_cms_project_uses_the_solution()
    {
        _root.Write("repo/Example.sln", "");
        _root.Write("repo/src/Web/Web.csproj", SiteFixture.Csproj());
        _root.Write("repo/src/Library/Library.csproj", SiteFixture.Csproj(sdk: "Microsoft.NET.Sdk", package: "Newtonsoft.Json"));

        var project = ProjectLocator.Locate(null, _root.Combine("repo/src/Library"));

        Assert.Equal(_root.Combine("repo/src/Web/Web.csproj"), project.ProjectFile);
    }

    [Fact]
    public void Prefers_the_web_project_over_a_test_project_referencing_the_cms()
    {
        _root.Write("repo/Example.sln", "");
        _root.Write("repo/src/Web/Web.csproj", SiteFixture.Csproj());
        _root.Write("repo/tests/Web.Tests/Web.Tests.csproj", SiteFixture.Csproj(sdk: "Microsoft.NET.Sdk"));

        var project = ProjectLocator.Locate(null, _root.Combine("repo"));

        Assert.Equal(_root.Combine("repo/src/Web/Web.csproj"), project.ProjectFile);
    }

    [Fact]
    public void Accepts_the_cms_meta_package()
    {
        _root.Write("repo/src/Web/Web.csproj", SiteFixture.Csproj(package: "EPiServer.CMS"));

        Assert.True(ProjectLocator.Locate(null, _root.Combine("repo/src/Web")).Project.IsCmsProject);
    }

    [Fact]
    public void Several_cms_projects_is_a_usage_error_asking_for_project()
    {
        _root.Write("repo/Example.sln", "");
        _root.Write("repo/src/SiteA/SiteA.csproj", SiteFixture.Csproj());
        _root.Write("repo/src/SiteB/SiteB.csproj", SiteFixture.Csproj());

        var error = Assert.Throws<UsageException>(() => ProjectLocator.Locate(null, _root.Combine("repo")));

        Assert.Contains("--project", error.Hint, StringComparison.Ordinal);
        Assert.Contains("SiteA", error.Message, StringComparison.Ordinal);
        Assert.Contains("SiteB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_project_resolves_the_ambiguity()
    {
        _root.Write("repo/Example.sln", "");
        _root.Write("repo/src/SiteA/SiteA.csproj", SiteFixture.Csproj());
        _root.Write("repo/src/SiteB/SiteB.csproj", SiteFixture.Csproj());

        var byFile = ProjectLocator.Locate("src/SiteB/SiteB.csproj", _root.Combine("repo"));
        var byDirectory = ProjectLocator.Locate("src/SiteA", _root.Combine("repo"));

        Assert.Equal(_root.Combine("repo/src/SiteB/SiteB.csproj"), byFile.ProjectFile);
        Assert.Equal("--project", byFile.HowFound);
        Assert.Equal(_root.Combine("repo/src/SiteA/SiteA.csproj"), byDirectory.ProjectFile);
        Assert.Equal(_root.Combine("repo"), byDirectory.SourceRoot);
    }

    [Fact]
    public void Missing_explicit_project_is_not_found()
    {
        Assert.Throws<NotFoundException>(() => ProjectLocator.Locate("nope/Web.csproj", _root.Path));
    }

    [Fact]
    public void No_cms_project_is_not_found()
    {
        _root.Write("repo/Example.sln", "");
        _root.Write("repo/src/Library/Library.csproj", SiteFixture.Csproj(sdk: "Microsoft.NET.Sdk", package: "Newtonsoft.Json"));

        Assert.Throws<NotFoundException>(() => ProjectLocator.Locate(null, _root.Combine("repo")));
    }

    [Fact]
    public void Reads_central_package_versions()
    {
        _root.Write("repo/Directory.Packages.props", """
            <Project><ItemGroup><PackageVersion Include="EPiServer.CMS.AspNetCore" Version="12.9.0" /></ItemGroup></Project>
            """);
        _root.Write("repo/src/Web/Web.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup><PackageReference Include="EPiServer.CMS.AspNetCore" /></ItemGroup></Project>
            """);

        var project = ProjectLocator.Locate(null, _root.Combine("repo/src/Web"));

        Assert.Equal("12.9.0", PackageVersions.Find(project.Project, CsprojFile.CmsPackage));
    }
}
