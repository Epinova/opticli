using System.Net;
using OptiCli.Core;
using OptiCli.Core.Cms;
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

        var fromSite = Core.Cms.OrphanedTypes.FromSite(types, await site.Agent.SendAsync<TypesWithoutCodeResult>(HttpMethod.Get, AgentRoutes.TypesWithoutCode, null, cancellationToken));
        var fromSource = Core.Cms.OrphanedTypes.FromSource(types, CSharpSourceIndex.Build(site.ProjectDirectory), ScheduledJobSources.Assemblies(site.ProjectDirectory));

        Assert.Equal(OrphanedTypes, fromSite.Select(t => t.Name));
        Assert.Equal(1, fromSite.Single(t => t.Guid == RemovedType).Instances);
        Assert.Equal(fromSite.Select(t => t.Name), fromSource.Select(t => t.Name));
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
        Assert.Contains("made in admin mode", validation.Single(v => v.Property == "EdgeAdminPage").Message);
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
            Assert.DoesNotContain(dry.Kept!, k => k.Type is "EdgeAdminPage" or "EdgePage" && k.Property is null);

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

    private static async Task<(OrphanRemover.Output Output, IReadOnlyList<string> Warnings)> RemoveAsync(SiteUnderTest site, OrphanRemovalRequest request, CancellationToken cancellationToken) =>
        await OrphanRemover.RunAsync(site.Agent, request, cancellationToken);

    /// <summary><c>types --orphaned</c> as the site answers it.</summary>
    private static async Task<IReadOnlyList<string>> OrphanedNamesAsync(SiteUnderTest site, CancellationToken cancellationToken)
    {
        var types = await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken);
        var fromSite = await site.Agent.SendAsync<TypesWithoutCodeResult>(HttpMethod.Get, AgentRoutes.TypesWithoutCode, null, cancellationToken);
        return Core.Cms.OrphanedTypes.FromSite(types, fromSite).Select(t => t.Name).ToList();
    }

    /// <summary>EdgePage's properties that aren't in its class, from the database.</summary>
    private static async Task<IReadOnlyList<string>> NotInCodeAsync(SiteUnderTest site, CancellationToken cancellationToken)
    {
        var edgePage = (await ContentTypeReader.ListAsync(site.Session.Db, cancellationToken)).Single(t => t.Name == "EdgePage");
        return (await ContentTypeReader.ListPropertiesAsync(site.Session.Db, edgePage.Id, cancellationToken)).Where(p => !p.ExistsOnModel).Select(p => p.Name).ToList();
    }

    /// <summary>Makes whatever of OrphansFixture.cs is missing, through the site.</summary>
    /// <returns>False on a site without the fixture (not the edge-case site, or one built before it had this endpoint).</returns>
    private static async Task<bool> ReseedAsync(SiteUnderTest site, CancellationToken cancellationToken)
    {
        var state = StateStore.For(OptiCliEnvironment.FromProcess(), site.ProjectDirectory).Read() ?? throw new InvalidOperationException("opticli serve isn't running.");
        using var http = new HttpClient { BaseAddress = state.BaseUrl };
        using var response = await http.PostAsync("opticli-fixture/orphans", null, cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent;
    }
}
