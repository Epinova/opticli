using System.Globalization;
using System.Reflection;
using System.Security.Principal;
using EPiServer;
using EPiServer.Approvals;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Security;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Cms;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Cms;

/// <summary>
/// How each caller reaches the CMS: what <see cref="CmsCall"/> passes to the repository, and which content it lets an
/// editor see or change. The repository is a recording stand-in, so no site is needed.
/// </summary>
public class CmsCallTests
{
    private const string EditorName = "editor@example.com";

    private static readonly ContentReference Page = new(123);

    private readonly Recorder<IContentRepository> _repository;

    private readonly IServiceProvider _services;

    public CmsCallTests()
    {
        var (repository, recorder) = Recorder<IContentRepository>.Create();
        _repository = recorder;
        _services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(Recorder<IContentVersionRepository>.Create().Proxy)
            .AddSingleton(EditableLanguages())
            .AddSingleton<IPrincipalAccessor>(new PrincipalAccessor(new GenericPrincipal(new GenericIdentity(EditorName), [])))
            .BuildServiceProvider();
    }

    /// <summary>Languages the editor may edit, every one.</summary>
    private static ILanguageBranchRepository EditableLanguages()
    {
        var (languages, recorder) = Recorder<ILanguageBranchRepository>.Create();
        recorder.Answer = (method, args) => method.Name == nameof(ILanguageBranchRepository.Load) && args[0] is CultureInfo culture ? new Branch(culture, edit: true) : null;
        return languages;
    }

    private CmsCall Call(CmsCaller caller) => new(_services, CancellationToken.None, caller);

    [Fact]
    public void A_hint_names_the_next_step_in_the_caller_s_own_terms()
    {
        Assert.Equal("opticli delete", Call(CmsCaller.Developer).ForCaller("opticli delete", "delete_content"));
        Assert.Equal("delete_content", Call(CmsCaller.Editor).ForCaller("opticli delete", "delete_content"));
    }

    [Fact]
    public void The_developer_saves_unchecked_and_an_editor_with_the_access_the_CMS_requires()
    {
        var content = new Secured(Page, AccessLevel.FullAccess);

        Call(CmsCaller.Developer).Save(content, SaveAction.Publish);
        Call(CmsCaller.Editor).Save(content, SaveAction.Publish);

        // Undefined makes the CMS work out the level from the action (Publish here), as the edit UI's saves do.
        Assert.Equal(
            [("Save", AccessLevel.NoAccess), ("Save", AccessLevel.Undefined)],
            _repository.Calls.Select(c => (c.Method, (AccessLevel)c.Args[2]!)));
    }

    [Fact]
    public void A_save_that_publishes_or_schedules_passes_the_callers_publishing_gate_first()
    {
        var content = new Versioned(Page, AccessLevel.FullAccess);
        var gated = new List<SaveAction>();
        var call = new CmsCall(_services, CancellationToken.None, CmsCaller.Editor, () => throw new InvalidOperationException("publishing off"));
        var counting = new CmsCall(_services, CancellationToken.None, CmsCaller.Editor, () => gated.Add(default));

        foreach (var action in new[] { SaveAction.Publish, SaveAction.Publish | SaveAction.ForceNewVersion, SaveAction.Schedule })
        {
            Assert.Equal("publishing off", Assert.Throws<InvalidOperationException>(() => call.Save(content, action)).Message);
        }
        Assert.Empty(_repository.Calls);
        call.Save(content, SaveAction.Save | SaveAction.ForceNewVersion);
        call.Save(content, SaveAction.RequestApproval);
        counting.Save(content, SaveAction.Publish);
        Call(CmsCaller.Developer).Save(content, SaveAction.Publish);

        Assert.Single(gated);
        Assert.Equal(4, _repository.Calls.Count(c => c.Method == "Save"));
    }

    [Fact]
    public void A_save_of_content_without_versions_is_live_at_once_and_passes_the_publishing_gate_but_a_folders_doesnt()
    {
        var gated = 0;
        var call = new CmsCall(_services, CancellationToken.None, CmsCaller.Editor, () => gated++);

        call.Save(new Secured(Page, AccessLevel.FullAccess), SaveAction.Save);
        call.Save(new Versioned(Page, AccessLevel.FullAccess), SaveAction.Save);
        call.Save(new ContentFolder { ContentLink = Page }, SaveAction.Save);

        Assert.Equal(1, gated);
        Assert.Equal(3, _repository.Calls.Count(c => c.Method == "Save"));
    }

    [Fact]
    public void The_deleting_gate_runs_when_asked_for_and_only_where_there_is_one()
    {
        var call = new CmsCall(_services, CancellationToken.None, CmsCaller.Editor, deleting: () => throw new InvalidOperationException("deleting off"));

        Assert.Equal("deleting off", Assert.Throws<InvalidOperationException>(call.RequireDeleting).Message);
        Call(CmsCaller.Developer).RequireDeleting();
        Call(CmsCaller.Editor).RequireDeleting();
    }

    [Fact]
    public void Only_an_editor_is_held_to_the_edit_UIs_rules_for_script_text_references_and_live_moves()
    {
        Assert.True(Call(CmsCaller.Developer).MayWriteScript);
        Assert.True(Call(CmsCaller.Developer).MayReferenceUnchecked);
        Assert.False(Call(CmsCaller.Developer).ChecksLiveMoves);
        Assert.False(Call(CmsCaller.Editor).MayWriteScript);
        Assert.False(Call(CmsCaller.Editor).MayReferenceUnchecked);
        Assert.True(Call(CmsCaller.Editor).ChecksLiveMoves);
    }

    [Fact]
    public void An_editor_saves_and_deletes_a_language_branch_only_with_edit_access_to_the_language()
    {
        var swedish = CultureInfo.GetCultureInfo("sv");
        var languages = Recorder<ILanguageBranchRepository>.Create();
        languages.Recorder.Answer = (method, args) => method.Name == nameof(ILanguageBranchRepository.Load) && args[0] is CultureInfo culture
            ? new Branch(culture, edit: culture.Name == "en")
            : null;
        var services = new ServiceCollection()
            .AddSingleton((IContentRepository)(object)_repository)
            .AddSingleton(languages.Proxy)
            .AddSingleton(Recorder<IContentVersionRepository>.Create().Proxy)
            .AddSingleton<IPrincipalAccessor>(new PrincipalAccessor(new GenericPrincipal(new GenericIdentity(EditorName), [])))
            .BuildServiceProvider();
        var editor = new CmsCall(services, CancellationToken.None, CmsCaller.Editor);
        var developer = new CmsCall(services, CancellationToken.None, CmsCaller.Developer);

        var refused = Assert.Throws<AgentException>(() => editor.Save(new Localized(Page, swedish), SaveAction.Save));
        Assert.Equal(AgentErrorCodes.Refused, refused.Code);
        Assert.Contains("'sv'", refused.Message);
        Assert.Throws<AgentException>(() => editor.DeleteLanguageBranch(Page, "sv"));
        Assert.Throws<AgentException>(() => editor.DeleteVersion(new Localized(new ContentReference(123, 7), swedish)));
        Assert.Empty(_repository.Calls);

        editor.Save(new Localized(Page, CultureInfo.GetCultureInfo("en")), SaveAction.Save);
        developer.Save(new Localized(Page, swedish), SaveAction.Save);
        developer.DeleteLanguageBranch(Page, "sv");
        Assert.Equal(["Save", "Save", "DeleteLanguageBranch"], _repository.Calls.Select(c => c.Method));
    }

    [Fact]
    public void An_editor_may_create_what_the_edit_UI_offers_below_the_parent_itself_when_it_is_known()
    {
        var article = new ContentType { ID = 7, Name = "ArticlePage" };
        var parentType = new ContentType { ID = 3, Name = "StartPage" };
        var availability = new Availability(byName: [article], byContent: []);
        var services = new ServiceCollection()
            .AddSingleton<ContentTypeAvailabilityService>(availability)
            .AddSingleton<IPrincipalAccessor>(new PrincipalAccessor(new GenericPrincipal(new GenericIdentity(EditorName), [])))
            .BuildServiceProvider();
        var editor = new CmsCall(services, CancellationToken.None, CmsCaller.Editor);
        var parent = new Secured(new ContentReference(5), AccessLevel.FullAccess) { ContentTypeID = parentType.ID };

        // The type's own access rights allow it, but its group's required access on this parent doesn't.
        Assert.False(editor.MayCreate(article, parentType, parent));
        Assert.Equal("content", availability.Asked);
        // A dry run's stand-in parent, of another type, is checked by the type's own access rights.
        Assert.True(editor.MayCreate(article, parentType, new Secured(new ContentReference(6), AccessLevel.FullAccess) { ContentTypeID = 99 }));
        Assert.Equal("name", availability.Asked);
        Assert.True(new CmsCall(services, CancellationToken.None, CmsCaller.Developer).MayCreate(article, parentType, parent));
    }

    [Fact]
    public void Only_the_developer_publishes_through_a_review_request_without_a_sequence_and_restores_from_the_recycle_bin()
    {
        // opticli publish --request-approval puts content without a sequence live; an editor's requestApproval never does.
        Assert.True(Call(CmsCaller.Developer).RequestApprovalMayPublish);
        Assert.False(Call(CmsCaller.Editor).RequestApprovalMayPublish);
        Assert.True(Call(CmsCaller.Developer).MayRestore);
        Assert.False(Call(CmsCaller.Editor).MayRestore);
    }

    [Fact]
    public void The_hint_for_a_review_request_without_a_sequence_is_in_the_callers_terms()
    {
        var approvals = Recorder<IApprovalDefinitionRepository>.Create();
        var services = new ServiceCollection().AddSingleton(approvals.Proxy).BuildServiceProvider();
        approvals.Recorder.Answer = (method, _) => method.Name == nameof(IApprovalDefinitionRepository.ResolveAsync) ? Task.FromResult<ApprovalDefinitionResolveResult?>(null) : null;

        var developer = Assert.Throws<AgentException>(() => Approvals.Decide(new CmsCall(services, default, CmsCaller.Developer), Page, publish: false, requestApproval: true, "123"));
        var editor = Assert.Throws<AgentException>(() => Approvals.Decide(new CmsCall(services, default, CmsCaller.Editor), Page, publish: false, requestApproval: true, "123"));

        Assert.Equal((AgentErrorReasons.NoApprovalSequence, AgentErrorReasons.NoApprovalSequence), (developer.Reason, editor.Reason));
        Assert.Equal("Publish it instead (publish), or save it as a draft.", developer.Hint);
        Assert.Contains("publish_content without requestApproval", editor.Hint);
        Assert.Contains("draft", editor.Hint);
    }

    [Fact]
    public void The_developer_moves_unchecked_and_an_editor_needs_delete_on_the_content()
    {
        var destination = new ContentReference(5);

        Call(CmsCaller.Developer).Move(Page, destination);
        Call(CmsCaller.Editor).Move(Page, destination);

        var moves = _repository.Calls.Where(c => c.Method == "Move").Select(c => ((AccessLevel)c.Args[2]!, (AccessLevel)c.Args[3]!)).ToList();
        Assert.Equal((AccessLevel.NoAccess, AccessLevel.NoAccess), moves[0]);
        Assert.Equal(AccessLevel.Read | AccessLevel.Delete, moves[1].Item1);
        Assert.True(moves[1].Item2.HasFlag(AccessLevel.Create));
    }

    [Fact]
    public void A_language_branch_is_removed_unchecked_for_the_developer_and_with_delete_for_an_editor()
    {
        Call(CmsCaller.Developer).DeleteLanguageBranch(Page, "sv");
        Call(CmsCaller.Editor).DeleteLanguageBranch(Page, "sv");

        Assert.Equal([AccessLevel.NoAccess, AccessLevel.Delete], _repository.Calls.Select(c => (AccessLevel)c.Args[2]!));
    }

    [Fact]
    public void A_delete_goes_to_the_recycle_bin_in_the_callers_name()
    {
        Call(CmsCaller.Developer).Delete(Page);
        Call(CmsCaller.Editor).Delete(Page);

        Assert.Equal(
            [("MoveToWastebasket", AgentProtocol.PrincipalName), ("MoveToWastebasket", EditorName)],
            _repository.Calls.Select(c => (c.Method, (string)c.Args[1]!)));
    }

    [Fact]
    public void The_developer_reads_everything()
    {
        var hidden = new Secured(Page, AccessLevel.NoAccess);

        Assert.True(Call(CmsCaller.Developer).CanRead(hidden));
        Assert.Same(hidden, Call(CmsCaller.Developer).RequireRead(hidden));
    }

    [Fact]
    public void An_editor_reads_what_they_have_read_access_to_and_content_without_access_rights()
    {
        var readable = new Secured(Page, AccessLevel.Read);
        var unsecured = new Unsecured(Page);

        Assert.Same(readable, Call(CmsCaller.Editor).RequireRead(readable));
        Assert.Same(unsecured, Call(CmsCaller.Editor).RequireRead(unsecured));
    }

    [Fact]
    public void Content_an_editor_cant_read_is_not_found()
    {
        var hidden = Assert.Throws<AgentException>(() => Call(CmsCaller.Editor).RequireRead(new Secured(Page, AccessLevel.Edit)));

        Assert.Equal((AgentErrorCodes.NotFound, "No content with id 123."), (hidden.Code, hidden.Message));
    }

    [Fact]
    public void A_ref_to_content_an_editor_cant_read_fails_exactly_as_one_to_content_that_doesnt_exist()
    {
        var missing = Assert.Throws<AgentException>(() => new ContentLocator(Call(CmsCaller.Editor)).Resolve("123"));
        _repository.Answer = (method, args) => Found(method, args, new Secured(Page, AccessLevel.NoAccess));
        var hidden = Assert.Throws<AgentException>(() => new ContentLocator(Call(CmsCaller.Editor)).Resolve("123"));
        var developer = new ContentLocator(Call(CmsCaller.Developer)).Resolve("123");

        Assert.Equal((missing.Code, missing.Message, missing.Hint), (hidden.Code, hidden.Message, hidden.Hint));
        Assert.Equal(AgentErrorCodes.NotFound, hidden.Code);
        Assert.Equal(Page, developer);
    }

    [Fact]
    public void A_change_the_CMS_doesnt_check_needs_the_level_for_an_editor_only()
    {
        var editable = new Secured(Page, AccessLevel.Read | AccessLevel.Edit);

        Call(CmsCaller.Developer).RequireAccess(editable, AccessLevel.Administer);
        Call(CmsCaller.Editor).RequireAccess(editable, AccessLevel.Edit);
        var refused = Assert.Throws<AgentException>(() => Call(CmsCaller.Editor).RequireAccess(editable, AccessLevel.Administer));

        Assert.Equal(AgentErrorCodes.Refused, refused.Code);
        Assert.Contains("Administer", refused.Message);
    }

    [Fact]
    public void A_refusal_never_names_content_the_editor_cant_read()
    {
        var hidden = Assert.Throws<AgentException>(() => Call(CmsCaller.Editor).RequireAccess(new Secured(Page, AccessLevel.Edit), AccessLevel.Administer));

        Assert.Equal(AgentErrorCodes.NotFound, hidden.Code);
        Assert.DoesNotContain("News", hidden.Message);
    }

    [Fact]
    public void Changes_are_recorded_under_the_callers_name()
    {
        Assert.Equal(AgentProtocol.PrincipalName, Call(CmsCaller.Developer).UserName);
        Assert.Equal(EditorName, Call(CmsCaller.Editor).UserName);
    }

    /// <summary>The repository's <c>TryGet</c> finding <paramref name="content"/>; anything else as unanswered.</summary>
    private static object? Found(MethodInfo method, object?[] args, IContent content)
    {
        if (method.Name != nameof(IContentLoader.TryGet))
        {
            return null;
        }
        args[^1] = content;
        return true;
    }

    internal sealed class PrincipalAccessor(IPrincipal principal) : IPrincipalAccessor
    {
        public IPrincipal Principal { get; set; } = principal;
    }

    /// <summary>A language whose access rights give exactly edit access, or none.</summary>
    internal sealed class Branch(CultureInfo culture, bool edit) : LanguageBranch(culture)
    {
        public override bool QueryEditAccessRights(IPrincipal user) => edit;
    }

    /// <summary>The CMS's lists of types to create: by the parent's type name (the type's own access rights), or for the parent itself.</summary>
    private sealed class Availability(IList<ContentType> byName, IList<ContentType> byContent) : ContentTypeAvailabilityService
    {
        public string? Asked { get; private set; }

        public override AvailableSetting GetSetting(string contentTypeName) => new();

        public override bool IsAllowed(string parentContentTypeName, string childContentTypeName) => true;

        public override IList<ContentType> ListAvailable(string contentTypeName, IPrincipal user)
        {
            Asked = "name";
            return byName;
        }

        public override IList<ContentType> ListAvailable(IContent content, bool contentFolder, IPrincipal user)
        {
            Asked = "content";
            return byContent;
        }
    }

    internal class Unsecured(ContentReference link) : IContent
    {
        public PropertyDataCollection Property { get; } = new();

        public string Name { get; set; } = "News";

        public ContentReference ContentLink { get; set; } = link;

        public ContentReference ParentLink { get; set; } = ContentReference.EmptyReference;

        public Guid ContentGuid { get; set; } = Guid.NewGuid();

        public int ContentTypeID { get; set; } = 1;

        public bool IsDeleted { get; set; }
    }

    /// <summary>Content whose access rights give everyone exactly <paramref name="granted"/>.</summary>
    internal class Secured(ContentReference link, AccessLevel granted) : Unsecured(link), ISecurable, ISecurityDescriptor
    {
        public ISecurityDescriptor GetSecurityDescriptor() => this;

        public bool HasAccess(IPrincipal principal, AccessLevel access) => (granted & access) == access;

        public AccessLevel GetAccessLevel(IPrincipal principal) => granted;
    }

    /// <summary>Content with versions, which a save only puts live when it publishes.</summary>
    internal sealed class Versioned(ContentReference link, AccessLevel granted) : Secured(link, granted), IVersionable
    {
        public VersionStatus Status { get; set; } = VersionStatus.CheckedOut;

        public bool IsPendingPublish { get; set; }

        public DateTime? StartPublish { get; set; }

        public DateTime? StopPublish { get; set; }
    }

    /// <summary>A language branch of content with versions, master language English.</summary>
    internal sealed class Localized(ContentReference link, CultureInfo language) : Secured(link, AccessLevel.FullAccess), ILocalizable, IVersionable
    {
        public CultureInfo Language { get; set; } = language;

        public IEnumerable<CultureInfo> ExistingLanguages { get; set; } = [CultureInfo.GetCultureInfo("en"), language];

        public CultureInfo MasterLanguage { get; set; } = CultureInfo.GetCultureInfo("en");

        public VersionStatus Status { get; set; } = VersionStatus.CheckedOut;

        public bool IsPendingPublish { get; set; }

        public DateTime? StartPublish { get; set; }

        public DateTime? StopPublish { get; set; }
    }
}

/// <summary>
/// A stand-in for <typeparamref name="T"/> that records every call, and answers from <see cref="Answer"/> or with the
/// return type's default.
/// </summary>
public class Recorder<T> : DispatchProxy where T : class
{
    public List<(string Method, object?[] Args)> Calls { get; } = [];

    /// <summary>Null from it means unanswered: the default is returned.</summary>
    public Func<MethodInfo, object?[], object?>? Answer { get; set; }

    public static (T Proxy, Recorder<T> Recorder) Create()
    {
        var proxy = Create<T, Recorder<T>>();
        return (proxy, (Recorder<T>)(object)proxy);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        var arguments = args ?? [];
        Calls.Add((method.Name, arguments));
        return Answer?.Invoke(method, arguments)
            ?? (method.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null);
    }
}
