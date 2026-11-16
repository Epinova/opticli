using System.Globalization;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Cms;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Projects;

/// <summary>Which of a project's items the refs of a ready or remove request name.</summary>
public class ProjectSelectionTests
{
    private static readonly FakeProjects Projects = new();

    private static readonly IReadOnlyList<LoadedItem> Items =
    [
        new(Projects.AddItem(1, new ContentReference(10, 100), "en"), null),
        new(Projects.AddItem(1, new ContentReference(10, 101), "sv"), null),
        new(Projects.AddItem(1, new ContentReference(11, 110), "en"), null),
        new(Projects.AddItem(1, new ContentReference(12, 120), null), null),
    ];

    private static IReadOnlyList<int> Select(string? language, params string[] refs) => ProjectSelection.Select(
            Items,
            refs.Select(r => (r, ContentReference.Parse(r))).ToList(),
            language is null ? null : CultureInfo.GetCultureInfo(language),
            "1 ('Launch', active)")
        .Select(i => i.Item.ContentLink.WorkID)
        .ToList();

    [Fact]
    public void A_content_ref_names_its_items_in_every_language() => Assert.Equal([100, 101], Select(null, "10"));

    [Fact]
    public void A_language_narrows_it() => Assert.Equal([101], Select("sv", "10"));

    [Fact]
    public void A_version_ref_names_that_version() => Assert.Equal([110], Select(null, "11_110"));

    [Fact]
    public void Content_without_a_language_is_named_without_one() => Assert.Equal([120], Select(null, "12"));

    [Fact]
    public void Items_come_once_in_the_projects_order() => Assert.Equal([100, 101, 110], Select(null, "11", "10", "10_100"));

    [Fact]
    public void Every_ref_that_names_no_item_is_listed_in_one_not_found()
    {
        var error = Assert.Throws<AgentException>(() => Select("en", "10", "13", "11_111", "12"));

        Assert.Equal(AgentErrorCodes.NotFound, error.Code);
        Assert.Equal("13, 11_111, 12 are not in project 1 ('Launch', active) in 'en'.", error.Message);
    }
}
