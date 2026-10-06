using OptiCli.Core.Cms;
using OptiCli.Core.SourceScan;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Cms;

public class OrphanedTypesTests : IDisposable
{
    private static readonly Guid Article = Guid.Parse("7d0e8f4a-1b2c-4d3e-8f9a-0b1c2d3e4f01");
    private static readonly Guid Renamed = Guid.Parse("7d0e8f4a-1b2c-4d3e-8f9a-0b1c2d3e4f02");
    private static readonly Guid Removed = Guid.Parse("7d0e8f4a-1b2c-4d3e-8f9a-0b1c2d3e4f03");

    private readonly TempDirectory _root = new();

    public OrphanedTypesTests()
    {
        _root.Write("Web/Example.Web.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><AssemblyName>Example.Web</AssemblyName></PropertyGroup></Project>""");
        _root.Write("Web/Models/ArticlePage.cs", """
            namespace Example.Web.Models;

            [ContentType(GUID = "7d0e8f4a-1b2c-4d3e-8f9a-0b1c2d3e4f01")]
            public class ArticlePage : PageData
            {
            }
            """);
        // Renamed in code, same GUID: found by its GUID, so not orphaned.
        _root.Write("Web/Models/NewsArticlePage.cs", """
            namespace Example.Web.Models;

            [ContentType(GUID = "7d0e8f4a-1b2c-4d3e-8f9a-0b1c2d3e4f02")]
            public class NewsArticlePage : PageData
            {
            }
            """);
    }

    public void Dispose() => _root.Dispose();

    private static ContentTypeInfo Type(int id, Guid guid, string name, string? modelType, int instances = 1) =>
        new(id, guid, name, null, null, ContentKind.Page, "Page", modelType, instances);

    private static readonly IReadOnlyList<ContentTypeInfo> Types =
    [
        Type(1, Guid.NewGuid(), "SysRoot", null),
        Type(2, Guid.NewGuid(), "SysContentFolder", "EPiServer.Core.ContentFolder,EPiServer"),
        Type(3, Article, "ArticlePage", "Example.Web.Models.ArticlePage, Example.Web, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"),
        Type(4, Renamed, "OldNewsPage", "Example.Web.Models.OldNewsPage, Example.Web, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"),
        Type(5, Removed, "CampaignPage", "Example.Web.Models.CampaignPage, Example.Web, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", instances: 3),
        Type(6, Guid.NewGuid(), "FormContainerBlock", "Vendor.Forms.FormContainerBlock, Vendor.Forms"),
        Type(7, Guid.NewGuid(), "AdminMadePage", null),
    ];

    [Fact]
    public void The_source_scan_finds_types_of_the_sites_own_assemblies_whose_class_is_gone()
    {
        var orphaned = OrphanedTypes.FromSource(Types, CSharpSourceIndex.Build(_root.Path), ScheduledJobSources.Assemblies(_root.Path));

        // Not the CMS's or a package's (their classes aren't in the source), nor types made in admin mode.
        Assert.Equal(["CampaignPage"], orphaned.Select(t => t.Name));
    }

    [Fact]
    public void The_site_knows_every_class_it_cant_load_packages_included()
    {
        var site = new TypesWithoutCodeResult([new TypeWithoutCode(5, Removed, "CampaignPage", "x"), new TypeWithoutCode(6, Types[5].Guid, "FormContainerBlock", "y")]);

        Assert.Equal(["CampaignPage", "FormContainerBlock"], OrphanedTypes.FromSite(Types, site).Select(t => t.Name));
    }

    [Theory]
    [InlineData("Example.Web.Models.ArticlePage, Example.Web, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", "Example.Web")]
    [InlineData("EPiServer.Core.ContentFolder,EPiServer", "EPiServer")]
    [InlineData("Example.Generic`1[[System.String, System.Private.CoreLib]], Example.Web", "Example.Web")]
    [InlineData("Example.NoAssembly", null)]
    [InlineData(null, null)]
    public void The_assembly_is_the_part_after_the_type_name(string? modelType, string? assembly)
    {
        Assert.Equal(assembly, OrphanedTypes.Assembly(modelType));
    }
}
