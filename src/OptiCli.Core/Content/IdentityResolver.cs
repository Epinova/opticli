using OptiCli.Core.Cms;
using OptiCli.Core.Data;
using OptiCli.Core.Urls;

namespace OptiCli.Core.Content;

/// <summary>
/// Turns ids and GUIDs into <see cref="ContentIdentity"/> in batches: callers register everything they
/// will need with <see cref="LoadAsync"/>, then look identities up without further queries.
/// </summary>
public sealed class IdentityResolver(CmsDatabase db, CmsModel model)
{
    private readonly Dictionary<int, ContentHeader> _headers = [];
    private readonly Dictionary<Guid, int> _ids = [];
    private readonly HashSet<Guid> _unknownGuids = [];
    private readonly Dictionary<Guid, (int Id, string Provider)> _providerContent = [];

    public UrlBuilder Urls { get; } = new(db, model);

    public CmsModel Model { get; } = model;

    public async Task LoadAsync(IEnumerable<int> ids, IEnumerable<Guid> guids, CancellationToken cancellationToken)
    {
        var newGuids = guids.Where(g => g != Guid.Empty && !_ids.ContainsKey(g) && !_unknownGuids.Contains(g)).Distinct().ToList();
        if (newGuids.Count > 0)
        {
            var found = await ContentHeaderReader.IdsByGuidsAsync(db, newGuids, cancellationToken);
            foreach (var guid in newGuids)
            {
                if (found.TryGetValue(guid, out var id))
                {
                    _ids[guid] = id;
                }
                else
                {
                    _unknownGuids.Add(guid);
                }
            }
            var unknown = newGuids.Where(_unknownGuids.Contains).ToList();
            if (unknown.Count > 0)
            {
                foreach (var (guid, mapped) in await ContentHeaderReader.ProviderContentAsync(db, unknown, cancellationToken))
                {
                    _providerContent[guid] = mapped;
                }
            }
        }

        var wanted = ids.Concat(newGuids.Where(_ids.ContainsKey).Select(g => _ids[g])).Where(id => !_headers.ContainsKey(id)).Distinct().ToList();
        if (wanted.Count > 0)
        {
            var headers = await ContentHeaderReader.ByIdsAsync(db, wanted, cancellationToken);
            foreach (var header in headers.Values)
            {
                _headers[header.Id] = header;
                _ids[header.Guid] = header.Id;
            }
            await Urls.PrepareAsync(headers.Values, cancellationToken);
        }
    }

    public ContentHeader? Header(int id) => _headers.GetValueOrDefault(id);

    public ContentHeader? Header(Guid guid) => _ids.TryGetValue(guid, out var id) ? Header(id) : null;

    public ContentIdentity ById(int id, LanguageBranch? language) =>
        Header(id) is { } header ? Describe(header, language) : ContentIdentity.MissingId(id);

    public ContentIdentity ByGuid(Guid guid, LanguageBranch? language) =>
        Header(guid) is { } header ? Describe(header, language)
        : _providerContent.TryGetValue(guid, out var mapped) ? ContentIdentity.FromProvider(guid, mapped.Id, mapped.Provider)
        : ContentIdentity.MissingGuid(guid);

    /// <summary>
    /// The identity in <paramref name="language"/> when the item has that branch, otherwise in its
    /// master language (the <c>language</c> field says which).
    /// </summary>
    public ContentIdentity Describe(ContentHeader header, LanguageBranch? language, int? version = null, VersionStatus? status = null, string? name = null)
    {
        var row = header.LanguageRow(language?.Id);
        var shown = row is null ? null : Model.Language(row.LanguageId);
        return new ContentIdentity(
            ContentIdentity.RefFor(header.Id, version),
            header.Guid,
            Model.TypeName(header.TypeId),
            name ?? row?.Name,
            shown?.DisplayCode,
            VersionStatuses.Name(status ?? row?.Status ?? VersionStatus.NotCreated),
            Urls.UrlOf(header, shown is { IsInvariant: false } ? shown : null)?.Path)
        {
            Kind = Model.Kind(header.TypeId) is var kind && kind.IsComposition() ? kind.Name() : null,
            Deleted = header.Deleted ? true : null,
            Blueprint = header.Blueprint ? true : null,
        };
    }
}
