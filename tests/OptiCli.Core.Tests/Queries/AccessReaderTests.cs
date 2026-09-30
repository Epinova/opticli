using OptiCli.Core.Queries;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Queries;

public class AccessReaderTests
{
    private static AccessEntry Role(string name, int mask) => new(name, AccessKinds.Role, AccessLevels.Describe(mask), mask);

    private static ILookup<int, AccessEntry> Stored(params (int Id, AccessEntry Entry)[] rows) => rows.ToLookup(r => r.Id, r => r.Entry);

    [Fact]
    public void An_item_with_its_own_entries_is_not_inherited()
    {
        var access = AccessReader.Effective(200, [1], Stored((200, Role("Everyone", 1)), (1, Role("WebEditors", 63))));

        Assert.False(access.Inherited);
        Assert.Equal("200", access.From);
        Assert.Equal("Everyone", Assert.Single(access.Entries).Name);
    }

    [Fact]
    public void An_item_without_entries_inherits_from_the_nearest_ancestor_that_has_some()
    {
        var access = AccessReader.Effective(220, [1, 200, 210], Stored((1, Role("Root", 63)), (200, Role("Everyone", 1)), (200, Role("Administrators", 63))));

        Assert.True(access.Inherited);
        Assert.Equal("200", access.From);
        Assert.Equal(["Administrators", "Everyone"], access.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Nothing_stored_anywhere_is_inherited_from_nowhere()
    {
        var access = AccessReader.Effective(5, [1], Stored());

        Assert.True(access.Inherited);
        Assert.Null(access.From);
        Assert.Empty(access.Entries);
    }
}
