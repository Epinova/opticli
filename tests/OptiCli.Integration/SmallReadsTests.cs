using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Queries;
using OptiCli.Core.Writes;

namespace OptiCli.Integration;

/// <summary>
/// <c>find --status scheduled|expired</c>, <c>history</c>, <c>categories</c> and <c>visitor-groups</c> on the edge-case
/// site, with scratch pages below its language root that each test deletes again.
/// </summary>
public sealed class SmallReadsTests
{
    [SiteFact]
    public async Task Scheduled_and_expired_content_is_found_with_when()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var type = site.Session.Model.Type(root.TypeId)!;
        var scheduled = (await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), type.Name, $"opticli-it scheduled {Guid.NewGuid():N}"), dryRun: false, cancellationToken)).CreatedId!.Value;
        var expired = (await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), type.Name, $"opticli-it expired {Guid.NewGuid():N}", Publish: true), dryRun: false, cancellationToken)).CreatedId!.Value;
        try
        {
            var at = DateTimeOffset.UtcNow.AddDays(30);
            await writes.RunAsync(new SetOperation(WriteOutput.Id(scheduled), Name: "scheduled") { PublishAt = at }, dryRun: false, cancellationToken);
            await writes.RunAsync(new UnpublishOperation(WriteOutput.Id(expired)), dryRun: false, cancellationToken);

            var find = new FindQuery(site.Session);
            var foundScheduled = await find.RunAsync(type, [], root.Id, FindStatus.Scheduled, null, 0, 1000, cancellationToken);
            var foundExpired = await find.RunAsync(type, [], root.Id, FindStatus.Expired, null, 0, 1000, cancellationToken);

            var item = Assert.Single(foundScheduled, f => f.Id == scheduled);
            Assert.InRange(item.PublishAt!.Value, at.UtcDateTime.AddSeconds(-1), at.UtcDateTime.AddSeconds(1));
            Assert.DoesNotContain(foundScheduled, f => f.Id == expired);
            Assert.True(Assert.Single(foundExpired, f => f.Id == expired).ExpiredAt <= DateTime.UtcNow);
            Assert.DoesNotContain(foundExpired, f => f.Id == scheduled);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(WriteOutput.Id(scheduled), IgnoreReferences: true), dryRun: false, cancellationToken);
            await writes.RunAsync(new DeleteOperation(WriteOutput.Id(expired), IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task History_names_publishes_moves_deletes_and_restores_newest_first()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var type = site.Session.Model.TypeName(root.TypeId);
        var folder = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), type, $"opticli-it history {Guid.NewGuid():N}"), dryRun: false, cancellationToken)).CreatedId!.Value);
        var page = (await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), type, "history page", Publish: true), dryRun: false, cancellationToken)).CreatedId!.Value;
        try
        {
            await writes.RunAsync(new MoveOperation(WriteOutput.Id(page), folder), dryRun: false, cancellationToken);
            await writes.RunAsync(new DeleteOperation(WriteOutput.Id(page)), dryRun: false, cancellationToken);
            await writes.RunAsync(new RestoreOperation(WriteOutput.Id(page)), dryRun: false, cancellationToken);

            var history = await new HistoryReader(site.Session).ListAsync(page, null, DateTime.UtcNow.AddMinutes(-5), null, 0, 50, cancellationToken);

            Assert.Equal(["restore", "delete", "move", "publish", "create"], history.Select(h => h.Action));
            var move = history[2];
            Assert.Equal((WriteOutput.Id(root.Id), folder), (move.From!.Ref, move.To!.Ref));
            Assert.Equal(folder, history[1].From!.Ref);
            Assert.Equal(folder, history[0].To!.Ref);
            Assert.Equal(("opticli", "en"), (history[3].By, history[3].Language));
            Assert.StartsWith($"{page}_", history[3].Version);
            Assert.Empty(await new HistoryReader(site.Session).ListAsync(page, null, null, "nobody-by-this-name", 0, 50, cancellationToken));
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(folder, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }

    [SiteFact]
    public async Task Categories_and_visitor_groups_are_listed_with_what_get_and_set_use()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is null)
        {
            return;
        }

        var groups = await new VisitorGroupReader(site.Session.Db).ListAsync(cancellationToken);
        var edge = Assert.Single(groups, g => g.Id == EdgeFixture.VisitorGroup);
        Assert.Equal(site.Session.Model.VisitorGroups[EdgeFixture.VisitorGroup], edge.Name);
        Assert.Equal(site.Session.Model.VisitorGroups.Keys.Order(), groups.Select(g => g.Id).Order());

        var categories = await new CategoryReader(site.Session.Db).ListAsync(cancellationToken);
        Assert.NotEmpty(categories);
        Assert.All(categories, c => Assert.Equal(c.Name, site.Session.Model.CategoryName(c.Id)));
        Assert.All(categories.Where(c => c.Parent is { } parent), c => Assert.Contains(categories, p => p.Id == c.Parent && c.Path.StartsWith(p.Path + "/", StringComparison.Ordinal)));
    }
}
