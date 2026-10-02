using System.Reflection;
using System.Security.Principal;
using EPiServer;
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
            .AddSingleton(Recorder<ILanguageBranchRepository>.Create().Proxy)
            .AddSingleton<IPrincipalAccessor>(new PrincipalAccessor(new GenericPrincipal(new GenericIdentity(EditorName), [])))
            .BuildServiceProvider();
    }

    private CmsCall Call(CmsCaller caller) => new(_services, CancellationToken.None, caller);

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

    private sealed class PrincipalAccessor(IPrincipal principal) : IPrincipalAccessor
    {
        public IPrincipal Principal { get; set; } = principal;
    }

    private class Unsecured(ContentReference link) : IContent
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
    private sealed class Secured(ContentReference link, AccessLevel granted) : Unsecured(link), ISecurable, ISecurityDescriptor
    {
        public ISecurityDescriptor GetSecurityDescriptor() => this;

        public bool HasAccess(IPrincipal principal, AccessLevel access) => (granted & access) == access;

        public AccessLevel GetAccessLevel(IPrincipal principal) => granted;
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
