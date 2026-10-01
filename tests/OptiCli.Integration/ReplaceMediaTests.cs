using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Integration;

/// <summary>upload --replace, on a scratch PDF in a scratch folder.</summary>
public sealed class ReplaceMediaTests
{
    [SiteFact]
    public async Task Replacing_a_file_saves_a_new_version_of_the_same_type_with_its_own_blob()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), projectDirectory: site.ProjectDirectory);
        var parent = site.Session.Model.Sites.GlobalAssetsRoot ?? throw new InvalidOperationException("The site has no global assets folder.");
        var folder = WriteOutput.Id((await writes.RunAsync(new CreateOperation(WriteOutput.Id(parent), "SysContentFolder", $"opticli-it {Guid.NewGuid():N}"), dryRun: false, cancellationToken)).CreatedId!.Value);
        var pdf = Path.Combine(Path.GetTempPath(), $"opticli-it-{Guid.NewGuid():N}.pdf");
        var text = Path.ChangeExtension(pdf, ".txt");
        await File.WriteAllBytesAsync(pdf, WriteRoundTripTests.MinimalPdf(), cancellationToken);
        await File.WriteAllTextAsync(text, "not a pdf", cancellationToken);
        try
        {
            var uploaded = Assert.IsType<WriteOutput>((await writes.RunAsync(new UploadOperation(pdf, Parent: folder, Publish: true), dryRun: false, cancellationToken)).Output);
            var replace = new UploadOperation(pdf) { Replace = uploaded.Ref };

            var dry = await writes.RunAsync(replace, dryRun: true, cancellationToken);
            Assert.Contains(dry.Warnings, w => w.Contains("would be replaced", StringComparison.Ordinal));
            await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new UploadOperation(text) { Replace = uploaded.Ref }, dryRun: true, cancellationToken));
            await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new UploadOperation(pdf) { Replace = folder }, dryRun: true, cancellationToken));

            var replaced = Assert.IsType<WriteOutput>((await writes.RunAsync(replace with { Publish = true }, dryRun: false, cancellationToken)).Output);
            Assert.True(replaced.Published);
            Assert.Equal((uploaded.Ref, uploaded.Type), (replaced.Ref, replaced.Type));
            Assert.NotEqual(uploaded.Upload!.Blob!.Path, replaced.Upload!.Blob!.Path);
            Assert.Contains($"--version {uploaded.Version!.Split('_')[1]}", UndoHints.For(replace with { Publish = true }, replaced));
        }
        finally
        {
            File.Delete(pdf);
            File.Delete(text);
            await writes.RunAsync(new DeleteOperation(folder, IgnoreReferences: true), dryRun: false, cancellationToken);
        }
    }
}
