using OptiCli.Core.Content;
using OptiCli.Core.Queries;
using OptiCli.Core.Writes;

namespace OptiCli.Integration;

/// <summary>Plans whose rich text links to content they create, below the edge-case site's language root.</summary>
public sealed class PlanTextLinkTests
{
    [SiteFact]
    public async Task Rich_text_links_to_planned_content_are_stored_as_permanent_links()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (await EdgeFixture.FindAsync(site, EdgeFixture.LanguageRoot, cancellationToken) is not { } root)
        {
            return;
        }
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var parent = WriteOutput.Id(root.Id);
        var existing = WriteOutput.Id((await writes.RunAsync(new CreateOperation(parent, "StandardPage", $"opticli-it {Guid.NewGuid():N}"), dryRun: false, cancellationToken)).CreatedId!.Value);
        var plan = WritePlan.Parse($$$"""
            {"guidNamespace": "{{{Guid.NewGuid()}}}", "operations": [
              {"op": "create", "id": "a", "parent": "{{{parent}}}", "type": "StandardPage", "name": "opticli-it a", "properties": {"MainBody": "<p><a href=\"$b#top\">to B</a></p>"}},
              {"op": "create", "id": "b", "parent": "{{{parent}}}", "type": "StandardPage", "name": "opticli-it b", "properties": {"MainBody": "<p><a href=\"$a\">back to A</a></p>"}},
              {"op": "set", "ref": "{{{existing}}}", "properties": {"MainBody": "<p><a href=\"$b\">to B</a></p>"}}]}
            """);
        if (site.Session.Model.Schema.Major >= 13)
        {
            // CMS 13 refuses a link to content that doesn't exist yet: the plan says so before anything is saved.
            var refused = await Assert.ThrowsAsync<Core.Errors.UsageException>(() => new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken));
            Assert.Contains("operations[0]: the text links to '$b'", refused.Message);
            await writes.RunAsync(new DeleteOperation(existing, IgnoreReferences: true), dryRun: false, cancellationToken);
            return;
        }
        var created = new List<string>();
        try
        {
            var dry = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(plan, dryRun: true, publishAll: false, cancellationToken);
            Assert.Equal(PlanStepStatus.Simulated, dry.Operations[2].Status);

            var run = await new PlanRunner(site.Session, writes, Path.GetTempPath()).RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
            created.AddRange(run.Created.Values);
            var b = await ContentHeaderReader.ByIdAsync(site.Session.Db, int.Parse(run.Created["b"], System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
            Assert.Equal(plan.Steps[1].Operation.ContentGuid, b!.Guid);

            var usages = await new WhereUsedReader(site.Session).FindAsync(b, cancellationToken);
            Assert.Contains(usages, u => u.Ref == run.Created["a"] && u.Property == "MainBody" && u.Kind == "richTextLink");
            Assert.Contains(usages, u => u.Ref == existing && u.Property == "MainBody");
            var a = await new ContentLoader(site.Session.Db, new IdentityResolver(site.Session.Db, site.Session.Model))
                .GetAsync(int.Parse(run.Created["a"], System.Globalization.CultureInfo.InvariantCulture), new VersionSelector(VersionKind.Latest), null, new Core.Properties.DecodeOptions(Full: true), cancellationToken);
            Assert.Contains(TextRefs.PermanentLink(b.Guid) + "#top", a.Properties["MainBody"]!.ToJsonString());
        }
        finally
        {
            foreach (var id in created.Append(existing))
            {
                await writes.RunAsync(new DeleteOperation(id, IgnoreReferences: true), dryRun: false, cancellationToken);
            }
        }
    }
}
