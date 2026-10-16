using OptiCli.Core.Queries;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Queries;

public class AccessReaderTests
{
    private static AccessEntry Role(string name, int mask) => new(name, AccessKinds.Role, AccessLevels.Describe(mask), mask);

    private static ILookup<int, AccessEntry> Stored(params (int Id, AccessEntry Entry)[] rows) => rows.ToLookup(r => r.Id, r => r.Entry);

    private static AccessReader.AccessChain Chain(int id, IReadOnlyList<int> ancestorIds, params AccessReader.AssetFolder[] folders) =>
        AccessReader.ChainAsync(id, ancestorIds, ids => Task.FromResult<IReadOnlyDictionary<int, AccessReader.AssetFolder>>(
            folders.Where(f => ids.Contains(f.Id)).ToDictionary(f => f.Id))).GetAwaiter().GetResult();

    [Fact]
    public void An_item_with_its_own_entries_is_not_inherited()
    {
        var access = AccessReader.Effective(200, Chain(200, [1]), Stored((200, Role("Everyone", 1)), (1, Role("WebEditors", 63))));

        Assert.False(access.Inherited);
        Assert.Equal("200", access.From);
        Assert.Equal("Everyone", Assert.Single(access.Entries).Name);
        Assert.Null(access.AssetsOf);
    }

    [Fact]
    public void An_item_without_entries_inherits_from_the_nearest_ancestor_that_has_some()
    {
        var access = AccessReader.Effective(220, Chain(220, [1, 200, 210]), Stored((1, Role("Root", 63)), (200, Role("Everyone", 1)), (200, Role("Administrators", 63))));

        Assert.True(access.Inherited);
        Assert.Equal("200", access.From);
        Assert.Equal(["Administrators", "Everyone"], access.Entries.Select(e => e.Name));
    }

    [Fact]
    public void Nothing_stored_anywhere_is_inherited_from_nowhere()
    {
        var access = AccessReader.Effective(5, Chain(5, [1]), Stored());

        Assert.True(access.Inherited);
        Assert.Null(access.From);
        Assert.Empty(access.Entries);
    }

    // 1 root > 3 content assets > 40 "For this page" folder of page 20 (1 > 10 > 20) > 41 subfolder > 42 block.
    private static readonly AccessReader.AssetFolder PagesFolder = new(40, 20, [1, 10]);

    [Fact]
    public void Content_in_a_for_this_page_folder_follows_the_owner_not_the_asset_root()
    {
        var chain = Chain(42, [1, 3, 40, 41], PagesFolder);

        Assert.Equal([42, 41, 20, 10, 1], chain.Candidates);
        var access = AccessReader.Effective(42, chain, Stored((1, Role("Root", 63)), (40, Role("Folder", 63)), (10, Role("Section", 1))));
        Assert.Equal((true, "10", "20"), (access.Inherited, access.From, access.AssetsOf));
        Assert.Equal("Section", Assert.Single(access.Entries).Name);
    }

    [Fact]
    public void The_folder_itself_has_its_owners_access_rights()
    {
        var access = AccessReader.Effective(40, Chain(40, [1, 3], PagesFolder), Stored((40, Role("Folder", 63)), (20, Role("Page", 1))));

        Assert.Equal((true, "20", "20"), (access.Inherited, access.From, access.AssetsOf));
    }

    [Fact]
    public void Own_entries_below_the_folder_still_come_first()
    {
        var access = AccessReader.Effective(42, Chain(42, [1, 3, 40, 41], PagesFolder), Stored((41, Role("Sub", 1)), (20, Role("Page", 1))));

        Assert.Equal((true, "41", "20"), (access.Inherited, access.From, access.AssetsOf));
    }

    [Fact]
    public void A_folder_whose_owner_is_gone_falls_back_to_its_parent()
    {
        var chain = Chain(42, [1, 3, 40], new AccessReader.AssetFolder(40, null, []));

        Assert.Equal([42, 3, 1], chain.Candidates);
        Assert.Null(chain.AssetsOf);
    }

    [Fact]
    public void An_owner_in_another_for_this_page_folder_is_followed_too()
    {
        // Block 42 is in block 30's folder 40; block 30 is in page 20's folder 50.
        var chain = Chain(42, [1, 3, 40], new AccessReader.AssetFolder(40, 30, [1, 3, 50]), new AccessReader.AssetFolder(50, 20, [1, 10]));

        Assert.Equal([42, 30, 20, 10, 1], chain.Candidates);
        Assert.Equal(30, chain.AssetsOf);
    }
}
