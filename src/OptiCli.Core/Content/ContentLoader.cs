using System.Text.Json.Nodes;
using OptiCli.Core.Data;
using OptiCli.Core.Errors;
using OptiCli.Core.Properties;
using OptiCli.Core.Text;

namespace OptiCli.Core.Content;

/// <summary>
/// Reads one content item with decoded properties: picks the branch and version, loads the rows,
/// nests them, and decodes in two passes so every reference is resolved with one batch of queries.
/// </summary>
public sealed class ContentLoader(CmsDatabase db, IdentityResolver identities)
{
    private CmsModel Model => identities.Model;

    /// <param name="language">Requested branch; null for the item's master language.</param>
    /// <exception cref="NotFoundException">The item or version does not exist.</exception>
    /// <exception cref="UsageException">Unknown <c>--fields</c> names.</exception>
    public async Task<ContentDocument> GetAsync(
        int contentId, VersionSelector version, LanguageBranch? language, DecodeOptions options, CancellationToken cancellationToken)
    {
        await identities.LoadAsync([contentId], [], cancellationToken);
        var header = identities.Header(contentId) ?? throw new NotFoundException($"No content with id {contentId}.");
        var notes = new List<string>();

        VersionInfo? shownVersion = null;
        int branch;
        if (version.Kind == VersionKind.Specific)
        {
            shownVersion = await VersionReader.ByIdAsync(db, Model, version.Id!.Value, cancellationToken);
            if (shownVersion is null || shownVersion.ContentId != contentId)
            {
                throw new NotFoundException(
                    $"Content {contentId} has no version {version.Id}.",
                    $"Run `opticli versions {contentId}` to list its versions.");
            }
            branch = shownVersion.LanguageId;
            if (language is not null && language.Id != branch)
            {
                notes.Add($"Version {version.Id} is in '{Model.Language(branch)?.Code}'; --lang {language.Code} was ignored.");
            }
        }
        else
        {
            branch = ChooseBranch(header, language, notes);
            if (version.Kind == VersionKind.Latest)
            {
                shownVersion = await VersionReader.LatestAsync(db, Model, contentId, branch, cancellationToken);
            }
        }

        var branchLanguage = Model.Language(branch);
        var row = header.Languages.GetValueOrDefault(branch);
        var rows = await RowsAsync(header, branch, shownVersion, cancellationToken);
        var fields = ValidateFields(header, options.Fields);
        var properties = await DecodeAsync(header, branch, rows, options with { Fields = fields }, branchLanguage, cancellationToken);

        if (shownVersion is null && row is not null && row.Status != VersionStatus.Published)
        {
            notes.Add($"Not published in '{branchLanguage?.Code}'; showing its primary draft (status {VersionStatuses.Name(row.Status)}).");
        }

        var latestDraft = shownVersion is null && row is not null
            ? await ContentHeaderReader.NewerDraftAsync(db, contentId, branch, row.VersionId, cancellationToken)
            : null;
        // Page settings come from the version shown, or the branch's primary version. Sorting isn't culture-specific:
        // a version of another branch may hold a stale copy.
        var kind = Model.Kind(header.TypeId);
        var isPage = kind == Cms.ContentKind.Page;
        var facts = shownVersion ?? (isPage && row?.VersionId is { } primary ? await VersionReader.ByIdAsync(db, Model, primary, cancellationToken) : null);
        var sortingVersion = facts?.LanguageId == header.MasterLanguageId ? facts : null;
        var shortcut = isPage ? await ShortcutAsync(facts, branchLanguage, cancellationToken) : null;
        var category = kind is Cms.ContentKind.Page or Cms.ContentKind.Block or Cms.ContentKind.Media
            ? await PropertyRowReader.BuiltInCategoriesAsync(db, contentId, branch, shownVersion?.Id, cancellationToken)
            : [];
        var identity = identities.Describe(
            header,
            branchLanguage,
            status: shownVersion?.StatusValue ?? row?.Status,
            name: shownVersion?.Name);

        return new ContentDocument(
            identity.Ref!,
            header.Guid,
            identity.Type!,
            identity.Name,
            identity.Language,
            identity.Status!,
            identity.Url,
            Model.Kind(header.TypeId).ToString().ToLowerInvariant(),
            ContentIdentity.RefFor(contentId, shownVersion?.Id ?? row?.VersionId),
            Model.Language(header.MasterLanguageId)?.DisplayCode,
            header.Languages.Keys.Select(id => Model.Language(id)?.DisplayCode).OfType<string>().Order().ToList(),
            header.ParentId is { } parent ? ContentIdentity.RefFor(parent) : null,
            shownVersion?.Saved ?? row?.Saved,
            shownVersion?.ChangedBy ?? row?.ChangedBy,
            shownVersion?.StartPublish ?? row?.StartPublish,
            // Not ?? as for StartPublish: a draft without a stop date isn't stopped by the published version's.
            shownVersion is not null ? shownVersion.StopPublish : row?.StopPublish,
            isPage ? Queries.ChildOrder.Name(sortingVersion?.ChildOrderRule ?? header.ChildOrderRule) : null,
            isPage ? sortingVersion?.PeerOrder ?? header.PeerOrder : null,
            isPage ? ShortcutInfo.SimpleAddressPath(facts?.ExternalUrl ?? row?.ExternalUrl) : null,
            shortcut,
            category.Count == 0 ? null : category.Select(Model.CategoryName).ToList(),
            latestDraft is { } draft ? ContentIdentity.RefFor(contentId, draft) : null,
            header.Deleted ? true : null,
            language is not null && language.Id != branch && version.Kind != VersionKind.Specific ? language.Code : null,
            notes.Count == 0 ? null : notes,
            properties);
    }

    /// <summary>Decoded primary properties of several items (for <c>--expand</c>), keyed by GUID; references stay identity-only.</summary>
    public async Task<IReadOnlyDictionary<Guid, JsonObject>> ExpandAsync(IReadOnlyCollection<int> contentIds, LanguageBranch? language, CancellationToken cancellationToken)
    {
        await identities.LoadAsync(contentIds, [], cancellationToken);
        var headers = contentIds.Select(identities.Header).OfType<ContentHeader>().ToList();
        if (headers.Count == 0)
        {
            return new Dictionary<Guid, JsonObject>();
        }

        var languageIds = headers.Select(h => h.MasterLanguageId).Append(language?.Id ?? 0).Where(id => id > 0);
        var allRows = (await PropertyRowReader.PrimaryAsync(db, headers.Select(h => h.Id), languageIds, cancellationToken)).ToLookup(r => r.ContentId);

        var trees = headers.Select(h =>
        {
            var branch = language is not null && h.Languages.ContainsKey(language.Id) ? language.Id : h.MasterLanguageId;
            return (Header: h, Branch: branch, Tree: PropertyTree.Build(Effective(allRows[h.Id], branch, h.MasterLanguageId)));
        }).ToList();

        var plain = new DecodeOptions();
        var collector = new ReferenceCollector();
        foreach (var (header, _, tree) in trees)
        {
            Decoder(collector, plain, header).Decode(header.TypeId, tree);
        }
        await identities.LoadAsync(collector.Ids, collector.Guids, cancellationToken);

        var lookup = new ResolvedReferences(identities, language);
        return trees.ToDictionary(t => t.Header.Guid, t => Decoder(lookup, plain, t.Header).Decode(t.Header.TypeId, t.Tree));
    }

    /// <summary>The page's shortcut, with the page it points at described; null for a normal page.</summary>
    private async Task<ShortcutInfo?> ShortcutAsync(VersionInfo? version, LanguageBranch? language, CancellationToken cancellationToken)
    {
        if (ShortcutInfo.TypeName(version?.LinkType) is not { } type)
        {
            return null;
        }
        var external = type == "external";
        var permanent = external ? LinkTarget.From(automaticLink: false, fetchData: false, contentLinkGuid: null, version!.LinkUrl) : null;
        var target = external ? permanent?.Guid : version!.ShortcutGuid is { } guid && guid != Guid.Empty ? guid : null;
        ContentIdentity? to = null;
        if (target is { } targetGuid)
        {
            await identities.LoadAsync([], [targetGuid], cancellationToken);
            to = identities.ByGuid(targetGuid, language);
        }
        return new ShortcutInfo(
            type,
            to,
            external ? version!.LinkUrl : null,
            permanent?.Anchor,
            ShortcutInfo.FrameTarget(version!.FrameName));
    }

    private int ChooseBranch(ContentHeader header, LanguageBranch? language, List<string> notes)
    {
        if (language is not null && header.Languages.ContainsKey(language.Id))
        {
            return language.Id;
        }
        var fallback = header.Languages.ContainsKey(header.MasterLanguageId)
            ? header.MasterLanguageId
            : header.Languages.Keys.DefaultIfEmpty(header.MasterLanguageId).Min();
        if (language is not null)
        {
            notes.Add($"No '{language.Code}' branch; showing '{Model.Language(fallback)?.Code ?? "invariant"}'.");
        }
        return fallback;
    }

    /// <summary>
    /// Primary values from <c>tblContentProperty</c>; for a specific version its own rows, plus shared
    /// values from the master branch when the version is in another language (the CMS stores them once).
    /// </summary>
    private async Task<IEnumerable<PropertyRow>> RowsAsync(ContentHeader header, int branch, VersionInfo? version, CancellationToken cancellationToken)
    {
        IEnumerable<PropertyRow> rows;
        if (version is null)
        {
            rows = await PropertyRowReader.PrimaryAsync(db, [header.Id], [branch, header.MasterLanguageId], cancellationToken);
        }
        else
        {
            rows = await PropertyRowReader.VersionAsync(db, header.Id, version.Id, branch, cancellationToken);
            if (branch != header.MasterLanguageId)
            {
                rows = rows.Concat(await PropertyRowReader.PrimaryAsync(db, [header.Id], [header.MasterLanguageId], cancellationToken));
            }
        }
        return Effective(rows, branch, header.MasterLanguageId);
    }

    private async Task<JsonObject> DecodeAsync(
        ContentHeader header, int branch, IEnumerable<PropertyRow> rows, DecodeOptions options, LanguageBranch? language, CancellationToken cancellationToken)
    {
        var tree = PropertyTree.Build(rows);

        var collector = new ReferenceCollector();
        Decoder(collector, options, header).Decode(header.TypeId, tree);
        await identities.LoadAsync(collector.Ids, collector.Guids, cancellationToken);

        IReadOnlyDictionary<Guid, JsonObject>? expanded = null;
        if (options.Expand)
        {
            var targets = collector.Ids
                .Concat(collector.Guids.Select(identities.Header).OfType<ContentHeader>().Select(h => h.Id))
                .Where(id => id != header.Id && identities.Header(id) is not null)
                .Distinct()
                .ToList();
            expanded = await ExpandAsync(targets, language, cancellationToken);
        }

        return Decoder(new ResolvedReferences(identities, language, expanded), options, header).Decode(header.TypeId, tree);
    }

    private PropertyDecoder Decoder(IReferenceLookup lookup, DecodeOptions options, ContentHeader header) =>
        new(Model, lookup, options, header.MasterLanguageId, id => Model.Language(id)?.DisplayCode);

    private IEnumerable<PropertyRow> Effective(IEnumerable<PropertyRow> rows, int branch, int master) =>
        PropertyRows.Effective(rows, branch, master, id => Model.Properties.GetValueOrDefault(id)?.CultureSpecific ?? false);

    private IReadOnlySet<string>? ValidateFields(ContentHeader header, IReadOnlySet<string>? fields)
    {
        if (fields is null)
        {
            return null;
        }
        var names = Model.PropertiesOf(header.TypeId).Select(p => p.Name).ToList();
        foreach (var field in fields)
        {
            if (!names.Contains(field, StringComparer.OrdinalIgnoreCase) && !IdentityFields.Contains(field))
            {
                throw new UsageException(
                    $"{Model.TypeName(header.TypeId)} has no property '{field}'.",
                    Suggestions.DidYouMean(field, names) ?? $"Run `opticli type {Model.TypeName(header.TypeId)}` to list its properties.");
            }
        }
        // Identity fields are always in the output, so naming them only asks for no other properties.
        return new HashSet<string>(fields.Where(f => names.Contains(f, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Top-level fields of every <c>get</c> result; <c>--fields</c> accepts them without selecting a property.</summary>
    private static readonly HashSet<string> IdentityFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "ref", "guid", "type", "name", "language", "status", "url", "kind", "version", "masterLanguage", "languages",
        "parent", "saved", "changedBy", "startPublish", "stopPublish", "deleted",
    };
}
