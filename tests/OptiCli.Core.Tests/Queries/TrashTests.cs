using OptiCli.Core.Data;
using OptiCli.Core.Queries;

namespace OptiCli.Core.Tests.Queries;

public class TrashTests
{
    [Theory]
    [InlineData("123", 123)]
    [InlineData(" 123 ", 123)]
    [InlineData("123_456", 123)]
    [InlineData("63__provider", null)]
    [InlineData("-1", null)]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_stored_parent_is_a_plain_content_reference(string? stored, int? id)
    {
        Assert.Equal(id, RestoreParents.Id(stored));
    }

    /// <summary>The start of the view the CMS generates for the store (its other columns left out).</summary>
    private const string View = """
        create view [dbo].[VW_EPiParentRestoreStore] as select
        CAST (R01.pkId as varchar(50)) + ':' + UPPER(CAST([Identity].Guid as varchar(50))) as [Id], R01.pkId as [StoreId], [Identity].Guid as [ExternalId], R01.ItemType as [ItemType],
        R01.String01 as "ParentLink",
        R01.String02 as "SourceLink"
        from [tblSystemBigTable] as R01
        """;

    [Fact]
    public void The_stores_view_says_which_column_holds_each_property()
    {
        Assert.Equal("String01", DynamicDataStore.ViewColumn(View, "ParentLink"));
        Assert.Equal("String02", DynamicDataStore.ViewColumn(View, "SourceLink"));
        Assert.Equal("Indexed_String02", DynamicDataStore.ViewColumn("R01.[Indexed_String02] as [SourceLink]", "SourceLink"));
        Assert.Null(DynamicDataStore.ViewColumn(View, "Other"));
        Assert.Null(DynamicDataStore.ViewColumn(null, "ParentLink"));
    }
}
