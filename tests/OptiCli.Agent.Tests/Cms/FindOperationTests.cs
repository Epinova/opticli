using System.Security.Principal;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Cms;
using OptiCli.Cms.Operations;

namespace OptiCli.Agent.Tests.Cms;

/// <summary>
/// <c>find_content</c>'s walk as an editor sees the tree: what they can't read is neither listed nor counted, so whether
/// the search was cut short tells nothing about it.
/// </summary>
public class FindOperationTests
{
    private static readonly ContentReference Folder = new(5);

    [Fact]
    public void Content_the_editor_cant_read_doesnt_make_a_search_look_cut_short()
    {
        var found = Find(CmsCaller.Editor, Child(10, "Alloy Plan", AccessLevel.Read), Child(11, "Alloy Meet", AccessLevel.NoAccess));

        Assert.Equal("Alloy Plan", Assert.Single(found.Items).Name);
        Assert.False(found.Truncated);
    }

    [Fact]
    public void A_search_is_cut_short_when_something_the_caller_can_read_is_left()
    {
        var editor = Find(CmsCaller.Editor, Child(10, "Alloy Plan", AccessLevel.Read), Child(12, "Alloy Track", AccessLevel.Read));
        var developer = Find(CmsCaller.Developer, Child(10, "Alloy Plan", AccessLevel.Read), Child(11, "Alloy Meet", AccessLevel.NoAccess));

        Assert.True(editor.Truncated);
        Assert.True(developer.Truncated);
    }

    private static CmsCallTests.Secured Child(int id, string name, AccessLevel access) =>
        new(new ContentReference(id), access) { Name = name, ParentLink = Folder };

    /// <summary>Searches the folder for "Alloy", one match at most, with the folder's children as given and nothing below them.</summary>
    private static ContentList Find(CmsCaller caller, params IContent[] children)
    {
        var folder = new CmsCallTests.Secured(Folder, AccessLevel.Read) { Name = "Products" };
        var (repository, repositoryRecorder) = Recorder<IContentRepository>.Create();
        repositoryRecorder.Answer = (method, args) =>
        {
            switch (method.Name)
            {
                case nameof(IContentLoader.TryGet):
                    args[^1] = folder;
                    return true;
                case nameof(IContentLoader.Get):
                    return folder;
                default:
                    return null;
            }
        };
        var (loader, loaderRecorder) = Recorder<IContentLoader>.Create();
        loaderRecorder.Answer = (method, args) => method.Name == nameof(IContentLoader.GetChildren)
            ? (args[0] is ContentReference parent && parent.CompareToIgnoreWorkID(Folder) && args[2] is 0 ? children.ToList() : new List<IContent>())
            : null;
        var services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(loader)
            .AddSingleton(Recorder<IContentTypeRepository>.Create().Proxy)
            .AddSingleton(Recorder<IContentVersionRepository>.Create().Proxy)
            .AddSingleton(Recorder<ILanguageBranchRepository>.Create().Proxy)
            .AddSingleton<IPrincipalAccessor>(new CmsCallTests.PrincipalAccessor(new GenericPrincipal(new GenericIdentity("editor@example.com"), [])))
            .BuildServiceProvider();
        return FindOperation.Run(new CmsCall(services, CancellationToken.None, caller), new FindRequest("Alloy", Root: "5", Limit: 1));
    }
}
