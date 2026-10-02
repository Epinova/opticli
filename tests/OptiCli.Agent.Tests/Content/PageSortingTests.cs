using System.Text.Json;
using OptiCli.Cms;
using OptiCli.Cms.Content;

namespace OptiCli.Agent.Tests.Content;

/// <summary>Child orders are the numbers of EPiServer.Filters.FilterSortOrder (PublishedDescending 8, Index 4, Alphabetical 3).</summary>
public class PageSortingTests
{
    [Theory]
    [InlineData("\"PublishedDescending\"", 8)]
    [InlineData("\" publisheddescending \"", 8)]
    [InlineData("\"Index\"", 4)]
    [InlineData("\"8\"", 8)]
    [InlineData("3", 3)]
    public void Child_sort_order_takes_a_name_or_number(string json, int expected) =>
        Assert.Equal(expected, PageSorting.ParseChildSortOrder(JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("\"None\"")]
    [InlineData("0")]
    [InlineData("\"Rank\"")]
    [InlineData("42")]
    [InlineData("\"Newest\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Child_sort_order_rejects_orders_edit_mode_does_not_offer(string json)
    {
        var error = Assert.Throws<AgentException>(() => PageSorting.ParseChildSortOrder(JsonDocument.Parse(json).RootElement));
        Assert.Contains("PublishedDescending", error.Message);
        Assert.DoesNotContain("Rank", error.Message);
    }

    [Theory]
    [InlineData("200", 200)]
    [InlineData("\"150\"", 150)]
    [InlineData("\"-5\"", -5)]
    public void Sort_index_takes_a_whole_number(string json, int expected) =>
        Assert.Equal(expected, PageSorting.ParseSortIndex(JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("1.5")]
    [InlineData("\"first\"")]
    [InlineData("null")]
    public void Sort_index_rejects_anything_else(string json) =>
        Assert.Throws<AgentException>(() => PageSorting.ParseSortIndex(JsonDocument.Parse(json).RootElement));
}
