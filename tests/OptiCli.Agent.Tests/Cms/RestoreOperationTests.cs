using System.Security.Principal;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Cms;
using OptiCli.Cms.Operations;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Cms;

/// <summary>
/// Restoring from the recycle bin through stand-ins for the CMS's repository and its store of previous parents: where
/// the content goes, and what stops it before anything moves.
/// </summary>
public class RestoreOperationTests
{
    private const int Bin = 2;

    static RestoreOperationTests()
    {
        // Set by the CMS when it starts; without a site the getter would ask its system definition.
        // A PageReference on CMS 12, where the property has that type; CMS 13 made it a ContentReference.
#if CMS13
        ContentReference.WasteBasket = new ContentReference(Bin);
#else
        ContentReference.WasteBasket = new PageReference(Bin);
#endif
    }

    private readonly Dictionary<int, CmsCallTests.Unsecured> _content = [];

    private readonly Dictionary<int, int> _storedParents = [];

    private readonly Recorder<IContentRepository> _repository;

    private readonly IServiceProvider _services;

    public RestoreOperationTests()
    {
        Add(1, "Root", parent: 0);
        Add(Bin, "Recycle bin", parent: 1);
        Add(10, "About us", parent: 1);
        Add(11, "Careers", parent: 1);
        var (repository, recorder) = Recorder<IContentRepository>.Create();
        _repository = recorder;
        recorder.Answer = (method, args) =>
        {
            var id = args.Length > 0 && args[0] is ContentReference link ? link.ID : 0;
            switch (method.Name)
            {
                case nameof(IContentLoader.TryGet):
                    var found = _content.GetValueOrDefault(id);
                    args[^1] = found;
                    return found is not null;
                case nameof(IContentLoader.Get):
                    return _content.TryGetValue(id, out var content) ? content : throw new ContentNotFoundException(new ContentReference(id));
                case nameof(IContentRepository.GetAncestors):
                    return Ancestors(id).ToList();
                default:
                    return null;
            }
        };
        var (parents, parentRecorder) = Recorder<IParentRestoreRepository>.Create();
        parentRecorder.Answer = (method, args) => method.Name == nameof(IParentRestoreRepository.GetParentLink) && args[0] is ContentReference item
            ? _storedParents.TryGetValue(item.ID, out var parent) ? new ContentReference(parent) : ContentReference.EmptyReference
            : null;
        _services = new ServiceCollection()
            .AddSingleton(repository)
            .AddSingleton(parents)
            .AddSingleton(Recorder<IContentTypeRepository>.Create().Proxy)
            .AddSingleton(Recorder<IContentVersionRepository>.Create().Proxy)
            .AddSingleton(Recorder<ILanguageBranchRepository>.Create().Proxy)
            .AddSingleton<IPrincipalAccessor>(new CmsCallTests.PrincipalAccessor(new GenericPrincipal(new GenericIdentity("editor@example.com"), [])))
            .BuildServiceProvider();
    }

    private void Add(int id, string name, int parent, bool deleted = false) =>
        _content[id] = new CmsCallTests.Unsecured(new ContentReference(id))
        {
            Name = name,
            ParentLink = parent == 0 ? ContentReference.EmptyReference : new ContentReference(parent),
            IsDeleted = deleted,
        };

    private IEnumerable<IContent> Ancestors(int id)
    {
        for (var parent = _content[id].ParentLink.ID; parent != 0; parent = _content[parent].ParentLink.ID)
        {
            yield return _content[parent];
        }
    }

    private RestoreResult Restore(string reference, string? parent = null, bool dryRun = false, CmsCaller caller = CmsCaller.Developer) =>
        RestoreOperation.Run(new CmsCall(_services, CancellationToken.None, caller), reference, new RestoreRequest { Parent = parent, DryRun = dryRun });

    private IEnumerable<(string Method, object?[] Args)> Moves => _repository.Calls.Where(c => c.Method == nameof(IContentRepository.Move));

    [Fact]
    public void Content_goes_back_below_the_parent_the_CMS_stored_as_the_edit_UI_restores_it()
    {
        Add(20, "Team", parent: Bin, deleted: true);
        _storedParents[20] = 10;

        var result = Restore("20");

        var move = Assert.Single(Moves);
        Assert.Equal((20, 10, AccessLevel.NoAccess, AccessLevel.NoAccess), (((ContentReference)move.Args[0]!).ID, ((ContentReference)move.Args[1]!).ID, (AccessLevel)move.Args[2]!, (AccessLevel)move.Args[3]!));
        Assert.Equal(("10", "2", "10", true, false), (result.Parent, result.PreviousParent, result.StoredParent, result.Restored, result.DryRun));
    }

    [Fact]
    public void A_dry_run_checks_everything_and_moves_nothing()
    {
        Add(20, "Team", parent: Bin, deleted: true);
        _storedParents[20] = 10;

        var result = Restore("20", dryRun: true);

        Assert.Empty(Moves);
        Assert.Equal(("10", false, true), (result.Parent, result.Restored, result.DryRun));
    }

    [Fact]
    public void A_given_parent_wins_over_the_stored_one_which_is_still_reported()
    {
        Add(20, "Team", parent: Bin, deleted: true);
        _storedParents[20] = 10;

        var result = Restore("20", parent: "11");

        Assert.Equal(11, ((ContentReference)Assert.Single(Moves).Args[1]!).ID);
        Assert.Equal(("11", "10"), (result.Parent, result.StoredParent));
    }

    [Fact]
    public void Without_a_stored_parent_the_parent_must_be_given()
    {
        Add(20, "Team", parent: Bin, deleted: true);

        var ex = Assert.Throws<AgentException>(() => Restore("20"));

        Assert.Equal(AgentErrorCodes.Usage, ex.Code);
        Assert.Contains("--to", ex.Hint);
        Assert.Equal("11", Restore("20", parent: "11").Parent);
    }

    [Fact]
    public void Content_that_isnt_in_the_recycle_bin_is_a_conflict()
    {
        var ex = Assert.Throws<AgentException>(() => Restore("10"));

        Assert.Equal(AgentErrorCodes.Conflict, ex.Code);
        Assert.Contains("not in the recycle bin", ex.Message);
    }

    [Fact]
    public void Content_below_deleted_content_names_what_to_restore_instead()
    {
        Add(20, "Team", parent: Bin, deleted: true);
        Add(21, "Jobs", parent: 20, deleted: true);
        _storedParents[21] = 11;

        var ex = Assert.Throws<AgentException>(() => Restore("21"));

        Assert.Equal(AgentErrorCodes.Usage, ex.Code);
        Assert.Contains("because 20 ('Team') above it was deleted", ex.Message);
        Assert.Contains("opticli restore 20", ex.Hint);
        Assert.Empty(Moves);
    }

    [Fact]
    public void A_parent_in_the_recycle_bin_too_is_a_conflict_naming_what_to_restore_first()
    {
        Add(20, "Team", parent: Bin, deleted: true);
        Add(21, "Jobs", parent: 20, deleted: true);
        Add(30, "Openings", parent: Bin, deleted: true);
        _storedParents[30] = 21;

        var ex = Assert.Throws<AgentException>(() => Restore("30"));

        Assert.Equal(AgentErrorCodes.Conflict, ex.Code);
        Assert.Contains("21 ('Jobs'), is in the recycle bin too (with 20, 'Team', above it)", ex.Message);
        Assert.Contains("opticli restore 20", ex.Hint);
        Assert.Equal(AgentErrorCodes.Usage, Assert.Throws<AgentException>(() => Restore("30", parent: "2")).Code);
        Assert.Empty(Moves);
    }

    [Fact]
    public void A_stored_parent_that_was_deleted_for_good_is_a_conflict()
    {
        Add(20, "Team", parent: Bin, deleted: true);
        _storedParents[20] = 99;

        var ex = Assert.Throws<AgentException>(() => Restore("20"));

        Assert.Equal(AgentErrorCodes.Conflict, ex.Code);
        Assert.Contains("no longer exists", ex.Message);
    }

    [Fact]
    public void An_editor_restores_in_the_edit_UI()
    {
        Add(20, "Team", parent: Bin, deleted: true);
        _storedParents[20] = 10;

        var ex = Assert.Throws<AgentException>(() => Restore("20", caller: CmsCaller.Editor));

        Assert.Equal(AgentErrorCodes.Refused, ex.Code);
        Assert.Empty(Moves);
    }
}
