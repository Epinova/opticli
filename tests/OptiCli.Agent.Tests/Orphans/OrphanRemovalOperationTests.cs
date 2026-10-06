using EPiServer.DataAbstraction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Http;
using OptiCli.Agent.Orphans;
using OptiCli.Cms;
using OptiCli.Protocol;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Orphans;

/// <summary>The orphan rules and the order of removal, over a stand-in for the site's content model.</summary>
public class OrphanRemovalOperationTests
{
    private const string Gone = "Example.Models.OldPage, Example";

    private sealed class Model(params SiteType[] types) : IContentModelSource
    {
        public Dictionary<int, TypeUsage> Usages { get; } = [];

        public Dictionary<int, StoredValueCounts> Stored { get; } = [];

        public List<string> Removed { get; } = [];

        /// <summary>The CMS refuses to remove this one.</summary>
        public string? Refuses { get; init; }

        public IReadOnlyList<SiteType> Types() => types;

        public TypeUsage Usage(SiteType type) => Usages.GetValueOrDefault(type.Id, TypeUsage.None);

        public StoredValueCounts Values(SiteProperty property) => Stored.GetValueOrDefault(property.Id, new StoredValueCounts(0, 0));

        public void Remove(SiteProperty property) => Removed.Add(Check($"property {property.Name}"));

        public void Remove(SiteType type) => Removed.Add(Check($"type {type.Name}"));

        private string Check(string label) => label.EndsWith($" {Refuses}", StringComparison.Ordinal) ? throw new DataAbstractionException($"{Refuses} cannot be deleted.") : label;
    }

    private static int _ids = 100;

    private static SiteType Type(string name, string? modelType = Gone, bool hasClass = false, params SiteProperty[] properties)
    {
        var id = Interlocked.Increment(ref _ids);
        return new SiteType(id, Guid.NewGuid(), name, name.EndsWith("Block", StringComparison.Ordinal) ? "Block" : "Page", name, null, modelType, hasClass,
            properties.Select(p => p with { TypeId = id }).ToList());
    }

    private static SiteProperty Property(string name, bool existsOnModel = false, bool inModel = false, Guid? blockType = null)
    {
        var id = Interlocked.Increment(ref _ids);
        return new SiteProperty(id, 0, name, existsOnModel, inModel, blockType,
            new RemovedPropertyDefinition(id, name, blockType is null ? "String" : "Block", null, null, false, false, false, true, name, null, "Information", 0));
    }

    private static OrphanRemovalRequest Remove(params string[] types) => new() { Types = types };

    [Fact]
    public void An_orphaned_type_that_nothing_uses_is_removed_and_recorded()
    {
        var old = Type("OldPage", properties: Property("Intro", existsOnModel: true));
        var model = new Model(old, Type("StandardPage", "Example.Models.StandardPage, Example", hasClass: true) with { AllowedChildren = ["OldPage", "StandardPage"] });

        var result = OrphanRemovalOperation.Run(model, Remove("oldpage"));

        Assert.Equal(["type OldPage"], model.Removed);
        var removed = Assert.Single(result.Types);
        Assert.Equal((old.Guid, Gone, "Intro"), (removed.Guid, removed.ModelType, Assert.Single(removed.Properties).Name));
        Assert.Equal(["StandardPage"], removed.AvailableUnder);
        Assert.True(result.Removed);
        Assert.Contains(OrphanRemoval.NoUndo, result.Warnings!);
    }

    [Fact]
    public void A_dry_run_runs_every_check_and_removes_nothing()
    {
        var model = new Model(Type("OldPage"));

        var result = OrphanRemovalOperation.Run(model, Remove("OldPage") with { DryRun = true });

        Assert.Empty(model.Removed);
        Assert.Equal((false, true, "OldPage"), (result.Removed, result.DryRun, Assert.Single(result.Types).Name));
    }

    [Fact]
    public void Content_keeps_a_type_also_when_it_is_only_in_the_recycle_bin()
    {
        var old = Type("OldPage");
        var model = new Model(old);
        model.Usages[old.Id] = new TypeUsage(0, 2, 0, 0, []);

        var error = Assert.Throws<AgentException>(() => OrphanRemovalOperation.Run(model, Remove("OldPage")));

        Assert.Equal((AgentErrorCodes.Conflict, AgentErrorReasons.Orphans), (error.Code, error.Reason));
        Assert.Contains("2 content items in the recycle bin", error.Message);
        Assert.Equal(OrphanPlanner.ContentHint, error.Hint);
        Assert.Empty(model.Removed);
    }

    [Fact]
    public void A_type_whose_page_type_values_name_it_stays_since_the_cms_would_clear_them()
    {
        var old = Type("OldPage");
        var model = new Model(old);
        model.Usages[old.Id] = new TypeUsage(0, 0, 0, 3, []);

        var error = Assert.Throws<AgentException>(() => OrphanRemovalOperation.Run(model, Remove("OldPage")));

        Assert.Equal(AgentErrorCodes.Conflict, error.Code);
        Assert.Contains("3 versions whose page-type property names it", error.Message);
    }

    [Theory]
    [InlineData("AdminPage", null, false, "made in admin mode")]
    [InlineData("StandardPage", "Example.Models.StandardPage, Example", true, "has a class the site loads")]
    [InlineData("SysRecycleBin", null, false, "one of the CMS's own types")]
    public void A_type_that_isnt_left_over_from_removed_code_is_refused(string name, string? modelType, bool hasClass, string reason)
    {
        var model = new Model(Type(name, modelType, hasClass));

        var error = Assert.Throws<AgentException>(() => OrphanRemovalOperation.Run(model, Remove(name) with { DryRun = true }));

        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void Named_items_are_all_checked_first_and_reported_in_order_with_the_weightiest_code()
    {
        var used = Type("UsedPage");
        var model = new Model(Type("OldPage"), used, Type("AdminPage", null));
        model.Usages[used.Id] = new TypeUsage(1, 0, 0, 0, []);

        var error = Assert.Throws<AgentException>(() => OrphanRemovalOperation.Run(model, Remove("OldPage", "Nope", "UsedPage", "AdminPage")));

        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Equal(["Nope", "UsedPage", "AdminPage"], error.Validation!.Select(v => v.Property));
        Assert.StartsWith("Nothing was removed. 3 of the 4 can't go", error.Message);
        Assert.Empty(model.Removed);
    }

    [Fact]
    public void A_name_that_doesnt_exist_is_not_found_with_a_suggestion()
    {
        var error = Assert.Throws<AgentException>(() => OrphanRemovalOperation.Run(new Model(Type("OldNewsPage")), Remove("OldNewsPag")));

        Assert.Equal(AgentErrorCodes.NotFound, error.Code);
        Assert.Contains("Did you mean OldNewsPage?", error.Message);
    }

    [Fact]
    public void A_block_type_goes_after_the_type_whose_property_uses_it()
    {
        var block = Type("OldTeaserBlock");
        var page = Type("OldPage", properties: Property("Teaser", existsOnModel: true, blockType: block.Guid));
        var model = new Model(block, page);

        var result = OrphanRemovalOperation.Run(model, Remove("OldTeaserBlock", "OldPage"));

        Assert.Equal(["type OldPage", "type OldTeaserBlock"], model.Removed);
        Assert.Equal(["OldPage", "OldTeaserBlock"], result.Types.Select(t => t.Name));
    }

    [Fact]
    public void A_block_type_stays_while_a_property_of_a_type_that_stays_uses_it()
    {
        var block = Type("OldTeaserBlock");
        var model = new Model(block, Type("StandardPage", "Example.Models.StandardPage, Example", hasClass: true, Property("Teaser", blockType: block.Guid)));

        var error = Assert.Throws<AgentException>(() => OrphanRemovalOperation.Run(model, Remove("OldTeaserBlock")));

        Assert.Equal(AgentErrorCodes.Conflict, error.Code);
        Assert.Contains("block type of StandardPage.Teaser", error.Message);
        Assert.Empty(model.Removed);
    }

    [Fact]
    public void A_property_with_values_needs_allow_destructive()
    {
        var page = Type("StandardPage", "Example.Models.StandardPage, Example", hasClass: true, Property("OldIntro"));
        var model = new Model(page);
        model.Stored[page.Properties[0].Id] = new StoredValueCounts(2, 5);
        var request = new OrphanRemovalRequest { Properties = [new OrphanPropertyRef("StandardPage", "oldintro")] };

        var refused = Assert.Throws<AgentException>(() => OrphanRemovalOperation.Run(model, request));
        Assert.Empty(model.Removed);
        var result = OrphanRemovalOperation.Run(model, request with { AllowDestructive = true });

        Assert.Equal(AgentErrorCodes.Refused, refused.Code);
        Assert.Contains("2 content items, 5 versions", refused.Message);
        Assert.Equal(["property OldIntro"], model.Removed);
        Assert.Equal(new StoredValueCounts(2, 5), Assert.Single(result.Properties).Values);
        Assert.Contains(result.Warnings!, w => w.StartsWith("Deleted the stored values of StandardPage.OldIntro (2 content items, 5 versions)", StringComparison.Ordinal));
        Assert.Contains(OrphanRemoval.AdminModeLookalike, result.Warnings!);
    }

    [Theory]
    [InlineData("Intro", true, false, "is in StandardPage's code")]
    [InlineData("Intro", false, true, "is in StandardPage's code")]
    public void A_property_in_the_code_is_refused(string name, bool existsOnModel, bool inModel, string reason)
    {
        var model = new Model(Type("StandardPage", "Example.Models.StandardPage, Example", hasClass: true, Property(name, existsOnModel, inModel)));

        var error = Assert.Throws<AgentException>(() =>
            OrphanRemovalOperation.Run(model, new OrphanRemovalRequest { Properties = [new OrphanPropertyRef("StandardPage", name)], AllowDestructive = true }));

        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void A_property_of_a_type_made_in_admin_mode_is_refused()
    {
        var model = new Model(Type("AdminPage", null, properties: Property("Text")));

        var error = Assert.Throws<AgentException>(() =>
            OrphanRemovalOperation.Run(model, new OrphanRemovalRequest { Properties = [new OrphanPropertyRef("AdminPage", "Text")] }));

        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Contains("made in admin mode", error.Message);
    }

    [Fact]
    public void Prune_removes_what_can_go_and_lists_the_rest_leaving_properties_alone_without_the_flag()
    {
        var used = Type("UsedPage");
        var page = Type("StandardPage", "Example.Models.StandardPage, Example", hasClass: true, Property("OldIntro"), Property("Heading", existsOnModel: true, inModel: true));
        var model = new Model(Type("OldPage"), used, Type("AdminPage", null), page);
        model.Usages[used.Id] = new TypeUsage(1, 0, 0, 0, []);

        var result = OrphanRemovalOperation.Run(model, new OrphanRemovalRequest { Prune = true });

        Assert.Equal(["type OldPage"], model.Removed);
        Assert.Equal([("StandardPage", "OldIntro", AgentErrorCodes.Refused), ("UsedPage", null, AgentErrorCodes.Conflict)],
            result.Kept.Select(k => (k.Type, k.Property, k.Code)));
        Assert.Contains("only with --properties", result.Kept[0].Reason);
    }

    [Fact]
    public void Prune_with_properties_frees_a_block_type_whose_only_property_goes_first()
    {
        var block = Type("OldTeaserBlock");
        var page = Type("StandardPage", "Example.Models.StandardPage, Example", hasClass: true, Property("OldTeaser", blockType: block.Guid), Property("OldIntro"));
        var model = new Model(block, page);
        model.Stored[page.Properties[1].Id] = new StoredValueCounts(1, 1);

        var result = OrphanRemovalOperation.Run(model, new OrphanRemovalRequest { Prune = true, PruneProperties = true });

        Assert.Equal(["property OldTeaser", "type OldTeaserBlock"], model.Removed);
        var kept = Assert.Single(result.Kept);
        Assert.Equal(("OldIntro", AgentErrorCodes.Refused), (kept.Property, kept.Code));
        Assert.Equal(new StoredValueCounts(1, 1), kept.Values);
    }

    [Fact]
    public void A_property_of_a_type_that_goes_in_the_same_run_goes_with_it()
    {
        var model = new Model(Type("OldPage", properties: Property("OldIntro")));

        var result = OrphanRemovalOperation.Run(model, new OrphanRemovalRequest { Prune = true, PruneProperties = true });

        Assert.Equal(["type OldPage"], model.Removed);
        Assert.Empty(result.Properties);
    }

    [Fact]
    public void When_the_cms_refuses_halfway_the_error_says_what_was_removed()
    {
        var block = Type("OldTeaserBlock");
        var model = new Model(Type("OldPage", properties: Property("Teaser", existsOnModel: true, blockType: block.Guid)), block) { Refuses = "OldTeaserBlock" };

        var error = Assert.Throws<AgentException>(() => OrphanRemovalOperation.Run(model, Remove("OldPage", "OldTeaserBlock")));

        Assert.Equal(AgentErrorCodes.Conflict, error.Code);
        Assert.Contains("The CMS refused to remove OldTeaserBlock", error.Message);
        Assert.Contains("Already removed: OldPage; not removed: OldTeaserBlock.", error.Message);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void Names_or_prune_are_required_and_exclusive(bool named, bool prune, bool pruneProperties)
    {
        var request = new OrphanRemovalRequest { Types = named ? ["OldPage"] : null, Prune = prune, PruneProperties = pruneProperties };

        var error = Assert.Throws<AgentException>(() => OrphanRemovalOperation.Run(new Model(Type("OldPage")), request));

        Assert.Equal(AgentErrorCodes.Usage, error.Code);
    }

    [Fact]
    public void Nothing_is_removed_against_a_shared_database()
    {
        var request = new AgentRequest(new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(Settings(pinned: Remote, approvedRemote: RemoteApproval)).BuildServiceProvider(),
        }, null);

        var error = Assert.Throws<AgentException>(() => OrphanRemovalOperation.Run(request, new OrphanRemovalRequest { Prune = true, DryRun = true }));

        Assert.Equal((AgentErrorCodes.Refused, OrphanRemoval.SharedRefusal), (error.Code, error.Message));
    }
}
