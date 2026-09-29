using OptiCli.Core.Cms;
using OptiCli.Core.Data;
using OptiCli.Core.Refs;

namespace OptiCli.Core.Content;

/// <summary>Everything a content command needs for one invocation: the database, the model and shared caches.</summary>
public sealed class ContentSession : IAsyncDisposable
{
    private ContentSession(CmsDatabase db, CmsModel model)
    {
        Db = db;
        Model = model;
        Identities = new IdentityResolver(db, model);
    }

    public CmsDatabase Db { get; }

    public CmsModel Model { get; }

    public IdentityResolver Identities { get; }

    public static async Task<ContentSession> OpenAsync(CmsDatabase db, CancellationToken cancellationToken) =>
        new(db, await CmsModel.LoadAsync(db, cancellationToken));

    /// <summary><c>--lang</c>; null when not given.</summary>
    public LanguageBranch? Language(string? code) => string.IsNullOrWhiteSpace(code) ? null : Model.RequireLanguage(code);

    /// <summary><c>--site</c>; null when not given.</summary>
    public SiteInfo? Site(string? nameOrHost) => string.IsNullOrWhiteSpace(nameOrHost) ? null : Model.Sites.RequireSite(nameOrHost);

    public Task<LocatedContent> LocateAsync(string reference, string? site, CancellationToken cancellationToken) =>
        new ContentLocator(Db, Model).LocateAsync(ContentRefParser.Parse(reference), Site(site), cancellationToken);

    /// <summary>Closes the database connection.</summary>
    public ValueTask DisposeAsync() => Db.DisposeAsync();

    /// <summary>The header of an item that is known to exist (from <see cref="LocateAsync"/>).</summary>
    public async Task<ContentHeader> HeaderAsync(int id, CancellationToken cancellationToken)
    {
        await Identities.LoadAsync([id], [], cancellationToken);
        return Identities.Header(id) ?? throw new Errors.NotFoundException($"No content with id {id}.");
    }
}
