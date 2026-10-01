using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Integration;

/// <summary>translate --with-blocks and --remove, on scratch pages below the edge-case site's language root.</summary>
public sealed class LanguageBranchTests
{
    [SiteFact]
    public async Task A_translation_with_blocks_translates_the_for_this_page_blocks_and_a_branch_can_be_removed_again()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var page = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(root.Id), "StandardPage", $"opticli-it {Guid.NewGuid():N}", Publish: true), dryRun: false, cancellationToken)).CreatedId!.Value);
        try
        {
            var block = WriteOutput.Id((await writes.RunAsync(new BlockCreateOperation("EditorialBlock", "opticli-it block", For: page, Publish: true), dryRun: false, cancellationToken)).CreatedId!.Value);

            var translate = new TranslateOperation(page, "sv", "opticli-it sv", Publish: true) { WithBlocks = true };
            var dry = Assert.IsType<WriteOutput>((await writes.RunAsync(translate, dryRun: true, cancellationToken)).Output);
            Assert.Equal((block, "translated"), (Assert.Single(dry.Blocks!).Ref, dry.Blocks![0].Status));
            var saved = await writes.RunAsync(translate, dryRun: false, cancellationToken);
            var output = Assert.IsType<WriteOutput>(saved.Output);
            Assert.NotNull(Assert.Single(output.Blocks!).Version);
            var blockHeader = await ContentHeaderReader.ByIdAsync(site.Session.Db, int.Parse(block, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            Assert.Contains(site.Session.Model.LanguageByCode("sv")!.Id, blockHeader!.Languages.Keys);
            Assert.Contains($"opticli translate {page} --lang sv --remove --confirm", UndoHints.For(translate, output));

            // Removing needs confirming, never the master language.
            var remove = new TranslateOperation(page, "sv") { Remove = true };
            var plan = Assert.IsType<RemoveLanguageOutput>((await writes.RunAsync(remove, dryRun: true, cancellationToken)).Output);
            Assert.Equal((true, false), (plan.Published, plan.Removed));
            var stopped = await Assert.ThrowsAsync<ConflictException>(() => writes.RunAsync(remove, dryRun: false, cancellationToken));
            Assert.Contains("--confirm", stopped.Hint);
            await Assert.ThrowsAsync<RefusedException>(() => writes.RunAsync(new TranslateOperation(page, "en") { Remove = true, Confirm = true }, dryRun: true, cancellationToken));

            var removed = Assert.IsType<RemoveLanguageOutput>((await writes.RunAsync(remove with { Confirm = true }, dryRun: false, cancellationToken)).Output);
            Assert.True(removed.Removed);
            var header = await ContentHeaderReader.ByIdAsync(site.Session.Db, int.Parse(page, System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            Assert.DoesNotContain(site.Session.Model.LanguageByCode("sv")!.Id, header!.Languages.Keys);
        }
        finally
        {
            await writes.RunAsync(new DeleteOperation(page, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }
}
