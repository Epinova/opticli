using System.Net;
using OptiCli.Core;
using OptiCli.Core.Cms;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.SourceScan;
using OptiCli.Protocol;

namespace OptiCli.Integration;

/// <summary>
/// <c>types --orphaned</c>, <c>type</c>'s stored values, and <c>types remove|remove-property|prune</c> on the edge-case
/// site, whose OrphansFixture.cs leaves what removed code leaves behind: page types without their class (one used by a
/// page, one whose page is in the recycle bin, one used by nothing whose property has an orphaned block type), EdgePage
/// properties its class doesn't have (one with a value, one without), and a type made in admin mode. The tests that
/// remove something make the fixture again (POST /opticli-fixture/orphans), so the suite can run again.
/// </summary>
public sealed class OrphansTests
{
    private static readonly Guid RemovedType = Guid.Parse("3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A10");

    private static readonly Guid AdminType = Guid.Parse("3B0C4A5E-0D1F-4E5A-9C7B-6A1D2E3F4A14");

    private static readonly string[] OrphanedTypes = ["EdgeRemovedBlock", "EdgeRemovedEmptyPage", "EdgeRemovedPage", "EdgeTrashedPage"];

    [SiteFact]
    public async Task A_type_whose_class_is_gone_is_found_by_the_site_and_by_the_source_scan_alike()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (!await ReseedAsync(site, cancellationToken))
        {
            return;
        }
        var types = await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken, countInstances: true);
        var cms13 = site.Session.Model.Schema.Major >= 13;

        var fromSite = Core.Cms.OrphanedTypes.FromSite(types, await site.Agent.SendAsync<TypesWithoutCodeResult>(HttpMethod.Get, AgentRoutes.TypesWithoutCode, null, cancellationToken));
        var fromSource = Core.Cms.OrphanedTypes.FromSource(types, CSharpSourceIndex.Build(site.ProjectDirectory), ScheduledJobSources.Assemblies(site.ProjectDirectory));

        // CMS 13 records neither a class nor a version for a type made in admin mode: EdgeAdminPage is listed as of unknown
        // origin there (it could be a code type a content import overwrote).
        Assert.Equal(cms13 ? ["EdgeAdminPage", .. OrphanedTypes] : OrphanedTypes, fromSite.Select(t => t.Name));
        Assert.Equal(cms13 ? ["EdgeAdminPage"] : [], fromSite.Where(t => t.OriginUnknown == true).Select(t => t.Name));
        Assert.Equal(1, fromSite.Single(t => t.Guid == RemovedType).Instances);
        // The source scan needs the class on record, which CMS 13 doesn't keep for a type with a GUID (EdgeTrashedPage there):
        // there the GUIDs of the build output's classes stand in for it.
        Assert.Equal(fromSite.Where(t => t.ModelType is not null).Select(t => t.Name), fromSource.Select(t => t.Name));
        Assert.Equal(cms13 ? ["EdgeAdminPage", "EdgeTrashedPage"] : [], fromSite.Where(t => t.ModelType is null).Select(t => t.Name));
        if (cms13)
        {
            var project = Core.Discovery.ProjectLocator.Locate(site.ProjectDirectory, site.ProjectDirectory);
            var output = Path.GetDirectoryName(OutputLocator.Locate(project, null, null, site.ProjectDirectory).Dll)!;
            var withoutClass = Core.Cms.OrphanedTypes.WithoutClassOnRecord(types, Core.Drift.BuildScanner.ContentTypeGuids(output), CSharpSourceIndex.Build(site.ProjectDirectory));
            Assert.Equal(fromSite.Where(t => t.ModelType is null).Select(t => (t.Name, t.OriginUnknown)), withoutClass.Select(t => (t.Name, t.OriginUnknown)));
            // The template's content import left the Alloy types without a class and a version too, but their classes are in
            // the build: they are neither orphaned nor of unknown origin.
            var article = types.Single(t => t.Name == "ArticlePage");
            Assert.Equal((null, null), (article.ModelType, article.SyncedVersion));
            Assert.DoesNotContain(fromSite, t => t.Name == "ArticlePage");
        }
    }

    [SiteFact]
    public async Task On_cms_13_a_type_of_unknown_origin_goes_only_with_the_flag()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (site.Session.Model.Schema.Major < 13 || !await ReseedAsync(site, cancellationToken))
        {
            // CMS 12 records the class of every code type: a type without one was made in admin mode (the test above).
            return;
        }
        var refused = await Assert.ThrowsAsync<RefusedException>(() => RemoveAsync(site, new OrphanRemovalRequest { Types = ["EdgeAdminPage"], DryRun = true }, cancellationToken));
        Assert.Contains("origin unknown", refused.Message);
        Assert.Contains(OrphanRemoval.IncludeUnknownOriginFlag, refused.Message);

        var (pruned, _) = await RemoveAsync(site, new OrphanRemovalRequest { Prune = true, DryRun = true }, cancellationToken);
        Assert.Contains(pruned.Kept!, k => (k.Type, k.Property, k.Code) == ("EdgeAdminPage", null, AgentErrorCodes.Refused) && k.Reason.Contains(OrphanRemoval.IncludeUnknownOriginFlag, StringComparison.Ordinal));
        Assert.DoesNotContain(pruned.Types, t => t.Name == "EdgeAdminPage");

        var (dry, _) = await RemoveAsync(site, new OrphanRemovalRequest { Prune = true, DryRun = true, IncludeUnknownOrigin = true }, cancellationToken);
        Assert.Contains(dry.Types, t => t is { Name: "EdgeAdminPage", OriginUnknown: true });
        try
        {
            var (removed, warnings) = await RemoveAsync(site, new OrphanRemovalRequest { Types = ["EdgeAdminPage"], IncludeUnknownOrigin = true }, cancellationToken);

            var record = Assert.Single(removed.Types);
            Assert.Equal(("EdgeAdminPage", AdminType, (bool?)true), (record.Name, record.Guid, record.OriginUnknown));
            Assert.Contains(OrphanRemoval.NoUndo, warnings);
            Assert.DoesNotContain(await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken), t => t.Name == "EdgeAdminPage");
        }
        finally
        {
            await ReseedAsync(site, cancellationToken);
        }
        Assert.Contains(await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken), t => t.Name == "EdgeAdminPage");
    }

    [SiteFact]
    public async Task A_property_that_isnt_in_its_class_has_its_stored_values_counted()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (!await ReseedAsync(site, cancellationToken))
        {
            return;
        }
        var types = await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken);
        var edgePage = types.Single(t => t.Name == "EdgePage");

        var properties = await ContentTypeReader.ListPropertiesAsync(site.Session.Db, edgePage.Id, cancellationToken);
        var values = await ContentTypeReader.OrphanValuesAsync(site.Session.Db, properties, cancellationToken);

        Assert.Equal(["EdgeRemovedEmptyText", "EdgeRemovedText"], properties.Where(p => !p.ExistsOnModel).Select(p => p.Name).Order());
        Assert.Equal((1, 1), values[properties.Single(p => p.Name == "EdgeRemovedText").Id]);
        Assert.Equal((0, 0), values[properties.Single(p => p.Name == "EdgeRemovedEmptyText").Id]);
    }

    [SiteFact]
    public async Task Content_keeps_an_orphaned_type_also_when_it_is_only_in_the_recycle_bin()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (!await ReseedAsync(site, cancellationToken))
        {
            return;
        }

        // Not a dry run: the site refuses before anything is removed.
        var error = await Assert.ThrowsAsync<ConflictException>(() => RemoveAsync(site, new OrphanRemovalRequest { Types = ["EdgeRemovedPage", "EdgeTrashedPage"] }, cancellationToken));

        var details = Assert.IsType<AgentErrorDetails>(error.Details);
        Assert.Equal(AgentErrorReasons.Orphans, details.Reason);
        Assert.Equal(
            [("EdgeRemovedPage", "In use: 1 content item."), ("EdgeTrashedPage", "In use: 1 content item in the recycle bin.")],
            details.Validation!.Select(v => (v.Property, v.Message)));
        Assert.Contains("trash --type", error.Hint);
        Assert.Equal(OrphanedTypes, await OrphanedNamesAsync(site, cancellationToken));
    }

    [SiteFact]
    public async Task A_type_made_in_admin_mode_or_with_a_class_is_refused()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (!await ReseedAsync(site, cancellationToken))
        {
            return;
        }

        var error = await Assert.ThrowsAsync<RefusedException>(() => RemoveAsync(site, new OrphanRemovalRequest { Types = [AdminType.ToString(), "EdgePage"] }, cancellationToken));

        var validation = Assert.IsType<AgentErrorDetails>(error.Details).Validation!;
        // CMS 13 can't tell it from a code type a content import overwrote: of unknown origin, removed only with the flag.
        Assert.Contains(site.Session.Model.Schema.Major >= 13 ? "origin unknown" : "made in admin mode", validation.Single(v => v.Property == "EdgeAdminPage").Message);
        Assert.Contains("has a class the site loads", validation.Single(v => v.Property == "EdgePage").Message);
    }

    [SiteFact]
    public async Task A_property_with_values_is_refused_without_the_flag_and_removed_with_it()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (!await ReseedAsync(site, cancellationToken))
        {
            return;
        }
        var request = new OrphanRemovalRequest { Properties = [new OrphanPropertyRef("EdgePage", "EdgeRemovedText")] };
        try
        {
            var refused = await Assert.ThrowsAsync<RefusedException>(() => RemoveAsync(site, request with { DryRun = true }, cancellationToken));
            Assert.Contains("It has stored values (1 content item, 1 version)", refused.Message);

            var (removed, warnings) = await RemoveAsync(site, request with { AllowDestructive = true }, cancellationToken);

            var property = Assert.Single(removed.Properties);
            Assert.Equal(("EdgePage", "EdgeRemovedText", "String", new StoredValueCounts(1, 1)), (property.Type, property.Property.Name, property.Property.DataType, property.Values));
            Assert.True(removed.Removed);
            Assert.Contains(OrphanRemoval.NoUndo, warnings);
            Assert.DoesNotContain("EdgeRemovedText", await NotInCodeAsync(site, cancellationToken));
        }
        finally
        {
            await ReseedAsync(site, cancellationToken);
        }
        Assert.Contains("EdgeRemovedText", await NotInCodeAsync(site, cancellationToken));
    }

    [SiteFact]
    public async Task A_property_without_values_needs_no_flag()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (!await ReseedAsync(site, cancellationToken))
        {
            return;
        }
        try
        {
            var (removed, _) = await RemoveAsync(site, new OrphanRemovalRequest { Properties = [new OrphanPropertyRef("EdgePage", "EdgeRemovedEmptyText")] }, cancellationToken);

            Assert.Equal(new StoredValueCounts(0, 0), Assert.Single(removed.Properties).Values);
            Assert.DoesNotContain("EdgeRemovedEmptyText", await NotInCodeAsync(site, cancellationToken));
        }
        finally
        {
            await ReseedAsync(site, cancellationToken);
        }
    }

    [SiteFact]
    public async Task Prune_removes_the_empty_orphans_block_type_last_and_keeps_the_rest()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (!await ReseedAsync(site, cancellationToken))
        {
            return;
        }
        try
        {
            var blocked = await Assert.ThrowsAsync<ConflictException>(() => RemoveAsync(site, new OrphanRemovalRequest { Types = ["EdgeRemovedBlock"], DryRun = true }, cancellationToken));
            Assert.Contains("block type of EdgeRemovedEmptyPage.Teaser", blocked.Message);

            var (dry, _) = await RemoveAsync(site, new OrphanRemovalRequest { Prune = true, DryRun = true }, cancellationToken);
            Assert.Equal(["EdgeRemovedEmptyPage", "EdgeRemovedBlock"], dry.Types.Select(t => t.Name));
            Assert.Equal(("Teaser", "EdgeRemovedBlock"), (dry.Types[0].Properties.Single().Name, dry.Types[0].Properties.Single().BlockType));
            Assert.Equal(OrphanedTypes, await OrphanedNamesAsync(site, cancellationToken));
            Assert.Contains(dry.Kept!, k => (k.Type, k.Property, k.Code) == ("EdgeRemovedPage", null, AgentErrorCodes.Conflict));
            Assert.Contains(dry.Kept!, k => (k.Type, k.Property, k.Code) == ("EdgeTrashedPage", null, AgentErrorCodes.Conflict));
            Assert.Contains(dry.Kept!, k => (k.Type, k.Property, k.Code) == ("EdgePage", "EdgeRemovedText", AgentErrorCodes.Refused));
            Assert.DoesNotContain(dry.Kept!, k => k.Type is "EdgePage" && k.Property is null);
            // CMS 13: kept, saying what --include-unknown-origin does; CMS 12 knows it was made in admin mode.
            Assert.Equal(site.Session.Model.Schema.Major >= 13, dry.Kept!.Any(k => k is { Type: "EdgeAdminPage", Property: null }));

            var (pruned, _) = await RemoveAsync(site, new OrphanRemovalRequest { Prune = true }, cancellationToken);

            Assert.True(pruned.Removed);
            Assert.Equal(["EdgeRemovedEmptyPage", "EdgeRemovedBlock"], pruned.Types.Select(t => t.Name));
            Assert.Equal(["EdgeRemovedPage", "EdgeTrashedPage"], await OrphanedNamesAsync(site, cancellationToken));
        }
        finally
        {
            await ReseedAsync(site, cancellationToken);
        }
        Assert.Equal(OrphanedTypes, await OrphanedNamesAsync(site, cancellationToken));
    }

    [SiteFact]
    public async Task A_type_that_page_type_values_name_is_refused_with_the_versions_holding_them()
    {
        var cancellationToken = CancellationToken.None;
        await using var site = await SiteUnderTest.ConnectAsync(cancellationToken);
        if (!await ReseedAsync(site, cancellationToken))
        {
            return;
        }
        var types = await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken);
        var empty = types.Single(t => t.Name == "EdgeRemovedEmptyPage");
        // Alloy's page list block filters by page type: a value naming the type, in a draft discarded afterwards.
        var blocks = await site.Session.Db.QueryAsync("""
            SELECT TOP 1 c.pkID FROM tblContent c INNER JOIN tblContentType ct ON ct.pkID = c.fkContentTypeID
            WHERE ct.Name = 'PageListBlock' AND c.Deleted = 0 ORDER BY c.pkID
            """, r => r.GetInt32("pkID"), cancellationToken);
        Assert.NotEmpty(blocks);
        var draft = await site.Agent.SendAsync<WriteResult>(HttpMethod.Post, AgentRoutes.Draft(blocks[0].ToString(System.Globalization.CultureInfo.InvariantCulture)), new DraftRequest
        {
            Properties = new Dictionary<string, System.Text.Json.JsonElement> { ["PageTypeFilter"] = System.Text.Json.JsonSerializer.SerializeToElement(empty.Id) },
        }, cancellationToken);
        var version = draft.Content!;
        try
        {
            var error = await Assert.ThrowsAsync<ConflictException>(() =>
                RemoveAsync(site, new OrphanRemovalRequest { Types = ["EdgeRemovedEmptyPage", "EdgeRemovedBlock"], DryRun = true }, cancellationToken));

            var reason = Assert.IsType<AgentErrorDetails>(error.Details).Validation!.Single(v => v.Property == "EdgeRemovedEmptyPage").Message;
            Assert.Contains($"1 page-type property value naming it (in {version.Ref})", reason);
            Assert.Equal(OrphansPlannerHint, error.Hint);
        }
        finally
        {
            await site.Agent.SendAsync<WriteResult>(HttpMethod.Post, AgentRoutes.Discard(version.Ref), new DiscardRequest(), cancellationToken);
        }
        await RemoveAsync(site, new OrphanRemovalRequest { Types = ["EdgeRemovedEmptyPage", "EdgeRemovedBlock"], DryRun = true }, cancellationToken);
    }

    /// <summary>The agent's hint for page-type values (OrphanPlanner.PageTypeHint), as the CLI passes it on.</summary>
    private const string OrphansPlannerHint =
        "Page-type property values name it (a page list's type filter, say): the CMS would clear them in every version when it removes the type. `opticli get <ref>` shows the versions listed; change or clear those values first (or discard those drafts), then run it again.";

    private static async Task<(OrphanRemover.Output Output, IReadOnlyList<string> Warnings)> RemoveAsync(SiteUnderTest site, OrphanRemovalRequest request, CancellationToken cancellationToken) =>
        await OrphanRemover.RunAsync(site.Agent, request, cancellationToken);

    /// <summary><c>types --orphaned</c> as the site answers it, without CMS 13's types of unknown origin.</summary>
    private static async Task<IReadOnlyList<string>> OrphanedNamesAsync(SiteUnderTest site, CancellationToken cancellationToken)
    {
        var types = await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken);
        var fromSite = await site.Agent.SendAsync<TypesWithoutCodeResult>(HttpMethod.Get, AgentRoutes.TypesWithoutCode, null, cancellationToken);
        return Core.Cms.OrphanedTypes.FromSite(types, fromSite).Where(t => t.OriginUnknown != true).Select(t => t.Name).ToList();
    }

    /// <summary>EdgePage's properties that aren't in its class, from the database.</summary>
    private static async Task<IReadOnlyList<string>> NotInCodeAsync(SiteUnderTest site, CancellationToken cancellationToken)
    {
        var edgePage = (await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken)).Single(t => t.Name == "EdgePage");
        return (await ContentTypeReader.ListPropertiesAsync(site.Session.Db, edgePage.Id, cancellationToken)).Where(p => !p.ExistsOnModel).Select(p => p.Name).ToList();
    }

    /// <summary>Makes whatever of OrphansFixture.cs is missing, through the site.</summary>
    /// <returns>False on a site that isn't the edge-case site (no EdgePage); on the edge-case site the fixture must answer.</returns>
    private static async Task<bool> ReseedAsync(SiteUnderTest site, CancellationToken cancellationToken)
    {
        if (!(await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken)).Any(t => t.Name == "EdgePage"))
        {
            return false;
        }
        var state = StateStore.For(OptiCliEnvironment.FromProcess(), site.ProjectDirectory).Read() ?? throw new InvalidOperationException("opticli serve isn't running.");
        using var http = new HttpClient { BaseAddress = state.BaseUrl };
        using var response = await http.PostAsync("opticli-fixture/orphans", null, cancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
            $"POST /opticli-fixture/orphans answered {(int)response.StatusCode}: the edge-case site's OrphansFixture.cs is missing or out of date (run setup.sh again).");
        return true;
    }
}
