using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.Data.Entity;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using EPiServer.SpecializedProperties;
using EPiServer.Validation;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Tests.Cms;
using OptiCli.Agent.Tests.Projects;
using OptiCli.Cms;
using OptiCli.Cms.Compat;
using OptiCli.Cms.Content;
using OptiCli.Cms.Operations;
using OptiCli.Cms.Projects;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

/// <summary>
/// Where the site's edit UI makes new blocks in a ContentArea shared (<c>UIOptions.InlineBlocksInContentAreaEnabled</c>
/// off), a new block in an area is a shared block in the content's "For this page" folder, saved with the content
/// (<see cref="SharedAreaBlocks"/>). Against an in-memory CMS: the content, its folder and its blocks, each save recorded.
/// The CMS 13 build runs them: CMS 12.0's ContentArea needs the CMS's service locator for an item that shows content.
/// </summary>
public class SharedAreaBlockTests
{
#if CMS13
    private const int ItemType = 7;

    private const int PageTypeId = 8;

    private const int BoxType = 9;

    private const int FolderType = 99;

    private static readonly ContentReference AssetsRoot = new(3);

    private static readonly ContentReference Start = new(5);

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    private static ContentType BlockType(int id, string name, Type model) =>
#if CMS13
        new() { ID = id, Name = name, ModelType = model, Base = ContentTypeBase.Block };
#else
        new EPiServer.DataAbstraction.BlockType { ID = id, Name = name, ModelType = model };
#endif

    private static readonly ContentType[] Types =
    [
        BlockType(ItemType, nameof(PropertyWriterTests.ItemBlock), typeof(PropertyWriterTests.ItemBlock)),
        new PageType { ID = PageTypeId, Name = "StandardPage", ModelType = typeof(Page) },
        BlockType(BoxType, nameof(InlineBlockWriteTests.BoxBlock), typeof(InlineBlockWriteTests.BoxBlock)),
        new ContentType { ID = FolderType, Name = "SysContentAssetFolder", ModelType = typeof(ContentAssetFolder) },
    ];

    /// <summary>A page: a title, a ContentArea and a local block with an area of its own.</summary>
    public sealed class Page : ContentData, IContent, IVersionable, ILocalizable, IResourceable, ISecurable
    {
        public Page()
        {
            Property.Add("Title", new PropertyString { PropertyDefinitionID = 31 });
            Property.Add("Area", new PropertyContentArea { PropertyDefinitionID = 32 });
            Property.Add("Hero", new PropertyLocalBox { PropertyDefinitionID = 33, Value = InlineBlockWriteTests.BoxBlock.Create() });
            ContentTypeID = PageTypeId;
        }

        public string Name { get; set; } = "Home";

        public ContentReference ContentLink { get; set; } = ContentReference.EmptyReference;

        public ContentReference ParentLink { get; set; } = Start;

        public Guid ContentGuid { get; set; } = Guid.NewGuid();
#if !CMS13

        public int ContentTypeID { get; set; }
#endif

        public bool IsDeleted { get; set; }

        public bool IsPendingPublish { get; set; }

        public DateTime? StartPublish { get; set; }

        public DateTime? StopPublish { get; set; }

        public VersionStatus Status { get; set; } = VersionStatus.CheckedOut;
#if CMS13

        public string? Variation { get; set; }
#endif

        public CultureInfo Language { get; set; } = English;

        public CultureInfo MasterLanguage { get; set; } = English;

        public IEnumerable<CultureInfo> ExistingLanguages { get; set; } = [English];

        public Guid ContentAssetsID { get; set; }

        public string ContentFolderRelativePath { get; set; } = "";

        /// <summary>What the caller may do on it: everything when null.</summary>
        public AccessLevel? Access { get; set; }

        public ISecurityDescriptor GetSecurityDescriptor()
        {
            var (descriptor, recorder) = Recorder<ISecurityDescriptor>.Create();
            recorder.Answer = (method, args) => method.Name == nameof(ISecurityDescriptor.HasAccess) && args is [_, AccessLevel level]
                ? Access is not { } access || (access & level) == level
                : null;
            return descriptor;
        }
    }

    /// <summary>A local block property holding a <see cref="InlineBlockWriteTests.BoxBlock"/>.</summary>
    private sealed class PropertyLocalBox : PropertyData
    {
        private object? _value;

        public override object? Value { get => _value; set => _value = value; }

        public override PropertyDataType Type => PropertyDataType.Block;

        public override Type PropertyValueType => typeof(InlineBlockWriteTests.BoxBlock);

        public override void ParseToSelf(string value) => throw new NotSupportedException();

        protected override void SetDefaultValue() => _value = null;
    }

    /// <summary>A shared block as the CMS gives it: a block with an identity, of either test type.</summary>
    public sealed class Shared : BlockData, IContent, IVersionable, ILocalizable, ISecurable
    {
        /// <summary>What the caller may do on it: everything when null.</summary>
        public AccessLevel? Access { get; set; }

        public ISecurityDescriptor GetSecurityDescriptor()
        {
            var (descriptor, recorder) = Recorder<ISecurityDescriptor>.Create();
            recorder.Answer = (method, args) => method.Name == nameof(ISecurityDescriptor.HasAccess) && args is [_, AccessLevel level]
                ? Access is not { } access || (access & level) == level
                : null;
            return descriptor;
        }

        public static Shared Create(int type)
        {
            var block = new Shared { ContentTypeID = type };
            foreach (var property in (type == BoxType ? InlineBlockWriteTests.BoxBlock.Create() : (BlockData)PropertyWriterTests.ItemBlock.Create()).Property)
            {
                block.Property.Add(property.Name, property.CreateWritableClone());
            }
            return block;
        }

        public string Name { get; set; } = "";

        public ContentReference ContentLink { get; set; } = ContentReference.EmptyReference;

        public ContentReference ParentLink { get; set; } = ContentReference.EmptyReference;

        public Guid ContentGuid { get; set; } = Guid.NewGuid();

#if !CMS13
        public int ContentTypeID { get; set; }

#endif
        public bool IsDeleted { get; set; }

        public bool IsPendingPublish { get; set; }

        public DateTime? StartPublish { get; set; }

        public DateTime? StopPublish { get; set; }

        public VersionStatus Status { get; set; } = VersionStatus.CheckedOut;
#if CMS13

        public string? Variation { get; set; }
#endif

        public CultureInfo Language { get; set; } = English;

        public CultureInfo MasterLanguage { get; set; } = English;

        public IEnumerable<CultureInfo> ExistingLanguages { get; set; } = [English];
    }

    /// <summary>An in-memory content store: every version of every item, and every save.</summary>
    private sealed class Store
    {
        private int _nextId = 1000;

        private int _nextVersion = 5000;

        public Dictionary<int, List<(IContent Content, string SavedBy)>> Items { get; } = [];

        public List<(string Name, Type Kind, SaveAction Action, AccessLevel? Access)> Saves { get; } = [];

        /// <summary>Content names whose save fails, as a database error would.</summary>
        public HashSet<string> Failing { get; } = [];

        /// <summary>Who the next save is recorded as.</summary>
        public string SavedBy { get; set; } = AgentProtocol.PrincipalName;

        /// <summary>A site handler of the CMS's save events: called after each save, with what was stored.</summary>
        public Action<IContent, SaveAction>? Saved { get; set; }

        public ContentReference Save(IContent content, SaveAction action, AccessLevel? access)
        {
            var link = Keep(content, action, access);
            Saved?.Invoke(Get(link)!, action);
            return link;
        }

        private ContentReference Keep(IContent content, SaveAction action, AccessLevel? access)
        {
            Saves.Add((content.Name, content.GetType(), action, access));
            if (Failing.Contains(content.Name))
            {
                throw new InvalidOperationException("The database refused it.");
            }
            var id = ContentReference.IsNullOrEmpty(content.ContentLink) ? _nextId++ : content.ContentLink.ID;
            var versions = Items.TryGetValue(id, out var list) ? list : Items[id] = [];
            var kind = action & SaveAction.ActionMask;
            var into = content is IVersionable && (action & SaveAction.ForceNewVersion) == 0 && content.ContentLink.WorkID > 0 && kind != SaveAction.Publish
                ? content.ContentLink.WorkID : 0;
            var link = content is IVersionable ? new ContentReference(id, into > 0 ? into : _nextVersion++) : new ContentReference(id);
            var stored = (IContent)((IReadOnly)content).CreateWritableClone();
            stored.ContentLink = link;
            if (stored is IVersionable versionable)
            {
                versionable.Status = kind switch
                {
                    SaveAction.Publish => VersionStatus.Published,
                    SaveAction.Schedule => VersionStatus.DelayedPublish,
                    SaveAction.CheckIn => VersionStatus.CheckedIn,
                    _ => VersionStatus.CheckedOut,
                };
                if (kind == SaveAction.Publish)
                {
                    foreach (var (old, _) in versions.Where(v => v.Content is IVersionable { Status: VersionStatus.Published }))
                    {
                        ((IVersionable)old).Status = VersionStatus.PreviouslyPublished;
                    }
                }
            }
            versions.RemoveAll(v => v.Content.ContentLink.WorkID == link.WorkID);
            versions.Add((stored, SavedBy));
            return link;
        }

        public IContent Add(IContent content, VersionStatus status = VersionStatus.Published, string? savedBy = null)
        {
            var link = Save(content, status == VersionStatus.Published ? SaveAction.Publish : SaveAction.Save, null);
            Saves.RemoveAt(Saves.Count - 1);
            var stored = Get(link)!;
            ((IVersionable)stored).Status = status;
            Items[link.ID][^1] = (stored, savedBy ?? AgentProtocol.PrincipalName);
            return stored;
        }

        public IContent? Get(ContentReference link) =>
            !Items.TryGetValue(link.ID, out var versions) ? null
            : link.WorkID > 0 ? versions.FirstOrDefault(v => v.Content.ContentLink.WorkID == link.WorkID).Content
            : (versions.FirstOrDefault(v => v.Content is IVersionable { Status: VersionStatus.Published }).Content ?? versions[^1].Content);

        public IContent Latest(int id) => Items[id][^1].Content;

        public IEnumerable<IContent> Children(ContentReference parent) =>
            Items.Values.Select(v => v[^1].Content).Where(c => c.ParentLink.CompareToIgnoreWorkID(parent));

        public ContentVersion[] Versions(ContentReference link) => Items.TryGetValue(link.ID, out var versions)
            ? versions.Select(v => new ContentVersion(v.Content.ContentLink, v.Content.Name, (v.Content as IVersionable)?.Status ?? VersionStatus.Published,
                DateTime.UtcNow, v.SavedBy, v.SavedBy, 0, (v.Content as ILocalizable)?.Language?.Name ?? "", true, false)).ToArray()
            : [];
    }

    /// <summary>The CMS's "For this page" folders, over the store.</summary>
    private sealed class Assets(Store store) : ContentAssetHelper
    {
        public override ContentAssetFolder GetAssetFolder(ContentReference contentLink) =>
            store.Get(contentLink) is IResourceable { ContentAssetsID: var id } && id != Guid.Empty
                ? store.Items.Values.Select(v => v[^1].Content).OfType<ContentAssetFolder>().FirstOrDefault(f => f.ContentGuid == id)!
                : null!;

        public override ContentAssetFolder GetOrCreateAssetFolder(ContentReference contentLink)
        {
            if (GetAssetFolder(contentLink) is { } existing)
            {
                return existing;
            }
            var folder = new ContentAssetFolder { ContentGuid = Guid.NewGuid(), ParentLink = AssetsRoot, ContentTypeID = FolderType };
            foreach (var (version, _) in store.Items[contentLink.ID])
            {
                folder.ContentOwnerID = version.ContentGuid;
                folder.Name = version.Name;
                ((IResourceable)version).ContentAssetsID = folder.ContentGuid;
            }
            store.Save(folder, SaveAction.Publish, AccessLevel.NoAccess);
            store.Saves.RemoveAt(store.Saves.Count - 1);
            return GetAssetFolder(contentLink);
        }

        public override IContent GetAssetOwner(ContentReference contentLink) => throw new NotSupportedException();

        public override IContent GetParentFolder(ContentReference root, string relativePath, DateTime? saveTime, bool createIfNotExist) => throw new NotSupportedException();

        public override IContentMedia GetMedia(ContentReference parentLink, string mediaName, Guid mediaId) => throw new NotSupportedException();

        public override void WriteProperty(IContent content, string propertyName, string propertyValue, bool log) => throw new NotSupportedException();
    }

    /// <summary>The edit UI's types to create: those in <see cref="Offered"/>, anywhere.</summary>
    private sealed class Availability : ContentTypeAvailabilityService
    {
        public HashSet<int> Offered { get; } = [ItemType, BoxType];

        /// <summary>The types offered for a new block of a content item ("For this page" folder): <see cref="Offered"/> when null.</summary>
        public HashSet<int>? OfferedForContent { get; set; }

        public override AvailableSetting GetSetting(string contentTypeName) => new();

        public override bool IsAllowed(string parentContentTypeName, string childContentTypeName) => true;

        public override IList<ContentType> ListAvailable(string contentTypeName, IPrincipal user) => Types.Where(t => Offered.Contains(t.ID)).ToList();

        public override IList<ContentType> ListAvailable(IContent content, bool contentFolder, IPrincipal user) => Types.Where(t => (OfferedForContent ?? Offered).Contains(t.ID)).ToList();
    }

    /// <summary>A site to write to: the store, the CMS's services over it, and the caller.</summary>
    private sealed class Site
    {
        public Store Store { get; } = new();

        public FakeProjects Projects { get; } = new();

        public Availability Availability { get; } = new();

        /// <summary>The site's validators: errors for a content item.</summary>
        public Func<IContentData, IEnumerable<ValidationError>> Validate { get; set; } = _ => [];

        public CmsCall Call { get; }

        /// <summary>Raises one of the CMS's content events, as a site handler's own save would.</summary>
        public Action<string, ContentEventArgs> Raise { get; }

        /// <summary>The values a new ItemBlock gets from its type (the CMS's defaults), before a write sets any.</summary>
        public Action<BlockData>? Defaults { get; set; }

        private BlockData WithDefaults(BlockData block)
        {
            Defaults?.Invoke(block);
            return block;
        }

        public Site(CmsCaller caller = CmsCaller.Developer)
        {
            var handlers = new Dictionary<string, Delegate?>();
            var (events, eventsRecorder) = Recorder<IContentEvents>.Create();
            eventsRecorder.Answer = (method, args) =>
            {
                if (method.Name.StartsWith("add_", StringComparison.Ordinal))
                {
                    handlers[method.Name[4..]] = Delegate.Combine(handlers.GetValueOrDefault(method.Name[4..]), (Delegate)args[0]!);
                }
                else if (method.Name.StartsWith("remove_", StringComparison.Ordinal))
                {
                    handlers[method.Name[7..]] = Delegate.Remove(handlers.GetValueOrDefault(method.Name[7..]), (Delegate)args[0]!);
                }
                return null;
            };
            Raise = (name, e) => handlers.GetValueOrDefault(name)?.DynamicInvoke(null, e);
            var (types, typesRecorder) = Recorder<IContentTypeRepository>.Create();
            typesRecorder.Answer = (method, args) => (method.Name, args) switch
            {
                ("Load", [int id]) => Types.FirstOrDefault(t => t.ID == id),
                ("Load", [string name]) => Types.FirstOrDefault(t => t.Name == name),
                ("Load", [Type model]) => Types.FirstOrDefault(t => t.ModelType == model),
                ("List", []) => Types,
                _ => null,
            };
            var (repository, repositoryRecorder) = Recorder<IContentRepository>.Create();
            repositoryRecorder.Answer = (method, args) => Repository(method, args);
            var (versions, versionsRecorder) = Recorder<IContentVersionRepository>.Create();
            versionsRecorder.Answer = (method, args) => (method.Name, args) switch
            {
                ("List", [ContentReference link]) => Store.Versions(link),
                ("List", [VersionFilter filter, _, _, _]) => Listed(args, Store.Versions(filter.ContentLink)),
                ("Load", [ContentReference link]) => Store.Versions(link).FirstOrDefault(v => v.ContentLink.WorkID == link.WorkID),
                _ => null,
            };
            var (validation, validationRecorder) = Recorder<IValidationService>.Create();
            validationRecorder.Answer = (method, args) => method.Name == nameof(IValidationService.Validate) && args is [IContentData content] ? Validate(content).ToList() : null;
            var factory = Recorder<EPiServer.Construction.IContentDataFactory<BlockData>>.Create();
            factory.Recorder.Answer = (method, args) => method.Name != "CreateInstance" ? null
                : args is [ContentType { ID: BoxType }] ? InlineBlockWriteTests.BoxBlock.Create()
                : WithDefaults(PropertyWriterTests.ItemBlock.Create());
            var services = new ServiceCollection()
                .AddSingleton(types)
                .AddSingleton(repository)
                .AddSingleton(versions)
                .AddSingleton(validation)
                .AddSingleton(events)
                .AddSingleton(factory.Proxy)
                .AddSingleton(Recorder<EPiServer.Construction.IContentDataBuilder>.Create().Proxy)
                .AddSingleton(Recorder<ILanguageBranchRepository>.Create().Proxy)
                .AddSingleton(Recorder<IFrameRepository>.Create().Proxy)
                .AddSingleton(Unused<CategoryRepository>())
                .AddSingleton(Unused<EPiServer.Web.DisplayOptions>())
                .AddSingleton<ContentAssetHelper>(new Assets(Store))
                .AddSingleton<ContentTypeAvailabilityService>(Availability)
                .AddSingleton<ProjectRepository>(Projects)
                .AddSingleton(NoApprovals())
                .AddSingleton(new EPiServer.Web.SystemDefinition(new ContentReference(1), new ContentReference(2), new ContentReference(4), AssetsRoot))
                .AddSingleton(EditUiPropertiesTests.Tabs())
                .AddSingleton<IPrincipalAccessor>(new CmsCallTests.PrincipalAccessor(new GenericPrincipal(new GenericIdentity("editor"), [])))
                .BuildServiceProvider();
            Call = new CmsCall(services, CancellationToken.None, caller);
        }

        /// <summary>A page of versions, its total count in the out parameter.</summary>
        private static ContentVersion[] Listed(object?[] args, ContentVersion[] versions)
        {
            args[^1] = versions.Length;
            return versions;
        }

        private object? Repository(System.Reflection.MethodInfo method, object?[] args)
        {
            switch (method.Name, args)
            {
                case ("Save", [IContent content, SaveAction action]):
                    return Store.Save(content, action, null);
                case ("Save", [IContent content, SaveAction action, AccessLevel access]):
                    return Store.Save(content, action, access);
                case ("Get", [ContentReference link, ..]):
                    return Store.Get(link) ?? throw new ContentNotFoundException(link);
                case ("TryGet", [ContentReference link, .., _]):
                    args[^1] = Store.Get(link);
                    return args[^1] is not null;
                case ("GetChildren", [ContentReference parent, ..]):
                    return Store.Children(parent).ToList();
                case ("GetDefault", [ContentReference parent, int type, ..]):
                    if (method.GetGenericArguments()[0] == typeof(ContentAssetFolder))
                    {
                        return new ContentAssetFolder { ContentTypeID = type, ParentLink = parent };
                    }
                    var block = Shared.Create(type);
                    block.ParentLink = parent;
                    // Without a language, the block has none of its own here (the CMS would take the preferred one).
                    block.Language = block.MasterLanguage = args is [_, _, CultureInfo culture] ? culture : CultureInfo.InvariantCulture;
                    return block;
                default:
                    return null;
            }
        }

        public WriteFlow Flow(bool shared = true) => new(Call, shared);

        /// <summary>A page in the store (published), with a folder when <paramref name="folder"/>.</summary>
        public Page Page(bool folder = false, params ContentAreaItem[] items)
        {
            var page = new Page();
            if (items.Length > 0)
            {
                var area = new ContentArea();
                foreach (var item in items)
                {
                    area.Items.Add(item);
                }
                page.Property["Area"].Value = area;
            }
            var stored = (Page)Store.Add(page);
            if (folder)
            {
                Folder(stored);
            }
            return (Page)Store.Get(stored.ContentLink.ToReferenceWithoutVersion())!;
        }

        public ContentAssetFolder Folder(IContent owner) =>
            new Assets(Store).GetOrCreateAssetFolder(owner.ContentLink.ToReferenceWithoutVersion());

        /// <summary>A shared block in the store, below <paramref name="parent"/>.</summary>
        public IContent Block(ContentReference parent, string name, string title, VersionStatus status = VersionStatus.Published, int type = ItemType, string? savedBy = null)
        {
            var block = Shared.Create(type);
            block.Name = name;
            block.ParentLink = parent;
            block.Property["Title"].Value = title;
            return Store.Add(block, status, savedBy);
        }

        /// <summary>A writable copy of the page's latest version, its values applied by the flow's writer.</summary>
        public (Page Writable, Dictionary<string, JsonElement?> Before) Write(WriteFlow flow, Page page, string? json = null, IReadOnlyList<AreaOperation>? ops = null)
        {
            var writable = (Page)((IReadOnly)Store.Latest(page.ContentLink.ID)).CreateWritableClone();
            var before = PropertyValues.Snapshot(writable);
            flow.Writer.Apply(writable, json is null ? null : Values(json));
            flow.Areas.Apply(writable, ops);
            return (writable, before);
        }

        public WriteResult Save(WriteFlow flow, IContent writable, Dictionary<string, JsonElement?> before, bool dryRun = false, SaveAction action = SaveAction.Save | SaveAction.ForceNewVersion) =>
            flow.Save(writable, before, action, dryRun, shown: null, baseVersion: null, saveUnchanged: false);

        public IContent Stored(ContentReference link) => Store.Get(link)!;
    }

    /// <summary>No approval sequences anywhere.</summary>
    private static EPiServer.Approvals.IApprovalDefinitionRepository NoApprovals()
    {
        var (approvals, recorder) = Recorder<EPiServer.Approvals.IApprovalDefinitionRepository>.Create();
        recorder.Answer = (method, _) => method.ReturnType is { IsGenericType: true } task && task.GetGenericTypeDefinition() == typeof(Task<>)
            ? typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(task.GetGenericArguments()[0]).Invoke(null, [null])
            : null;
        return approvals;
    }

    private static T Unused<T>() where T : class =>
        (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T).IsAbstract
            ? typeof(T).Assembly.GetTypes().First(t => t.IsSubclassOf(typeof(T)) && !t.IsAbstract)
            : typeof(T));

    private static Dictionary<string, JsonElement> Values(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static List<ContentAreaItem> Items(IContentData content, string property = "Area") =>
        (content.Property[property].Value as ContentArea)?.Items.ToList() ?? [];

    private static ContentAreaItem Ref(IContent content) => new() { ContentLink = content.ContentLink.ToReferenceWithoutVersion() };

    private static object? Title(IContent content) => content.Property["Title"].Value;

    [Fact]
    public void A_new_block_in_an_area_is_a_shared_draft_in_the_pages_folder_saved_before_the_page()
    {
        var site = new Site();
        var page = site.Page();
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, """{"Area": [{"type": "ItemBlock", "name": "Intro", "properties": {"Title": "Hello"}}, {"type": "ItemBlock", "displayOption": null}]}""");

        var result = site.Save(flow, writable, before);

        var folder = site.Folder(page);
        Assert.Equal([("Intro", typeof(Shared)), ("Home ItemBlock 1", typeof(Shared)), ("Home", typeof(Page))], site.Store.Saves.Select(s => (s.Name, s.Kind)));
        Assert.All(site.Store.Saves.Take(2), s => Assert.Equal((SaveAction.Save, AccessLevel.NoAccess), (s.Action, s.Access)));
        var items = Items(site.Store.Latest(page.ContentLink.ID));
        var blocks = items.Select(i => site.Stored(i.ContentLink)).ToList();
        Assert.All(items, i => Assert.Null(InlineBlocks.Of(i)));
        Assert.Equal([("Intro", "Hello"), ("Home ItemBlock 1", null)], blocks.Select(b => (b.Name, Title(b))));
        Assert.All(blocks, b => Assert.Equal((folder.ContentLink.ID, VersionStatus.CheckedOut), (b.ParentLink.ID, ((IVersionable)b).Status)));

        var shared = result.SharedBlocks!;
        Assert.Equal(["Area[0]", "Area[1]"], shared.Select(s => s.Path));
        Assert.All(shared, s => Assert.Equal((SharedBlockActions.Created, "checkedOut", "ItemBlock", folder.ContentLink.ID.ToString(CultureInfo.InvariantCulture)), (s.Action, s.Status, s.Type, s.Folder)));
        Assert.Equal(blocks.Select(b => b.ContentLink.ToString()), shared.Select(s => s.Ref));
        var info = Assert.Single(result.Validation!);
        Assert.Equal("info", info.Severity);
        Assert.Contains("Area[0], Area[1] are shared blocks in the content's \"For this page\" folder", info.Message);
        // The change shows the blocks as the area holds them now: by their refs.
        Assert.Contains(blocks[0].ContentLink.ID.ToString(CultureInfo.InvariantCulture), result.Changes.Single(c => c.Property == "Area").After!.Value.GetRawText());
    }

    [Fact]
    public void An_area_add_makes_a_shared_block_and_later_ops_of_the_same_write_change_it()
    {
        var site = new Site();
        var page = site.Page(folder: true, Ref(site.Block(AssetsRoot, "Elsewhere", "x")));
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, ops:
        [
            new AreaOperation { Op = AreaOps.Add, Property = "Area", Type = "ItemBlock", Values = Values("""{"Title": "First"}"""), At = 0 },
            new AreaOperation { Op = AreaOps.Set, Property = "Area", Index = 0, Values = Values("""{"Title": "Changed"}"""), Name = "Named" },
        ]);

        var result = site.Save(flow, writable, before);

        var made = Assert.Single(result.SharedBlocks!);
        Assert.Equal(("Area[0]", "Named", SharedBlockActions.Created), (made.Path, made.Name, made.Action));
        Assert.Equal("Changed", Title(site.Stored(ContentReference.Parse(made.Ref!))));
        Assert.Equal(2, Items(writable).Count);
    }

    [Fact]
    public void A_block_added_and_removed_again_in_the_same_write_isnt_made()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, ops:
        [
            new AreaOperation { Op = AreaOps.Add, Property = "Area", Type = "BoxBlock", Values = Values("""{"Area": [{"type": "ItemBlock"}]}""") },
            new AreaOperation { Op = AreaOps.Add, Property = "Area", Type = "ItemBlock", Name = "Kept" },
            new AreaOperation { Op = AreaOps.Remove, Property = "Area", Index = 0 },
        ]);

        var result = site.Save(flow, writable, before);

        Assert.Equal(("Area[0]", "Kept"), (Assert.Single(result.SharedBlocks!).Path, result.SharedBlocks![0].Name));
        Assert.Single(site.Store.Saves, s => s.Kind == typeof(Shared));
    }

    [Fact]
    public void With_the_edit_ui_making_inline_blocks_nothing_changes()
    {
        var site = new Site();
        var page = site.Page();
        var flow = site.Flow(shared: false);
#if CMS13
        var (writable, before) = site.Write(flow, page, """{"Area": [{"type": "ItemBlock", "properties": {"Title": "Inline"}}]}""");

        var result = site.Save(flow, writable, before);

        Assert.Null(result.SharedBlocks);
        Assert.Null(result.Validation);
        Assert.Equal([typeof(Page)], site.Store.Saves.Select(s => s.Kind));
        Assert.Equal("Inline", InlineBlocks.Of(Items(writable).Single())!.Property["Title"].Value);
#else
        // CMS 12.0 has no inline blocks: refused as before.
        Assert.Contains("need CMS 12.20", Assert.Throws<AgentException>(() => site.Write(flow, page, """{"Area": [{"type": "ItemBlock"}]}""")).Message);
#endif
    }

    [Fact]
    public void A_new_block_inside_a_new_block_is_saved_first_and_both_go_in_the_pages_folder()
    {
        var site = new Site();
        var page = site.Page();
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page,
            """{"Area": [{"type": "BoxBlock", "properties": {"Title": "Outer", "Area": [{"type": "ItemBlock", "properties": {"Title": "Inner"}}]}}]}""");

        var result = site.Save(flow, writable, before);

        Assert.Equal(["Area[0].Area[0]", "Area[0]"], result.SharedBlocks!.Select(s => s.Path));
        var outer = site.Stored(Items(writable).Single().ContentLink);
        var inner = site.Stored(Items(outer).Single().ContentLink);
        Assert.Equal(("Outer", "Inner"), (Title(outer), Title(inner)));
        Assert.Equal(outer.ParentLink, inner.ParentLink);
        Assert.Equal(["Home BoxBlock 1", "Home ItemBlock 1"], new[] { outer.Name, inner.Name });
    }

    [Fact]
    public void A_new_block_in_a_local_blocks_area_goes_in_the_pages_folder()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, ops: [new AreaOperation { Op = AreaOps.Add, Property = "Hero.Area", Type = "ItemBlock", Values = Values("""{"Title": "In hero"}""") }]);

        var result = site.Save(flow, writable, before);

        var made = Assert.Single(result.SharedBlocks!);
        Assert.Equal(("Hero.Area[0]", site.Folder(page).ContentLink.ID.ToString(CultureInfo.InvariantCulture)), (made.Path, made.Folder));
        var hero = (BlockData)writable.Property["Hero"].Value!;
        Assert.Equal("In hero", Title(site.Stored(Items(hero).Single().ContentLink)));
    }

#if CMS13
    [Fact]
    public void A_new_block_in_an_inline_blocks_area_goes_in_the_pages_folder_and_the_inline_block_stays()
    {
        var site = new Site();
        var box = InlineBlockWriteTests.BoxBlock.Create();
        box.Property["Title"].Value = "Box";
        var inline = new ContentAreaItem();
        InlineBlocks.Set(inline, box, BoxType);
        var page = site.Page(folder: true, inline);
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, ops: [new AreaOperation { Op = AreaOps.Add, Property = "Area[0].Area", Type = "ItemBlock", Values = Values("""{"Title": "Nested"}""") }]);

        var result = site.Save(flow, writable, before);

        Assert.Equal("Area[0].Area[0]", Assert.Single(result.SharedBlocks!).Path);
        var stillInline = InlineBlocks.Of(Items(writable).Single())!;
        Assert.Equal("Box", stillInline.Property["Title"].Value);
        Assert.Equal("Nested", Title(site.Stored(Items(stillInline).Single().ContentLink)));
    }
#endif

    [Fact]
    public void New_content_gets_its_folder_first_and_its_blocks_in_it()
    {
        var site = new Site();
        var start = site.Store.Add(new Page { Name = "Start" });
        var flow = site.Flow();
        var page = new Page { Name = "News", ContentLink = ContentReference.EmptyReference, ParentLink = start.ContentLink.ToReferenceWithoutVersion() };
        var before = PropertyValues.Snapshot(page);
        flow.Writer.Apply(page, Values("""{"Area": [{"type": "ItemBlock", "properties": {"Title": "Hello"}}]}"""));

        var result = flow.Save(page, before, SaveAction.Save, dryRun: false, shown: null, baseVersion: null, saveUnchanged: true);

        Assert.Equal([(typeof(ContentAssetFolder), SaveAction.Publish, AccessLevel.NoAccess), (typeof(Shared), SaveAction.Save, AccessLevel.NoAccess)],
            site.Store.Saves.Take(2).Select(s => (s.Kind, s.Action, s.Access!.Value)));
        Assert.Equal(typeof(Page), site.Store.Saves[2].Kind);
        var folder = (ContentAssetFolder)site.Store.Items.Values.Select(v => v[^1].Content).Single(c => c is ContentAssetFolder);
        var saved = (Page)site.Stored(new ContentReference(result.Content!.Id));
        Assert.Equal((saved.ContentGuid, folder.ContentGuid, AssetsRoot), (folder.ContentOwnerID, saved.ContentAssetsID, folder.ParentLink));
        var block = site.Stored(Items(saved).Single().ContentLink);
        Assert.Equal((folder.ContentLink.ID, "News ItemBlock 1"), (block.ParentLink.ID, block.Name));
    }

    [Fact]
    public void A_dry_run_saves_nothing_and_says_what_it_would_make()
    {
        var site = new Site();
        var page = site.Page();
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, """{"Area": [{"type": "ItemBlock", "properties": {"Title": "Hello"}}]}""");

        var result = site.Save(flow, writable, before, dryRun: true);

        Assert.Empty(site.Store.Saves);
        Assert.Null(new Assets(site.Store).GetAssetFolder(page.ContentLink));
        var planned = Assert.Single(result.SharedBlocks!);
        Assert.Equal(("Area[0]", null, null, "Home ItemBlock 1", "checkedOut", SharedBlockActions.Created), (planned.Path, planned.Ref, planned.Folder, planned.Name, planned.Status, planned.Action));
        Assert.True(result.Valid);
    }

    [Fact]
    public void A_block_value_the_site_refuses_stops_the_whole_write_before_anything_is_saved()
    {
        var site = new Site
        {
            Validate = content => content is Shared shared && Equals(shared.Property["Title"].Value, "bad")
                ? [new ValidationError { PropertyName = "Title", ErrorMessage = "Not that.", Severity = ValidationErrorSeverity.Error }]
                : [],
        };
        var page = site.Page();
        const string json = """{"Area": [{"type": "ItemBlock", "properties": {"Title": "fine"}}, {"type": "BoxBlock", "properties": {"Area": [{"type": "ItemBlock", "properties": {"Title": "bad"}}]}}]}""";

        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, json);
        var refused = Assert.Throws<AgentException>(() => site.Save(flow, writable, before));

        Assert.Equal(AgentErrorCodes.Validation, refused.Code);
        Assert.Contains(refused.Validation!, i => i.Property == "Area[1].Area[0].Title" && i.Message == "Not that.");
        Assert.Empty(site.Store.Saves);
        var dry = site.Flow();
        var (dryWritable, dryBefore) = site.Write(dry, page, json);
        Assert.False(site.Save(dry, dryWritable, dryBefore, dryRun: true).Valid);
    }

    [Fact]
    public void The_blocks_go_in_the_writes_project_with_the_page()
    {
        var site = new Site();
        site.Projects.Add(1, "Launch");
        var page = site.Page();
        var flow = site.Flow();
        flow.Project = ProjectSave.For(site.Call, "Launch", ready: false, publish: false, publishAt: null);
        var (writable, before) = site.Write(flow, page, """{"Area": [{"type": "ItemBlock", "properties": {"Title": "Hello"}}]}""");

        var result = site.Save(flow, writable, before);

        var block = ContentReference.Parse(Assert.Single(result.SharedBlocks!).Ref!);
        Assert.Equal([block.ToString(), result.Content!.Ref], site.Projects.Items.Select(i => i.ContentLink.ToString()));
        Assert.Equal("checkedOut", result.SharedBlocks![0].Status);
    }

    [Theory]
    [InlineData("Intro", "Changed since", false)]
    [InlineData(null, "Same", false)]
    [InlineData(null, "Other", true)]
    [InlineData("Other", "Same", true)]
    public void An_add_if_missing_finds_a_block_of_the_pages_folder_by_its_name_or_values(string? name, string title, bool added)
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var own = site.Block(site.Folder(page).ContentLink, "Intro", "Same");
        var elsewhere = site.Block(AssetsRoot, "Other", "Other");
        page = OwnerOf(site, own, Ref(own), Ref(elsewhere));

        var flow = site.Flow();
        var (writable, _) = site.Write(flow, page, ops:
            [new AreaOperation { Op = AreaOps.Add, Property = "Area", Type = "ItemBlock", Name = name, Values = Values($$"""{"Title": "{{title}}"}"""), IfMissing = true }]);

        Assert.Equal(added ? 3 : 2, Items(writable).Count);
        Assert.Equal(added ? 1 : 0, flow.Writer.Shared!.Count);
    }

    /// <summary>The owner of <paramref name="block"/>'s folder, with <paramref name="items"/> in its area.</summary>
    private static Page OwnerOf(Site site, IContent block, params ContentAreaItem[] items)
    {
        var folder = (ContentAssetFolder)site.Store.Latest(block.ParentLink.ID);
        var owner = site.Store.Items.Values.Select(v => v[^1].Content).OfType<Page>().First(p => p.ContentGuid == folder.ContentOwnerID);
        var writable = (Page)((IReadOnly)owner).CreateWritableClone();
        var area = new ContentArea();
        foreach (var item in items)
        {
            area.Items.Add(item);
        }
        writable.Property["Area"].Value = area;
        return (Page)site.Store.Get(site.Store.Save(writable, SaveAction.Publish, null))!;
    }

    [Fact]
    public void A_block_of_the_pages_folder_given_whole_again_is_updated_and_an_exact_copy_left_as_it_is()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var changed = site.Block(site.Folder(page).ContentLink, "Intro", "Old");
        var copied = site.Block(site.Folder(page).ContentLink, "Kept", "Kept");
        var elsewhere = site.Block(AssetsRoot, "Global", "Global");
        page = OwnerOf(site, changed, Ref(changed), Ref(copied), Ref(elsewhere));
        site.Store.Saves.Clear();
        var flow = site.Flow();
        // As a plan run again gives it: each block whole, the global one by its ref.
        var (writable, before) = site.Write(flow, page, $$$"""
            {"Area": [
              {"type": "ItemBlock", "name": "Intro", "properties": {"Title": "New"}},
              {"type": "ItemBlock", "name": "Kept", "properties": {"Title": "Kept"}},
              {"ref": "{{{elsewhere.ContentLink.ID}}}"}
            ]}
            """);

        var result = site.Save(flow, writable, before);

        var updated = Assert.Single(result.SharedBlocks!);
        Assert.Equal(("Area[0]", SharedBlockActions.Updated, "Intro", "checkedOut"), (updated.Path, updated.Action, updated.Name, updated.Status));
        Assert.Equal([changed.ContentLink.ID, copied.ContentLink.ID, elsewhere.ContentLink.ID], Items(writable).Select(i => i.ContentLink.ID));
        Assert.Equal("New", Title(site.Store.Latest(changed.ContentLink.ID)));
        Assert.Equal(VersionStatus.Published, ((IVersionable)site.Stored(changed.ContentLink.ToReferenceWithoutVersion())).Status);
        Assert.Equal([("Intro", typeof(Shared), SaveAction.Save | SaveAction.ForceNewVersion)], site.Store.Saves.Where(s => s.Kind == typeof(Shared)).Select(s => (s.Name, s.Kind, s.Action)));
        // The page's preview shows the block's published version until the block is published.
        Assert.Contains(result.Validation!, i => (i.Property, i.Severity) == ("Area[0]", "info") && i.Message.Contains("shows the block's published version"));
    }

    [Fact]
    public void A_block_from_elsewhere_is_never_taken_over()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var anchor = site.Block(site.Folder(page).ContentLink, "Anchor", "Anchor", type: BoxType);
        var elsewhere = site.Block(AssetsRoot, "Global", "Global");
        page = OwnerOf(site, anchor, Ref(elsewhere));
        site.Store.Saves.Clear();
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, """{"Area": [{"type": "ItemBlock", "properties": {"Title": "Global changed"}}]}""");

        var result = site.Save(flow, writable, before);

        Assert.Equal(SharedBlockActions.Created, Assert.Single(result.SharedBlocks!).Action);
        Assert.Equal("Global", Title(site.Store.Latest(elsewhere.ContentLink.ID)));
        Assert.NotEqual(elsewhere.ContentLink.ID, Items(writable).Single().ContentLink.ID);
    }

    [Fact]
    public void A_publish_publishes_the_drafts_of_the_pages_own_blocks_along_and_no_others()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var folder = site.Folder(page).ContentLink;
        var draft = site.Block(folder, "Draft", "Draft", VersionStatus.CheckedOut);
        var theirs = site.Block(folder, "Theirs", "Theirs", VersionStatus.CheckedOut, savedBy: "someone");
        var global = site.Block(AssetsRoot, "Global draft", "Global", VersionStatus.CheckedOut);
        page = OwnerOf(site, draft, Ref(draft), Ref(theirs), Ref(global));
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, ops: [new AreaOperation { Op = AreaOps.Add, Property = "Area", Type = "ItemBlock", Values = Values("""{"Title": "New"}""") }]);

        var result = site.Save(flow, writable, before, action: SaveAction.Publish | SaveAction.ForceNewVersion);

        Assert.True(result.Published);
        var shared = result.SharedBlocks!;
        Assert.Equal([("Area[3]", SharedBlockActions.Created, "published"), ("Area[0]", SharedBlockActions.Published, "published")],
            shared.Select(s => (s.Path, s.Action, s.Status)));
        Assert.All(shared, s => Assert.Equal(VersionStatus.Published, ((IVersionable)site.Stored(ContentReference.Parse(s.Ref!))).Status));
        Assert.Equal(VersionStatus.CheckedOut, ((IVersionable)site.Store.Latest(theirs.ContentLink.ID)).Status);
        Assert.Equal(VersionStatus.CheckedOut, ((IVersionable)site.Store.Latest(global.ContentLink.ID)).Status);
        Assert.Contains(result.Validation!, i => i.Severity == "warning" && i.Property == "Area[1]" && i.Message.Contains("someone saved"));
    }

    [Fact]
    public void A_dry_run_of_a_publish_lists_the_drafts_it_would_publish_along()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var draft = site.Block(site.Folder(page).ContentLink, "Draft", "Draft", VersionStatus.CheckedOut);
        page = OwnerOf(site, draft, Ref(draft));
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, """{"Title": "Changed"}""");

        var result = site.Save(flow, writable, before, dryRun: true, action: SaveAction.Publish | SaveAction.ForceNewVersion);

        var along = Assert.Single(result.SharedBlocks!);
        Assert.Equal(("Area[0]", SharedBlockActions.Published, draft.ContentLink.ToString()), (along.Path, along.Action, along.Ref));
        Assert.Equal(VersionStatus.CheckedOut, ((IVersionable)site.Store.Latest(draft.ContentLink.ID)).Status);
    }

    [Fact]
    public void An_editor_needs_create_and_edit_on_the_page_and_the_blocks_are_saved_unchecked()
    {
        var site = new Site(CmsCaller.Editor);
        var page = site.Page(folder: true);
        page.Language = page.MasterLanguage = CultureInfo.InvariantCulture;
        site.Store.Items[page.ContentLink.ID].ForEach(v => ((Page)v.Content).Language = ((Page)v.Content).MasterLanguage = CultureInfo.InvariantCulture);
        const string json = """{"Area": [{"type": "ItemBlock", "properties": {"Title": "Hello"}}]}""";

        site.Store.Items[page.ContentLink.ID].ForEach(v => ((Page)v.Content).Access = AccessLevel.Read | AccessLevel.Edit);
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, json);
        var refused = Assert.Throws<AgentException>(() => site.Save(flow, writable, before, dryRun: true));
        Assert.Equal(AgentErrorCodes.Refused, refused.Code);
        Assert.Contains("Create access", refused.Message);

        site.Store.Items[page.ContentLink.ID].ForEach(v => ((Page)v.Content).Access = AccessLevel.Read | AccessLevel.Edit | AccessLevel.Create);
        var allowed = site.Flow();
        (writable, before) = site.Write(allowed, page, json);
        site.Availability.Offered.Remove(ItemType);
        Assert.Contains("may not create ItemBlock", Assert.Throws<AgentException>(() => site.Write(site.Flow(), page, json)).Message);
        site.Availability.Offered.Add(ItemType);

        var result = site.Save(allowed, writable, before);

        Assert.Equal(SharedBlockActions.Created, Assert.Single(result.SharedBlocks!).Action);
        Assert.Equal([(typeof(Shared), (AccessLevel?)AccessLevel.NoAccess), (typeof(Page), null)], site.Store.Saves.Select(s => (s.Kind, s.Access)));
    }

    [Fact]
    public void A_failed_save_of_the_page_names_the_blocks_it_made()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, """{"Area": [{"type": "ItemBlock", "properties": {"Title": "Hello"}}]}""");
        site.Store.Failing.Add("Home");

        var error = Assert.Throws<AgentException>(() => site.Save(flow, writable, before));

        Assert.Equal(AgentErrorCodes.Internal, error.Code);
        Assert.Contains("The database refused it.", error.Message);
        var made = Assert.Single(error.MadeBlocks!);
        Assert.Equal(("Area[0]", SharedBlockActions.Created), (made.Path, made.Action));
        Assert.Contains(made.Ref!, error.Hint);
        Assert.Equal(VersionStatus.CheckedOut, ((IVersionable)site.Stored(ContentReference.Parse(made.Ref!))).Status);
    }

    [Fact]
    public void A_positional_write_onto_a_shared_item_is_refused_with_the_blocks_ref()
    {
        var site = new Site();
        var page = site.Page(folder: true, new ContentAreaItem { ContentLink = new ContentReference(123) });
        var flow = site.Flow();

        var refused = Assert.Throws<AgentException>(() => site.Write(flow, page, ops: [new AreaOperation { Op = AreaOps.Add, Property = "Area[0].Area", Type = "ItemBlock" }]));

        Assert.Contains("Area[0] is the shared block 123", refused.Message);
        Assert.Contains("opticli set 123", refused.Hint);
    }

    [Fact]
    public void A_block_saved_after_the_page_keeps_the_site_changes_of_the_pages_save()
    {
        // PublishOperation publishes the page, then its blocks along (CmsCall.SaveAsset), then reports the site's changes.
        var site = new Site();
        var page = site.Page(folder: true);
        var draft = site.Block(site.Folder(page).ContentLink, "Draft", "Draft", VersionStatus.CheckedOut);
        // A site handler of the publish events: it publishes other content too (a search index entry, say).
        var indexed = site.Block(AssetsRoot, "Indexed", "Indexed");
        site.Store.Saved = (content, action) =>
        {
            if ((action & SaveAction.ActionMask) == SaveAction.Publish)
            {
                site.Raise("PublishedContent", new ContentEventArgs(indexed.ContentLink, indexed));
            }
        };

        site.Call.Save((IContent)((IReadOnly)page).CreateWritableClone(), SaveAction.Publish | SaveAction.ForceNewVersion);
        site.Call.SaveAsset((IContent)((IReadOnly)draft).CreateWritableClone(), SaveAction.Publish);

        // The page's, not the block's: that one is the write's own (sharedBlocks), not the site's.
        Assert.Equal(indexed.ContentLink, Assert.Single(site.Call.TakeSiteEvents()).Link);
    }

    [Fact]
    public void A_new_block_in_the_area_of_a_block_taken_over_goes_in_the_pages_folder_and_is_saved_first()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var folder = site.Folder(page).ContentLink;
        var outer = site.Block(folder, "Outer", "Old", type: BoxType);
        page = OwnerOf(site, outer, Ref(outer));
        site.Store.Saves.Clear();
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page,
            """{"Area": [{"type": "BoxBlock", "name": "Outer", "properties": {"Title": "New", "Area": [{"type": "ItemBlock", "properties": {"Title": "Inner"}}]}}]}""");

        var result = site.Save(flow, writable, before);

        Assert.Equal([("Area[0].Area[0]", SharedBlockActions.Created), ("Area[0]", SharedBlockActions.Updated)], result.SharedBlocks!.Select(s => (s.Path, s.Action)));
        Assert.Equal(outer.ContentLink.ID, Items(writable).Single().ContentLink.ID);
        var updated = site.Store.Latest(outer.ContentLink.ID);
        var inner = site.Stored(Items(updated).Single().ContentLink);
        Assert.Equal(("New", "Inner", folder.ID), (Title(updated), Title(inner), inner.ParentLink.ID));
    }

    [Fact]
    public void An_editor_cant_change_a_block_of_the_pages_folder_they_may_not_edit_by_writing_the_area_whole()
    {
        var site = new Site(CmsCaller.Editor);
        var page = Neutral(site, site.Page(folder: true));
        var locked = Neutral(site, site.Block(site.Folder(page).ContentLink, "Intro", "Old"));
        ((Shared)site.Store.Latest(locked.ContentLink.ID)).Access = AccessLevel.Read;
        page = OwnerOf(site, locked, Ref(locked));
        site.Store.Saves.Clear();
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, """{"Area": [{"type": "ItemBlock", "name": "Intro", "properties": {"Title": "New"}}]}""");

        var refused = Assert.Throws<AgentException>(() => site.Save(flow, writable, before));

        Assert.Equal(AgentErrorCodes.Refused, refused.Code);
        Assert.Empty(site.Store.Saves);
        Assert.Equal("Old", Title(site.Store.Latest(locked.ContentLink.ID)));
    }

    [Fact]
    public void A_publish_doesnt_publish_along_a_block_draft_the_editor_may_not_publish()
    {
        var site = new Site(CmsCaller.Editor);
        var page = Neutral(site, site.Page(folder: true));
        var draft = Neutral(site, site.Block(site.Folder(page).ContentLink, "Draft", "Draft", VersionStatus.CheckedOut, savedBy: "editor"));
        ((Shared)site.Store.Latest(draft.ContentLink.ID)).Access = AccessLevel.Read | AccessLevel.Edit;
        page = OwnerOf(site, draft, Ref(draft));
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, """{"Title": "Changed"}""");

        var result = site.Save(flow, writable, before, action: SaveAction.Publish | SaveAction.ForceNewVersion);

        Assert.True(result.Published);
        Assert.Null(result.SharedBlocks);
        Assert.Equal(VersionStatus.CheckedOut, ((IVersionable)site.Store.Latest(draft.ContentLink.ID)).Status);
        Assert.Contains(result.Validation!, i => (i.Property, i.Severity) == ("Area[0]", "warning") && i.Message.Contains("Publish access"));
    }

    [Fact]
    public void An_editor_gets_no_type_the_edit_ui_doesnt_offer_for_a_new_block_of_the_page()
    {
        var site = new Site(CmsCaller.Editor);
        var page = Neutral(site, site.Page(folder: true));
        site.Availability.OfferedForContent = [BoxType];
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, """{"Area": [{"type": "ItemBlock", "properties": {"Title": "Hello"}}]}""");

        var dry = site.Save(flow, writable, before, dryRun: true);
        var refused = Assert.Throws<AgentException>(() => site.Save(flow, writable, before));

        Assert.False(dry.Valid);
        Assert.Equal(AgentErrorCodes.Validation, refused.Code);
        Assert.Contains(refused.Validation!, i => i.Property == "Area[0]" && i.Message.Contains("doesn't offer ItemBlock"));
        Assert.Empty(site.Store.Saves);
    }

    [Fact]
    public void Several_blocks_of_the_pages_folder_of_a_type_given_changed_by_their_names_are_updated_not_made_again()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var folder = site.Folder(page).ContentLink;
        var first = site.Block(folder, "First", "One");
        var second = site.Block(folder, "Second", "Two");
        page = OwnerOf(site, first, Ref(first), Ref(second));
        site.Store.Saves.Clear();
        var flow = site.Flow();
        // Both changed, in the other order, and a new one of the type: no "only one of its type left" pairing applies.
        var (writable, before) = site.Write(flow, page, """
            {"Area": [
              {"type": "ItemBlock", "name": "Second", "properties": {"Title": "Two changed"}},
              {"type": "ItemBlock", "name": "First", "properties": {"Title": "One changed"}},
              {"type": "ItemBlock", "name": "Third", "properties": {"Title": "Three"}}
            ]}
            """);

        var result = site.Save(flow, writable, before);

        Assert.Equal([("Area[0]", SharedBlockActions.Updated, "Second"), ("Area[1]", SharedBlockActions.Updated, "First"), ("Area[2]", SharedBlockActions.Created, "Third")],
            result.SharedBlocks!.Select(s => (s.Path, s.Action, s.Name)));
        Assert.Equal([second.ContentLink.ID, first.ContentLink.ID], Items(writable).Take(2).Select(i => i.ContentLink.ID));
        Assert.Equal(("One changed", "Two changed"), (Title(site.Store.Latest(first.ContentLink.ID)), Title(site.Store.Latest(second.ContentLink.ID))));
    }

    /// <summary>An item showing an inline <paramref name="type"/> block with that title, named in the area as <paramref name="name"/>.</summary>
    private static ContentAreaItem Inline(string name, string title, int type = ItemType)
    {
        var block = type == BoxType ? (BlockData)InlineBlockWriteTests.BoxBlock.Create() : PropertyWriterTests.ItemBlock.Create();
        block.Property["Title"].Value = title;
        var item = new ContentAreaItem();
        InlineBlocks.Set(item, block, type);
        CmsApi.SetRenderSetting(item, InlineBlocks.NameKey, name);
        return item;
    }

    [Fact]
    public void A_review_request_is_refused_while_the_page_has_new_or_draft_blocks_of_its_folder()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var draft = site.Block(site.Folder(page).ContentLink, "Draft", "Draft", VersionStatus.CheckedOut);
        page = OwnerOf(site, draft, Ref(draft));
        site.Store.Saves.Clear();
        const SaveAction review = SaveAction.RequestApproval | SaveAction.ForceNewVersion;

        // A new block, in a dry run too.
        foreach (var dryRun in new[] { true, false })
        {
            var flow = site.Flow();
            var (writable, before) = site.Write(flow, page, ops: [new AreaOperation { Op = AreaOps.Add, Property = "Area", Type = "ItemBlock", Values = Values("""{"Title": "New"}""") }]);
            var refused = Assert.Throws<AgentException>(() => site.Save(flow, writable, before, dryRun, review));
            Assert.Equal(AgentErrorCodes.Usage, refused.Code);
            Assert.Contains("Area[1] (a new block)", refused.Message);
            Assert.Contains($"Area[0] (block {draft.ContentLink.ID}, a draft)", refused.Message);
            Assert.Contains("CMS project", refused.Hint);
        }
        // Only a draft block the page shows already.
        var titled = site.Flow();
        var (changed, changedBefore) = site.Write(titled, page, """{"Title": "Changed"}""");
        Assert.Contains("Area[0] (block", Assert.Throws<AgentException>(() => site.Save(titled, changed, changedBefore, action: review)).Message);
        Assert.Empty(site.Store.Saves);
        // The publish operation asks the same of the version it sends for review.
        Assert.Throws<AgentException>(() => site.Flow().RequireBlocksPublishedForReview(page, review, "page"));

        // In a project, the page and its blocks are reviewed together; and once the block is published, nothing stops it.
        site.Projects.Add(1, "Launch");
        var inProject = site.Flow();
        inProject.Project = ProjectSave.For(site.Call, "Launch", ready: false, publish: false, publishAt: null);
        inProject.RequireBlocksPublishedForReview(page, review, "page");
        site.Store.Save((IContent)((IReadOnly)site.Store.Latest(draft.ContentLink.ID)).CreateWritableClone(), SaveAction.Publish, null);
        site.Flow().RequireBlocksPublishedForReview(page, review, "page");
        // Where the edit UI makes inline blocks, as before.
        site.Flow(shared: false).RequireBlocksPublishedForReview(page, review, "page");
    }

    [Fact]
    public void A_dry_run_shows_a_planned_block_in_the_changes_as_what_it_will_be()
    {
        var site = new Site { Defaults = block => block.Property["Secret"].Value = "from the type" };
        var page = site.Page(folder: true);
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page,
            """{"Area": [{"type": "BoxBlock", "name": "Outer", "displayOption": null, "properties": {"Title": "Box", "Area": [{"type": "ItemBlock", "properties": {"Title": "Inner"}}]}}]}""");

        var result = site.Save(flow, writable, before, dryRun: true);

        var item = JsonNodeOf(result.Changes.Single(c => c.Property == "Area").After!.Value)!.AsArray().Single()!.AsObject();
        Assert.Equal(("BoxBlock", "Outer", "planned", "Box"), ((string?)item["type"], (string?)item["name"], (string?)item["shared"], (string?)item["properties"]!["Title"]));
        Assert.Null(item["guid"]);
        var inner = item["properties"]!["Area"]!.AsArray().Single()!.AsObject();
        Assert.Equal(("ItemBlock", "Home ItemBlock 1", "planned", "Inner"), ((string?)inner["type"], (string?)inner["name"], (string?)inner["shared"], (string?)inner["properties"]!["Title"]));
        // The developer sees every value; an editor only what the edit UI shows them.
        Assert.Equal("from the type", (string?)inner["properties"]!["Secret"]);

        var editorSite = new Site(CmsCaller.Editor) { Defaults = block => block.Property["Secret"].Value = "from the type" };
        var editorPage = Neutral(editorSite, editorSite.Page(folder: true));
        var editorFlow = editorSite.Flow();
        var (editorWritable, editorBefore) = editorSite.Write(editorFlow, editorPage, """{"Area": [{"type": "ItemBlock", "properties": {"Title": "Mine"}}]}""");
        var shown = JsonNodeOf(editorSite.Save(editorFlow, editorWritable, editorBefore, dryRun: true).Changes.Single(c => c.Property == "Area").After!.Value)!.AsArray().Single()!;
        Assert.Equal("Mine", (string?)shown["properties"]!["Title"]);
        Assert.Null(shown["properties"]!["Secret"]);
        Assert.DoesNotContain("from the type", shown.ToJsonString());
    }

    private static System.Text.Json.Nodes.JsonNode? JsonNodeOf(JsonElement element) => System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText());

    [Fact]
    public void Inline_blocks_an_area_has_stay_inline_when_written_whole_by_their_names_and_only_new_items_become_shared()
    {
        var site = new Site();
        var page = site.Page(folder: true, Inline("Intro", "One"), Inline("Outro", "Two"));
        site.Store.Saves.Clear();
        var flow = site.Flow();
        // Both changed and a new one of the type: the inline rules alone pair none of them (two blocks, three items).
        var (writable, before) = site.Write(flow, page, """
            {"Area": [
              {"type": "ItemBlock", "name": "Outro", "properties": {"Title": "Two changed"}},
              {"type": "ItemBlock", "name": "Intro", "properties": {"Title": "One changed"}},
              {"type": "ItemBlock", "name": "Third", "properties": {"Title": "Three"}}
            ]}
            """);

        var result = site.Save(flow, writable, before);

        var items = Items(writable);
        Assert.Equal(["Outro", "Intro"], items.Take(2).Select(i => InlineBlocks.Name(i)));
        Assert.Equal(["Two changed", "One changed"], items.Take(2).Select(i => InlineBlocks.Of(i)!.Property["Title"].Value));
        Assert.Null(InlineBlocks.Of(items[2]));
        Assert.Equal(("Area[2]", "Third", SharedBlockActions.Created), (Assert.Single(result.SharedBlocks!).Path, result.SharedBlocks![0].Name, result.SharedBlocks![0].Action));

        // Where the edit UI makes inline blocks, as before: three new inline blocks.
        var inline = site.Flow(shared: false);
        var (inlineWritable, _) = site.Write(inline, page, """
            {"Area": [
              {"type": "ItemBlock", "name": "Outro", "properties": {"Title": "Two changed"}},
              {"type": "ItemBlock", "name": "Intro", "properties": {"Title": "One changed"}},
              {"type": "ItemBlock", "name": "Third", "properties": {"Title": "Three"}}
            ]}
            """);
        Assert.All(Items(inlineWritable), i => Assert.NotNull(InlineBlocks.Of(i)));
    }

    [Fact]
    public void After_names_the_only_block_of_its_type_left_inline_or_in_the_folder_is_taken_over()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var folderBlock = site.Block(site.Folder(page).ContentLink, "Teaser", "Folder");
        page = OwnerOf(site, folderBlock, Inline("Intro", "One"), Ref(folderBlock), Inline("Spare", "Two"));
        site.Store.Saves.Clear();
        var flow = site.Flow();
        // The folder block and an inline block by their names; then one item and one inline block of the type left.
        var (writable, before) = site.Write(flow, page, """
            {"Area": [
              {"type": "ItemBlock", "name": "Teaser", "properties": {"Title": "Folder changed"}},
              {"type": "ItemBlock", "name": "Intro", "properties": {"Title": "One changed"}},
              {"type": "ItemBlock", "name": "Renamed", "properties": {"Title": "Two changed"}}
            ]}
            """);

        var result = site.Save(flow, writable, before);

        var items = Items(writable);
        Assert.Equal(folderBlock.ContentLink.ID, items[0].ContentLink.ID);
        Assert.Equal(("Intro", "One changed"), (InlineBlocks.Name(items[1]), InlineBlocks.Of(items[1])!.Property["Title"].Value));
        Assert.Equal(("Renamed", "Two changed"), (InlineBlocks.Name(items[2]), InlineBlocks.Of(items[2])!.Property["Title"].Value));
        Assert.Equal([("Area[0]", SharedBlockActions.Updated)], result.SharedBlocks!.Select(s => (s.Path, s.Action)));
        Assert.Equal("Folder changed", Title(site.Store.Latest(folderBlock.ContentLink.ID)));
    }

    [Fact]
    public void A_name_two_blocks_have_pairs_neither_and_the_item_becomes_a_new_shared_block()
    {
        var site = new Site();
        var page = site.Page(folder: true, Inline("Same", "One"), Inline("Same", "Two"));
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, """
            {"Area": [
              {"type": "ItemBlock", "name": "Same", "properties": {"Title": "Changed"}},
              {"type": "ItemBlock", "name": "Other", "properties": {"Title": "New"}}
            ]}
            """);

        var result = site.Save(flow, writable, before);

        Assert.All(Items(writable), i => Assert.Null(InlineBlocks.Of(i)));
        Assert.Equal(2, result.SharedBlocks!.Count(s => s.Action == SharedBlockActions.Created));
    }

    [Fact]
    public void A_project_publish_leaves_the_projects_own_block_drafts_to_the_project_and_warns_of_one_left_behind()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var folder = site.Folder(page).ContentLink;
        var notReady = site.Block(folder, "Not ready", "x", VersionStatus.CheckedOut);
        var ready = site.Block(folder, "Ready", "y", VersionStatus.CheckedOut);
        var outside = site.Block(folder, "Outside", "z", VersionStatus.CheckedOut);
        page = OwnerOf(site, notReady, Ref(notReady), Ref(ready), Ref(outside));
        bool? InProject(int id, CultureInfo? language) => id == notReady.ContentLink.ID ? false : id == ready.ContentLink.ID ? true : null;

        var warnings = new List<ValidationIssue>();
        var along = site.Flow().PublishAlong(page, SaveAction.Publish, othersConfirmed: false, dryRun: false, warnings, InProject)!;

        Assert.Equal([outside.ContentLink.ID], along.Select(a => ContentReference.Parse(a.Ref!).ID));
        Assert.Equal(VersionStatus.CheckedOut, ((IVersionable)site.Store.Latest(notReady.ContentLink.ID)).Status);
        Assert.Equal(VersionStatus.CheckedOut, ((IVersionable)site.Store.Latest(ready.ContentLink.ID)).Status);
        var warning = Assert.Single(warnings);
        Assert.Equal(("Area[0]", "warning"), (warning.Property, warning.Severity));
        Assert.Contains("isn't marked Ready to publish", warning.Message);
    }

    [Fact]
    public void An_add_if_missing_that_finds_a_block_of_the_folder_lists_it_as_existing()
    {
        var site = new Site();
        var page = site.Page(folder: true);
        var own = site.Block(site.Folder(page).ContentLink, "Intro", "Same");
        page = OwnerOf(site, own, Ref(site.Block(AssetsRoot, "Global", "g")), Ref(own));
        site.Store.Saves.Clear();
        AreaOperation[] add = [new AreaOperation { Op = AreaOps.Add, Property = "Area", Type = "ItemBlock", Name = "Intro", Values = Values("""{"Title": "Changed since"}"""), IfMissing = true }];

        var dryFlow = site.Flow();
        var (dryWritable, dryBefore) = site.Write(dryFlow, page, ops: add);
        var dry = site.Save(dryFlow, dryWritable, dryBefore, dryRun: true);
        var flow = site.Flow();
        var (writable, before) = site.Write(flow, page, ops: add);
        var result = site.Save(flow, writable, before);

        foreach (var found in new[] { Assert.Single(dry.SharedBlocks!), Assert.Single(result.SharedBlocks!) })
        {
            Assert.Equal(("Area[1]", SharedBlockActions.Existing, "Intro", own.ContentLink.ID), (found.Path, found.Action, found.Name, ContentReference.Parse(found.Ref!).ID));
        }
        Assert.False(result.Saved);
        Assert.Empty(site.Store.Saves);
    }

    /// <summary><paramref name="content"/> with no language of its own (every version), so an editor's language access isn't asked.</summary>
    private static T Neutral<T>(Site site, T content) where T : IContent
    {
        foreach (var (version, _) in site.Store.Items[content.ContentLink.ID])
        {
            switch (version)
            {
                case Page page:
                    page.Language = page.MasterLanguage = CultureInfo.InvariantCulture;
                    break;
                case Shared block:
                    block.Language = block.MasterLanguage = CultureInfo.InvariantCulture;
                    break;
            }
        }
        return (T)site.Store.Latest(content.ContentLink.ID);
    }
#endif
}
