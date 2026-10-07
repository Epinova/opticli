using EPiServer.DataAbstraction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Http;
using OptiCli.Agent.Orphans;
using OptiCli.Agent.Tests.Cms;
using OptiCli.Cms;
using OptiCli.Protocol;
using static OptiCli.Agent.Tests.Hosting.HostingFixture;

namespace OptiCli.Agent.Tests.Orphans;

/// <summary>The orphan rules and the order of removal, over a stand-in for the site's content model.</summary>
public class OrphanRemovalOperationTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("opticli-orphans-").FullName;

    private readonly StringWriter _echo = new();

    public void Dispose() => Directory.Delete(_state, recursive: true);

    private string RecordFile => Path.Combine(_state, OrphanRemoval.RecordFileName);

    private OrphanRemovalResult Run(IContentModelSource model, OrphanRemovalRequest request) =>
        OrphanRemovalOperation.Run(model, request, request.DryRun ? null : new RemovalRecords(RecordFile, "/sites/Example", "localhost/example", _echo));

    private IReadOnlyList<RemovalRecord> Records() =>
        File.Exists(RecordFile) ? File.ReadAllLines(RecordFile).Select(l => System.Text.Json.JsonSerializer.Deserialize<RemovalRecord>(l, AgentJson.Options)!).ToList() : [];

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

        /// <summary>Runs after each removal: e.g. values that appear meanwhile.</summary>
        public Action<string>? After { get; init; }

        public void Remove(SiteProperty property) => Done(Check($"property {property.Name}"));

        public void Remove(SiteType type) => Done(Check($"type {type.Name}"));

        private void Done(string label)
        {
            Removed.Add(label);
            After?.Invoke(label);
        }

        /// <summary>Throws this instead of removing <see cref="Refuses"/>.</summary>
        public Exception? Throws { get; init; }

        private string Check(string label) => label.EndsWith($" {Refuses}", StringComparison.Ordinal)
            ? throw Throws ?? new DataAbstractionException($"{Refuses} cannot be deleted.")
            : label;
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

        var result = Run(model, Remove("oldpage"));

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

        var result = Run(model, Remove("OldPage") with { DryRun = true });

        Assert.Empty(model.Removed);
        Assert.Equal((false, true, "OldPage"), (result.Removed, result.DryRun, Assert.Single(result.Types).Name));
    }

    [Fact]
    public void Content_keeps_a_type_also_when_it_is_only_in_the_recycle_bin()
    {
        var old = Type("OldPage");
        var model = new Model(old);
        model.Usages[old.Id] = new TypeUsage(0, 2, 0, 0, []);

        var error = Assert.Throws<AgentException>(() => Run(model, Remove("OldPage")));

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

        var error = Assert.Throws<AgentException>(() => Run(model, Remove("OldPage")));

        Assert.Equal(AgentErrorCodes.Conflict, error.Code);
        Assert.Contains("3 page-type property values naming it", error.Message);
    }

    [Theory]
    [InlineData("AdminPage", null, false, "made in admin mode")]
    [InlineData("StandardPage", "Example.Models.StandardPage, Example", true, "has a class the site loads")]
    [InlineData("SysRecycleBin", null, false, "one of the CMS's own types")]
    public void A_type_that_isnt_left_over_from_removed_code_is_refused(string name, string? modelType, bool hasClass, string reason)
    {
        var model = new Model(Type(name, modelType, hasClass));

        var error = Assert.Throws<AgentException>(() => Run(model, Remove(name) with { DryRun = true }));

        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void A_type_its_model_sync_made_without_a_class_on_record_is_removed_like_one_whose_class_is_gone()
    {
        // CMS 13 keeps no class for a model with a GUID: once the class is gone, only the sync's version says it came
        // from code. Its properties count as code's too.
        var synced = Type("OldPage", modelType: null, properties: Property("OldText")) with { SyncedFromCode = true };
        var model = new Model(synced, Type("AdminPage", null, properties: Property("AdminText")));

        var removed = Run(model, new OrphanRemovalRequest { Prune = true, PruneProperties = true, AllowDestructive = true });

        Assert.Equal(["OldPage"], removed.Types.Select(t => t.Name));
        Assert.Equal("", removed.Types[0].ModelType);
        Assert.DoesNotContain(removed.Properties, p => p.Type == "AdminPage");
        Assert.Contains("made in admin mode", Assert.Throws<AgentException>(() => Run(model, Remove("AdminPage") with { DryRun = true })).Message);
    }

    [Fact]
    public void Named_items_are_all_checked_first_and_reported_in_order_with_the_weightiest_code()
    {
        var used = Type("UsedPage");
        var model = new Model(Type("OldPage"), used, Type("AdminPage", null));
        model.Usages[used.Id] = new TypeUsage(1, 0, 0, 0, []);

        var error = Assert.Throws<AgentException>(() => Run(model, Remove("OldPage", "Nope", "UsedPage", "AdminPage")));

        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Equal(["Nope", "UsedPage", "AdminPage"], error.Validation!.Select(v => v.Property));
        Assert.StartsWith("Nothing was removed. 3 of the 4 can't go", error.Message);
        Assert.Empty(model.Removed);
    }

    [Fact]
    public void A_name_that_doesnt_exist_is_not_found_with_a_suggestion()
    {
        var error = Assert.Throws<AgentException>(() => Run(new Model(Type("OldNewsPage")), Remove("OldNewsPag")));

        Assert.Equal(AgentErrorCodes.NotFound, error.Code);
        Assert.Contains("Did you mean OldNewsPage?", error.Message);
    }

    [Fact]
    public void A_block_type_goes_after_the_type_whose_property_uses_it()
    {
        var block = Type("OldTeaserBlock");
        var page = Type("OldPage", properties: Property("Teaser", existsOnModel: true, blockType: block.Guid));
        var model = new Model(block, page);

        var result = Run(model, Remove("OldTeaserBlock", "OldPage"));

        Assert.Equal(["type OldPage", "type OldTeaserBlock"], model.Removed);
        Assert.Equal(["OldPage", "OldTeaserBlock"], result.Types.Select(t => t.Name));
    }

    [Fact]
    public void A_block_type_stays_while_a_property_of_a_type_that_stays_uses_it()
    {
        var block = Type("OldTeaserBlock");
        var model = new Model(block, Type("StandardPage", "Example.Models.StandardPage, Example", hasClass: true, Property("Teaser", blockType: block.Guid)));

        var error = Assert.Throws<AgentException>(() => Run(model, Remove("OldTeaserBlock")));

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

        var refused = Assert.Throws<AgentException>(() => Run(model, request));
        Assert.Empty(model.Removed);
        var result = Run(model, request with { AllowDestructive = true });

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
            Run(model, new OrphanRemovalRequest { Properties = [new OrphanPropertyRef("StandardPage", name)], AllowDestructive = true }));

        Assert.Equal(AgentErrorCodes.Refused, error.Code);
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void A_property_of_a_type_made_in_admin_mode_is_refused()
    {
        var model = new Model(Type("AdminPage", null, properties: Property("Text")));

        var error = Assert.Throws<AgentException>(() =>
            Run(model, new OrphanRemovalRequest { Properties = [new OrphanPropertyRef("AdminPage", "Text")] }));

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

        var result = Run(model, new OrphanRemovalRequest { Prune = true });

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

        var result = Run(model, new OrphanRemovalRequest { Prune = true, PruneProperties = true });

        Assert.Equal(["property OldTeaser", "type OldTeaserBlock"], model.Removed);
        var kept = Assert.Single(result.Kept);
        Assert.Equal(("OldIntro", AgentErrorCodes.Refused), (kept.Property, kept.Code));
        Assert.Equal(new StoredValueCounts(1, 1), kept.Values);
    }

    [Fact]
    public void A_property_of_a_type_that_goes_in_the_same_run_goes_with_it()
    {
        var model = new Model(Type("OldPage", properties: Property("OldIntro")));

        var result = Run(model, new OrphanRemovalRequest { Prune = true, PruneProperties = true });

        Assert.Equal(["type OldPage"], model.Removed);
        Assert.Empty(result.Properties);
    }

    [Fact]
    public void When_the_cms_refuses_halfway_the_error_says_what_was_removed()
    {
        var block = Type("OldTeaserBlock");
        var model = new Model(Type("OldPage", properties: Property("Teaser", existsOnModel: true, blockType: block.Guid)), block) { Refuses = "OldTeaserBlock" };

        var error = Assert.Throws<AgentException>(() => Run(model, Remove("OldPage", "OldTeaserBlock")));

        Assert.Equal(AgentErrorCodes.Conflict, error.Code);
        Assert.Contains("The CMS refused to remove OldTeaserBlock", error.Message);
        Assert.Contains("Already removed: OldPage (details.removed has their records", error.Message);
        Assert.EndsWith("not removed: OldTeaserBlock.", error.Message);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void Names_or_prune_are_required_and_exclusive(bool named, bool prune, bool pruneProperties)
    {
        var request = new OrphanRemovalRequest { Types = named ? ["OldPage"] : null, Prune = prune, PruneProperties = pruneProperties };

        var error = Assert.Throws<AgentException>(() => Run(new Model(Type("OldPage")), request));

        Assert.Equal(AgentErrorCodes.Usage, error.Code);
    }

    [Fact]
    public void A_property_a_content_provider_uses_stays_whatever_the_flag()
    {
        var page = Type("StandardPage", "Example.Models.StandardPage, Example", hasClass: true, Property("OldIntro"));
        var model = new Model(page);
        model.Stored[page.Properties[0].Id] = new StoredValueCounts(0, 0) { Providers = ["CatalogContent"] };
        var request = new OrphanRemovalRequest { Properties = [new OrphanPropertyRef("StandardPage", "OldIntro")], AllowDestructive = true };

        var error = Assert.Throws<AgentException>(() => Run(model, request));
        var pruned = Run(model, new OrphanRemovalRequest { Prune = true, PruneProperties = true, AllowDestructive = true, DryRun = true });

        Assert.Equal(AgentErrorCodes.Conflict, error.Code);
        Assert.Contains("A content provider still uses it (CatalogContent)", error.Message);
        Assert.Contains(OrphanRemoval.ProviderHint, error.Hint);
        Assert.Empty(model.Removed);
        var kept = Assert.Single(pruned.Kept);
        Assert.Equal(AgentErrorCodes.Conflict, kept.Code);
        Assert.Equal(["CatalogContent"], kept.Values!.Providers!);
        Assert.Empty(pruned.Properties);
    }

    [Fact]
    public void Page_type_values_get_their_own_hint_with_the_versions_holding_them()
    {
        var old = Type("OldPage");
        var model = new Model(old);
        model.Usages[old.Id] = new TypeUsage(0, 0, 0, 2, []) { PageTypeVersions = ["45_678", "46"] };

        var error = Assert.Throws<AgentException>(() => Run(model, Remove("OldPage")));

        Assert.Contains("2 page-type property values naming it (in 45_678, 46)", error.Message);
        Assert.Equal(OrphanPlanner.PageTypeHint, error.Hint);
    }

    [Fact]
    public void Each_removal_is_recorded_in_the_file_and_the_sites_output_before_it_happens()
    {
        var page = Type("StandardPage", "Example.Models.StandardPage, Example", hasClass: true, Property("OldIntro"));
        var old = Type("OldPage", properties: Property("Heading", existsOnModel: true));
        var model = new Model(page, old) { After = label => Assert.Contains(Records(), r => label.EndsWith(r.Type?.Name ?? r.Property!.Property.Name, StringComparison.Ordinal)) };
        model.Stored[page.Properties[0].Id] = new StoredValueCounts(1, 2);

        var result = Run(model, new OrphanRemovalRequest { Prune = true, PruneProperties = true, AllowDestructive = true });

        Assert.Equal(RecordFile, result.RecordFile);
        var records = Records();
        Assert.Equal(2, records.Count);
        Assert.Equal(("StandardPage", "OldIntro", new StoredValueCounts(1, 2)), (records[0].Property!.Type, records[0].Property!.Property.Name, records[0].Property!.Values));
        Assert.Equal(("OldPage", "Heading"), (records[1].Type!.Name, records[1].Type!.Properties.Single().Name));
        Assert.All(records, r => Assert.Equal(("/sites/Example", "localhost/example"), (r.Project, r.Database)));
        Assert.Equal(2, _echo.ToString().Split('\n').Count(l => l.StartsWith("[opticli] Removing the ", StringComparison.Ordinal)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(RecordFile));
        }
    }

    [Fact]
    public void A_dry_run_records_nothing()
    {
        var result = Run(new Model(Type("OldPage")), Remove("OldPage") with { DryRun = true });

        Assert.Null(result.RecordFile);
        Assert.False(File.Exists(RecordFile));
    }

    [Fact]
    public void A_run_that_stops_halfway_returns_the_full_records_of_what_it_removed_and_keeps_the_inner_code()
    {
        var block = Type("OldTeaserBlock");
        var model = new Model(Type("OldPage", properties: Property("Teaser", existsOnModel: true, blockType: block.Guid)), block)
        {
            Refuses = "OldTeaserBlock",
            Throws = AgentException.NotFound("'OldTeaserBlock' was removed meanwhile."),
        };

        var error = Assert.Throws<AgentException>(() => Run(model, Remove("OldPage", "OldTeaserBlock")));

        Assert.Equal(AgentErrorCodes.NotFound, error.Code);
        Assert.Contains("Already removed: OldPage", error.Message);
        Assert.Equal(("OldPage", "Teaser"), (Assert.Single(error.Removal!.Types).Name, error.Removal.Types[0].Properties.Single().Name));
        Assert.Equal(RecordFile, error.Removal.RecordFile);
        // The record of the one that failed, then a line saying it wasn't removed.
        Assert.Equal(["OldPage", "OldTeaserBlock", null], Records().Select(r => r.Type?.Name));
        Assert.StartsWith("OldTeaserBlock: ", Records()[2].Failed);
    }

    [Fact]
    public void Values_that_appear_after_the_check_stop_the_run_before_that_property_goes()
    {
        var page = Type("StandardPage", "Example.Models.StandardPage, Example", hasClass: true, Property("OldIntro"), Property("OldTitle"));
        Model model = null!;
        model = new Model(page) { After = _ => model.Stored[page.Properties[1].Id] = new StoredValueCounts(1, 1) };

        var error = Assert.Throws<AgentException>(() => Run(model, new OrphanRemovalRequest { Properties = [new("StandardPage", "OldIntro"), new("StandardPage", "OldTitle")] }));

        Assert.Equal(AgentErrorCodes.Conflict, error.Code);
        Assert.Contains("OldTitle wasn't removed: its stored values changed since they were checked", error.Message);
        Assert.Equal(["property OldIntro"], model.Removed);
        Assert.Equal("OldIntro", Assert.Single(error.Removal!.Properties).Property.Name);
        Assert.Single(Records());
    }

    [Fact]
    public void A_caller_that_stops_waiting_halfway_leaves_a_complete_record_of_what_was_removed()
    {
        using var aborted = new CancellationTokenSource();
        var model = new Model(Type("OldPage"), Type("OlderPage")) { After = _ => aborted.Cancel() };

        var error = Assert.Throws<AgentException>(() => OrphanRemovalOperation.Run(model, new OrphanRemovalRequest { Prune = true },
            new RemovalRecords(RecordFile, "/sites/Example", "localhost/example", null), aborted.Token));

        Assert.Contains("The caller stopped waiting", error.Message);
        Assert.Equal(["type OldPage"], model.Removed);
        Assert.Equal(["OldPage"], Records().Select(r => r.Type!.Name));
        Assert.Equal("OldPage", Assert.Single(error.Removal!.Types).Name);
    }

    [Theory]
    [InlineData("""{"types":[null]}""")]
    [InlineData("""{"types":[" "]}""")]
    [InlineData("""{"properties":[{"type":null,"property":"OldIntro"}]}""")]
    [InlineData("""{"properties":[null]}""")]
    public void Nulls_in_a_raw_request_are_usage_errors(string json)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<OrphanRemovalRequest>(json, AgentRequest.RequestOptions)!;

        var error = Assert.Throws<AgentException>(() => Run(new Model(Type("OldPage")), request));

        Assert.Equal(AgentErrorCodes.Usage, error.Code);
    }

    [Fact]
    public void The_record_file_is_in_opticlis_state_directory()
    {
        Assert.Equal(Path.Combine("/x/state", "opticli", OrphanRemoval.RecordFileName),
            RemovalRecords.DefaultPath(name => name == "XDG_STATE_HOME" ? "/x/state" : null));
        Assert.Equal(Path.Combine("/home/someone", ".local", "state", "opticli", OrphanRemoval.RecordFileName),
            RemovalRecords.DefaultPath(name => name == "HOME" ? "/home/someone" : null));
        Assert.Equal("/site/App_Data/removals.jsonl",
            RemovalRecords.DefaultPath(name => name switch
            {
                RemovalRecords.PathVariable => "/site/App_Data/removals.jsonl",
                "XDG_STATE_HOME" => "/x/state",
                _ => null,
            }));
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

    [Fact]
    public void A_property_is_deleted_through_the_cms_by_its_repository_on_cms_12_and_by_saving_its_type_without_it_on_cms_13()
    {
        var (types, typeCalls) = Recorder<IContentTypeRepository>.Create();
        var (properties, propertyCalls) = Recorder<IPropertyDefinitionRepository>.Create();
        var kept = new PropertyDefinition { ID = 11, Name = "Kept", ContentTypeID = 5 };
        var gone = new PropertyDefinition { ID = 12, Name = "Gone", ContentTypeID = 5 };
        var type = new ContentType { ID = 5, Name = "EdgePage" };
        type.PropertyDefinitions.Add(kept);
        type.PropertyDefinitions.Add(gone);
        typeCalls.Answer = (method, args) => method.Name == nameof(IContentTypeRepository.Load) && args[0] is 5 ? type : null;

        OptiCli.Agent.Compat.AgentBuild.DeleteProperty(types, properties, gone);

#if CMS13
        // A writable copy of the type, saved without the property; the CMS deletes the definitions a saved type lacks.
        var saved = Assert.IsType<ContentType>(typeCalls.Calls.Single(c => c.Method == nameof(IContentTypeRepository.Save)).Args[0]);
        Assert.NotSame(type, saved);
        Assert.Equal(["Kept"], saved.PropertyDefinitions.Select(p => p.Name));
        Assert.Empty(propertyCalls.Calls);

        // Gone meanwhile: a conflict, and nothing saved.
        typeCalls.Calls.Clear();
        var error = Assert.Throws<AgentException>(() => OptiCli.Agent.Compat.AgentBuild.DeleteProperty(types, properties, new PropertyDefinition { ID = 13, Name = "Other", ContentTypeID = 5 }));
        Assert.Equal(AgentErrorCodes.Conflict, error.Code);
        Assert.DoesNotContain(typeCalls.Calls, c => c.Method == nameof(IContentTypeRepository.Save));
#else
        Assert.Equal([(nameof(IPropertyDefinitionRepository.Delete), (object?)gone)], propertyCalls.Calls.Select(c => (c.Method, c.Args[0])));
        Assert.Empty(typeCalls.Calls);
#endif
    }
}
