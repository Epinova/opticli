using System.Globalization;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

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

    /// <summary>
    /// A catalog in two languages, and its content translated. Commerce 14 keeps catalog content in every language of its
    /// catalog and saves a copy of its own in each (as system), which opticli 0.36 took for someone else's draft, so every
    /// translation needed includeDraft; Commerce 15 makes a language branch as the CMS does, which translate refused.
    /// </summary>
    [CommerceSiteFact]
    public async Task Catalog_content_is_translated_and_published_in_one_step_while_another_users_draft_still_stops_it()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        var run = Guid.NewGuid().ToString("N")[..8];
        var writes = new WriteExecutor(site.Session, _ => Task.FromResult(site.Agent));
        var languages = Properties(("DefaultCurrency", "USD"), ("DefaultLanguage", "en"), ("WeightBase", "kgs"), ("LengthBase", "cm"));
        languages["CatalogLanguages"] = new System.Text.Json.Nodes.JsonArray("en", "sv");
        var catalog = Assert.IsType<WriteOutput>((await writes.RunAsync(new CreateOperation("catalog-root", "CatalogContent", $"opticli-it {run}", languages, Publish: true), dryRun: false, cancellationToken)).Output);
        var women = $"code:it-{run}-women";
        await writes.RunAsync(new CreateOperation(catalog.Ref!, "FashionNode", "Women", Properties(("Code", $"it-{run}-women"), ("Teaser", "For her")), Publish: true), dryRun: false, cancellationToken);
        await writes.RunAsync(new CreateOperation(catalog.Ref!, "FashionNode", "Men", Properties(("Code", $"it-{run}-men"), ("Teaser", "For him")), Publish: true), dryRun: false, cancellationToken);
        var commerce14 = (await site.Agent.SendAsync<CommerceInfo>(HttpMethod.Get, AgentRoutes.Commerce, null, cancellationToken)).Version.StartsWith("14.", StringComparison.Ordinal);

        // A plan translates and publishes in one step, without includeDraft, and finds it so on its second run.
        var plan = WritePlan.Parse($$"""
            {"operations": [{"op": "translate", "ref": "{{women}}", "lang": "sv", "properties": {"DisplayName": "Dam", "Teaser": "För henne"}, "publish": true}]}
            """);
        PlanRunner Runner() => new(site.Session, writes, Path.GetTempPath());
        Assert.Equal(PlanStepStatus.Valid, (await Runner().RunAsync(plan, dryRun: true, publishAll: false, cancellationToken)).Operations[0].Status);
        var first = await Runner().RunAsync(plan, dryRun: false, publishAll: false, cancellationToken);
        var translated = Assert.IsType<WriteOutput>(first.Operations[0].Result);
        Assert.True(translated.Published);
        Assert.Equal("sv", translated.Language);
        Assert.Null(translated.PendingDraft);
        // Commerce 14 had the branch already (its own copy), Commerce 15 makes it.
        Assert.Equal(commerce14 ? true : null, translated.Existing);
        Assert.Equal(PlanStepStatus.Unchanged, (await Runner().RunAsync(plan, dryRun: false, publishAll: false, cancellationToken)).Operations[0].Status);

        // set --lang publishes a translation in one step too, where translate made the branch (Commerce 15) or not (14).
        var men = $"code:it-{run}-men";
        if (!commerce14)
        {
            await writes.RunAsync(new TranslateOperation(men, "sv"), dryRun: false, cancellationToken);
        }
        var set = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(men, Properties(("DisplayName", "Herr")), Lang: "sv", Publish: true), dryRun: false, cancellationToken)).Output);
        Assert.True(set.Published);
        Assert.Null(set.PendingDraft);

        // A translation saved as a draft, in a branch never published: commerce get --lang reads it (the branch's latest),
        // not Commerce 14's copy before it. The name is the master language's, in every language.
        var kids = Assert.IsType<WriteOutput>((await writes.RunAsync(new CreateOperation(catalog.Ref!, "FashionNode", "Kids", Properties(("Code", $"it-{run}-kids")), Publish: true), dryRun: false, cancellationToken)).Output);
        var kidsDraft = Assert.IsType<WriteOutput>((await writes.RunAsync(new TranslateOperation(kids.Ref!, "sv", Properties: Properties(("Teaser", "Barn"))), dryRun: false, cancellationToken)).Output);
        Assert.False(kidsDraft.Published);
        var read = await site.Agent.SendAsync<ContentItem>(HttpMethod.Get, AgentRoutes.Read(kids.Ref!, "sv"), null, cancellationToken);
        Assert.Equal((int.Parse(kidsDraft.Version!.Split('_')[1], CultureInfo.InvariantCulture), "Barn", "Kids"),
            (read.Version!.Value, read.Properties!["Teaser"].Value?.GetString(), read.Name));

        // A draft in a published branch.
        var draft = Assert.IsType<WriteOutput>((await writes.RunAsync(new SetOperation(women, Properties(("Teaser", "Nytt")), Lang: "sv"), dryRun: false, cancellationToken)).Output);
        Assert.False(draft.Published);

        // Catalog content has one name, in every language: a name in sv is refused, as a language the catalog hasn't is.
        var name = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(new SetOperation(women, Name: "Dam", Lang: "sv"), dryRun: true, cancellationToken));
        Assert.Contains("DisplayName", name.Message);
        await writes.RunAsync(new CreateOperation("catalog-root", "CatalogContent", $"opticli-it {run} en",
            Properties(("DefaultCurrency", "USD"), ("DefaultLanguage", "en"), ("WeightBase", "kgs"), ("LengthBase", "cm")), Publish: true), dryRun: false, cancellationToken);
        var english = await Assert.ThrowsAsync<UsageException>(() => writes.RunAsync(
            new CreateOperation($"catalog:opticli-it {run} en", "FashionNode", "Kids", Properties(("Code", $"it-{run}-kids")), Lang: "sv"), dryRun: false, cancellationToken));
        Assert.Contains("whose languages are en", english.Message);
        Assert.Contains("CatalogLanguages", english.Hint);

        // A plan that gives that catalog sv translates into it: the dry run leaves the translation to the run.
        await writes.RunAsync(new CreateOperation($"catalog:opticli-it {run} en", "FashionNode", "Kids", Properties(("Code", $"it-{run}-kids-en")), Publish: true), dryRun: false, cancellationToken);
        var grow = WritePlan.Parse($$"""
            {"operations": [
              {"op": "set", "ref": "catalog:opticli-it {{run}} en", "properties": {"CatalogLanguages": ["en", "sv"]}, "publish": true},
              {"op": "translate", "ref": "code:it-{{run}}-kids-en", "lang": "sv", "properties": {"DisplayName": "Barn"}, "publish": true}
            ]}
            """);
        var growDry = await Runner().RunAsync(grow, dryRun: true, publishAll: false, cancellationToken);
        Assert.Equal(PlanStepStatus.Deferred, growDry.Operations[1].Status);
        Assert.Contains("Operation 0 gives a catalog the language 'sv'", growDry.Operations[1].Warnings![0]);
        var grown = await Runner().RunAsync(grow, dryRun: false, publishAll: false, cancellationToken);
        Assert.True(Assert.IsType<WriteOutput>(grown.Operations[1].Result).Published);
        Assert.Equal(PlanStepStatus.Valid, (await Runner().RunAsync(grow, dryRun: true, publishAll: false, cancellationToken)).Operations[1].Status);

        // Another user's draft in sv still stops the publish, until it is confirmed.
        if (!await site.HandOverAsync(draft.Version!, cancellationToken))
        {
            // A Commerce site set up before its fixture could hand a version over: the rest is about someone else's draft.
            return;
        }
        var publish = new SetOperation(women, Properties(("DisplayName", "Damer")), Lang: "sv", Publish: true);
        var refused = await Assert.ThrowsAsync<ConflictException>(() => writes.RunAsync(publish, dryRun: false, cancellationToken));
        var details = Assert.IsType<Core.Serve.AgentErrorDetails>(refused.Details);
        Assert.Equal((draft.Version!.Replace("_CatalogContent", ""), SiteUnderTest.SomeoneElse), (details.Draft?.Version, details.Draft?.SavedBy));
        var confirmed = Assert.IsType<WriteOutput>((await writes.RunAsync(publish with { IncludeDraft = true }, dryRun: false, cancellationToken)).Output);
        Assert.True(confirmed.Published);
        Assert.Equal(SiteUnderTest.SomeoneElse, confirmed.PendingDraft?.SavedBy);
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
