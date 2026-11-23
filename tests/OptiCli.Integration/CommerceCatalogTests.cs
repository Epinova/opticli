using System.Globalization;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Integration;

/// <summary>
/// Catalog content through create, set and plans, on the Commerce test sites (tests/fixtures/commerce/setup.sh). Each test
/// makes its own catalog, with codes of its own, so runs don't meet; catalog content isn't deleted (opticli doesn't).
/// </summary>
public sealed class CommerceCatalogTests
{
    private static string Plan(string run, Guid guidNamespace) => $$$"""
        {
          "guidNamespace": "{{{guidNamespace}}}",
          "operations": [
            {"op": "create", "id": "catalog", "type": "CatalogContent", "parent": "catalog-root", "name": "opticli-it {{{run}}}", "properties": {"DefaultCurrency": "USD", "DefaultLanguage": "en", "WeightBase": "kgs", "LengthBase": "cm"}, "publish": true},
            {"op": "create", "id": "men", "type": "FashionNode", "parent": "$catalog", "name": "Men", "properties": {"Code": "it-{{{run}}}-men", "Teaser": "For him"}, "publish": true},
            {"op": "create", "id": "shirt", "type": "FashionProduct", "parent": "$men", "name": "Oxford shirt", "properties": {"Code": "it-{{{run}}}-shirt", "Brand": "Acme", "Description": "<p>A shirt</p>"}, "publish": true},
            {"op": "create", "id": "shirt-m", "type": "FashionVariant", "parent": "$men", "name": "Oxford shirt M", "properties": {"Code": "it-{{{run}}}-shirt-m", "Size": "M"}, "publish": true},
            {"op": "create", "id": "bundle", "type": "FashionBundle", "parent": "$men", "name": "Shirt bundle", "properties": {"Code": "it-{{{run}}}-bundle"}},
            {"op": "create", "id": "start", "type": "StartPage", "parent": "1", "name": "opticli-it {{{run}}}", "properties": {"Catalog": "$catalog", "FeaturedProduct": "$shirt"}, "publish": true}
          ]
        }
        """;

    [CommerceSiteFact]
    public async Task A_catalog_plan_runs_and_runs_again_without_changing_anything()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var run = Guid.NewGuid().ToString("N")[..8];
        var plan = WritePlan.Parse(Plan(run, Guid.NewGuid()));
        PlanRunner Runner() => new(site.Session, new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent), updateExisting: true), Path.GetTempPath());

        var dry = await Runner().RunAsync(plan, dryRun: true, publishAll: false, cancellationToken);
        Assert.All(dry.Operations, o => Assert.True(o.Status is PlanStepStatus.Valid or PlanStepStatus.Simulated or PlanStepStatus.Deferred, $"{o.Index}: {o.Status} {o.Error?.Message}"));

        var first = await Runner().RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
        Assert.All(first.Operations, o => Assert.Equal(PlanStepStatus.Saved, o.Status));
        Assert.Matches("^-[0-9]+__CatalogContent$", first.Created["catalog"]);
        Assert.Matches("^[0-9]+__CatalogContent$", first.Created["shirt"]);
        Assert.Matches("^[0-9]+$", first.Created["start"]);
        var bundle = Assert.IsType<WriteOutput>(first.Operations[4].Result);
        Assert.False(bundle.Published);
        Assert.EndsWith("_CatalogContent", bundle.Version);
        Assert.Contains("which opticli doesn't delete", first.Operations[2].Undo ?? "", StringComparison.Ordinal);

        // The CMS page points at the catalog and the product by their refs, which get prints and every command takes back.
        var page = await new ContentLoader(site.Session.Db, new IdentityResolver(site.Session.Db, site.Session.Model))
            .GetAsync(int.Parse(first.Created["start"], CultureInfo.InvariantCulture), new VersionSelector(VersionKind.Published, null), null, new Core.Properties.DecodeOptions(), cancellationToken);
        Assert.Equal(first.Created["catalog"], page.Properties["Catalog"]?["value"]?["ref"]?.GetValue<string>());
        Assert.Equal(first.Created["shirt"], page.Properties["FeaturedProduct"]?["value"]?["ref"]?.GetValue<string>());
        Assert.Null(page.Properties["FeaturedProduct"]?["value"]?["missing"]);

        // Again: every step finds its content as the plan wants it.
        var second = await Runner().RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
        Assert.All(second.Operations, o => Assert.Equal(PlanStepStatus.Unchanged, o.Status));
        Assert.Equal(first.Created, second.Created);
    }

    /// <summary>
    /// set takes catalog content by every ref: the ref a write or get prints (a catalog's id is negative), its GUID, its
    /// code. opticli 0.35 found none of them, and its agent looked up the version of catalog content as the CMS's.
    /// </summary>
    [CommerceSiteFact]
    public async Task Set_takes_catalog_content_by_its_ref_GUID_and_code_and_refuses_what_it_doesnt_do()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var run = Guid.NewGuid().ToString("N")[..8];
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var catalog = Assert.IsType<WriteOutput>((await writes.RunAsync(new CreateOperation("catalog-root", "CatalogContent", $"opticli-it {run}",
            Properties(("DefaultCurrency", "USD"), ("DefaultLanguage", "en"), ("WeightBase", "kgs"), ("LengthBase", "cm")), Publish: true), dryRun: false, cancellationToken)).Output);
        Assert.Matches("^-[0-9]+__CatalogContent$", catalog.Ref);
        var product = Assert.IsType<WriteOutput>((await writes.RunAsync(new CreateOperation($"catalog:opticli-it {run}", "FashionProduct", "Shirt",
            Properties(("Code", $"it-{run}-p"), ("Brand", "Acme"))), dryRun: false, cancellationToken)).Output);
        Assert.Equal(catalog.Ref, product.Parent);
        Assert.False(product.Published);

        var byRef = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(catalog.Ref!, Properties(("Owner", "it"))), dryRun: false, cancellationToken)).Output);
        Assert.True(byRef.Saved);
        Assert.Equal(catalog.Ref, byRef.Ref);
        var byGuid = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(product.Guid!.Value.ToString(), Properties(("Brand", "Acme 2"))), dryRun: false, cancellationToken)).Output);
        Assert.True(byGuid.Saved);
        Assert.Equal(product.Ref, byGuid.Ref);
        var byCode = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation($"code:it-{run}-p", Publish: true), dryRun: false, cancellationToken)).Output);
        Assert.True(byCode.Published);
        Assert.Equal("Acme 2", byCode.Changes.FirstOrDefault(c => c.Property == "Brand")?.After?.ToString() ?? "Acme 2");

        var publish = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new PublishOperation($"code:it-{run}-p"), dryRun: false, cancellationToken));
        Assert.Contains("catalog content", publish.Message);
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => writes.RunAsync(new SetOperation($"code:it-{run}-none", Name: "x"), dryRun: false, cancellationToken));
        Assert.Contains("No catalog content", missing.Message);
        var project = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new SetOperation(product.Ref!, Name: "x") { Project = "Spring" }, dryRun: false, cancellationToken));
        Assert.Contains("doesn't take a project", project.Message);
    }

    private static System.Text.Json.Nodes.JsonObject Properties(params (string Name, string Value)[] values)
    {
        var json = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, value) in values)
        {
            json[name] = value;
        }
        return json;
    }
}
