using System.Globalization;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Refs;
using OptiCli.Core.Serve;
using OptiCli.Core.Urls;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// Runs one <see cref="WriteOperation"/>: resolves refs and checks names against the database, reads
/// the base version for concurrency, calls the agent, and reshapes its answer.
/// </summary>
/// <remarks>
/// The agent only accepts ids, versions and GUIDs, so URLs are resolved here; GUIDs of content from a
/// content provider (not in this database) are passed through for the site to resolve.
/// Publish, move and delete have no dry run in the agent; for those the checks run here, against the database.
/// </remarks>
public sealed class WriteExecutor(ContentSession session, Func<CancellationToken, Task<AgentClient>> connect, string? site = null)
{
    public const string AgentSource = "agent";
    public const string DbSource = "db";

    /// <summary>Content type of the recycle bin (<c>ContentReference.WasteBasket</c>).</summary>
    private const string RecycleBinType = "SysRecycleBin";

    private AgentClient? _agent;

    /// <exception cref="ContentValidationException">A dry run found the change would fail validation (details: the dry-run result).</exception>
    public async Task<WriteOutcome> RunAsync(WriteOperation operation, bool dryRun, CancellationToken cancellationToken)
    {
        var outcome = operation switch
        {
            SetOperation set => await SetAsync(set, dryRun, cancellationToken),
            AreaEdit area => await AreaAsync(area, dryRun, cancellationToken),
            CreateOperation create => await CreateAsync(create, dryRun, cancellationToken),
            BlockCreateOperation block => await BlockAsync(block, dryRun, cancellationToken),
            TranslateOperation translate => await TranslateAsync(translate, dryRun, cancellationToken),
            PublishOperation publish => await PublishAsync(publish, dryRun, cancellationToken),
            MoveOperation move => await MoveAsync(move, dryRun, cancellationToken),
            DeleteOperation delete => await DeleteAsync(delete, dryRun, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown operation {operation.GetType().Name}."),
        };

        if (outcome.Output is WriteOutput { DryRun: true, Valid: false } invalid)
        {
            var errors = invalid.Validation?.Where(v => v.Severity == "error").Select(v => v.Property is null ? v.Message : $"{v.Property}: {v.Message}") ?? [];
            throw new ContentValidationException(
                $"Dry run: the change would fail validation ({string.Join("; ", errors)}). Nothing was saved.",
                "details has the dry-run result; fix the listed properties and retry.")
            { Details = invalid };
        }
        return outcome;
    }

    private async Task<WriteOutcome> SetAsync(SetOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await EditableAsync(op.Ref, cancellationToken);
        PropertyNameCheck.Check(session.Model, target.Header.TypeId, op.Properties);
        var language = LanguageFor(op.Lang, target);
        var request = new DraftRequest
        {
            Lang = language?.Code,
            Name = op.Name,
            Properties = PropertyArguments.ToRequest(op.Properties),
            Publish = op.Publish,
            DryRun = dryRun,
            BaseVersion = await BaseVersionAsync(target, language, op.BaseVersion, op.Force, cancellationToken),
        };
        if (request.Properties is null && request.Name is null)
        {
            throw new UsageException("Nothing to set.", $"Give properties ({PropertyArguments.Syntax}), --values or --name.");
        }
        return await DraftAsync(target, request, cancellationToken);
    }

    private async Task<WriteOutcome> AreaAsync(AreaEdit op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await EditableAsync(op.Ref, cancellationToken);
        var property = PropertyNameCheck.RequireContentArea(session.Model, target.Header.TypeId, op.Property);
        var language = LanguageFor(op.Lang, target);

        var edit = op.Action switch
        {
            AreaOps.Add => new AreaOperation
            {
                Op = AreaOps.Add,
                Property = property.Name,
                Ref = (await ResolveAsync(op.Item ?? throw new UsageException("area add needs the block to add."), "block", cancellationToken)).ContentRef,
                At = op.At,
                DisplayOption = op.Display,
            },
            AreaOps.Remove or AreaOps.Move => new AreaOperation
            {
                Op = op.Action,
                Property = property.Name,
                Index = op.Index,
                Ref = op.Item is null ? null : (await ResolveAsync(op.Item, "item", cancellationToken)).ContentRef,
                At = op.Action == AreaOps.Move ? op.To ?? throw new UsageException("area move needs the target position.") : null,
            },
            _ => throw new UsageException($"Unknown area action '{op.Action}'.", "Use add, remove or move."),
        };
        if (edit.Op != AreaOps.Add && (edit.Index is null) == (edit.Ref is null))
        {
            throw new UsageException($"area {op.Action} needs the item: its index or the content it references.");
        }

        var request = new DraftRequest
        {
            Lang = language?.Code,
            AreaOps = [edit],
            Publish = op.Publish,
            DryRun = dryRun,
            BaseVersion = await BaseVersionAsync(target, language, op.BaseVersion, op.Force, cancellationToken),
        };
        return await DraftAsync(target, request, cancellationToken);
    }

    private async Task<WriteOutcome> DraftAsync(Target target, DraftRequest request, CancellationToken cancellationToken)
    {
        WriteResult result;
        try
        {
            result = await PostAsync<WriteResult>(AgentRoutes.Draft(target.AgentRef), request, cancellationToken);
        }
        catch (ConflictException ex) when (ex.Details is AgentErrorDetails { CurrentVersion: { } current })
        {
            // The agent's hint speaks protocol ("baseVersion"); say it in command-line terms.
            var latest = WriteOutput.VersionRef(target.Id, current);
            throw new ConflictException(ex.Message,
                $"Someone saved {latest} after the version this change was based on. Look at it (opticli get {latest}), then run the command again: without --base-version it is based on the latest version; --force skips the check.")
            { Details = ex.Details };
        }
        return Outcome(WriteOutput.From(result), null);
    }

    private async Task<WriteOutcome> CreateAsync(CreateOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var parent = await ResolveAsync(op.Parent, "parent", cancellationToken);
        var type = session.Model.RequireType(op.Type);
        PropertyNameCheck.Check(session.Model, type.Id, op.Properties);
        var request = new CreateRequest
        {
            Parent = parent.ContentRef,
            Type = type.Name,
            Name = op.Name,
            Lang = session.Language(op.Lang)?.Code,
            Properties = PropertyArguments.ToRequest(op.Properties),
            Publish = op.Publish,
            DryRun = dryRun,
        };
        return await CreatedAsync(request, type.Name, cancellationToken);
    }

    private async Task<WriteOutcome> BlockAsync(BlockCreateOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        if ((op.For is null) == (op.Parent is null))
        {
            throw new UsageException("Give exactly one of --for <ref> (the content's \"For this page\" folder) or --parent <folder>.");
        }
        var type = session.Model.RequireType(op.Type);
        if (type.Kind != ContentKind.Block)
        {
            throw new UsageException($"{type.Name} is a {type.Kind.ToString().ToLowerInvariant()} type, not a block type.", "Use `opticli create` for pages and folders.");
        }
        PropertyNameCheck.Check(session.Model, type.Id, op.Properties);
        var request = new CreateRequest
        {
            Parent = op.Parent is null ? null : (await ResolveAsync(op.Parent, "parent", cancellationToken)).ContentRef,
            ForContent = op.For is null ? null : (await ResolveAsync(op.For, "--for", cancellationToken)).ContentRef,
            Type = type.Name,
            Name = op.Name,
            Lang = session.Language(op.Lang)?.Code,
            Properties = PropertyArguments.ToRequest(op.Properties),
            Publish = op.Publish,
            DryRun = dryRun,
        };
        return await CreatedAsync(request, type.Name, cancellationToken);
    }

    private async Task<WriteOutcome> CreatedAsync(CreateRequest request, string typeName, CancellationToken cancellationToken)
    {
        var result = await PostAsync<WriteResult>(AgentRoutes.Create, request, cancellationToken);
        var output = WriteOutput.From(result, typeName, request.Name, request.Parent);
        return Outcome(output, result.Saved ? result.Content?.Id : null);
    }

    private async Task<WriteOutcome> TranslateAsync(TranslateOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await EditableAsync(op.Ref, cancellationToken);
        var language = session.Language(op.Lang) ?? throw new UsageException("translate needs --lang <code>.");
        PropertyNameCheck.Check(session.Model, target.Header.TypeId, op.Properties);
        var request = new LanguageBranchRequest
        {
            Lang = language.Code,
            Name = op.Name,
            Properties = PropertyArguments.ToRequest(op.Properties),
            Publish = op.Publish,
            DryRun = dryRun,
        };
        var result = await PostAsync<WriteResult>(AgentRoutes.Languages(target.ContentRef), request, cancellationToken);
        return Outcome(WriteOutput.From(result), null);
    }

    private async Task<WriteOutcome> PublishAsync(PublishOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await EditableAsync(op.Ref, cancellationToken);
        if (op.Version is { } given && target.Version is { } inRef && given != inRef)
        {
            throw new UsageException($"The ref names version {inRef} but --version says {given}.");
        }
        var language = LanguageFor(op.Lang, target);
        var versionId = op.Version ?? target.Version;

        if (!dryRun)
        {
            var result = await PostAsync<WriteResult>(AgentRoutes.Publish(target.ContentRef), new PublishRequest { Version = versionId, Lang = language?.Code }, cancellationToken);
            return Outcome(WriteOutput.From(result), null);
        }

        var version = versionId is { } id
            ? await VersionReader.ByIdAsync(session.Db, session.Model, id, cancellationToken)
            : await VersionReader.LatestAsync(session.Db, session.Model, target.Id, (language ?? MasterLanguage(target)).Id, cancellationToken);
        if (version is null || version.ContentId != target.Id)
        {
            throw new NotFoundException($"Content {target.Id} has no version {(versionId is { } missing ? missing.ToString(CultureInfo.InvariantCulture) : "in that language")}.", $"List them with `opticli versions {target.Id}`.");
        }
        if (version.StatusValue == VersionStatus.Published)
        {
            throw new ConflictException($"Version {version.Ref} is already the published version.");
        }
        var identity = session.Identities.Describe(target.Header, session.Model.Language(version.LanguageId), version.Id, version.StatusValue, version.Name);
        var output = new WriteOutput(
            WriteOutput.Id(target.Id), version.Ref, identity.Guid, identity.Type, identity.Name, identity.Language, identity.Status,
            target.Header.ParentId is { } parent ? WriteOutput.Id(parent) : null,
            Saved: false, Published: false, DryRun: true, Valid: true, BaseVersion: version.Ref, Changes: [], Validation: null);
        return new WriteOutcome(output, DbSource, null,
            ["Dry-run publish checks only that the version exists and isn't published yet; the CMS validates it when it is actually published."]);
    }

    private async Task<WriteOutcome> MoveAsync(MoveOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = Movable(await EditableAsync(op.Ref, cancellationToken));
        var destination = await EditableAsync(op.To, cancellationToken, "--to");
        if (destination.Id == target.Id || destination.Header.AncestorIds.Contains(target.Id))
        {
            throw new UsageException($"Can't move {target.Id} below itself.");
        }
        var descendants = await DescendantCountAsync(target.Header, cancellationToken);
        if (dryRun)
        {
            return new WriteOutcome(DryMove(target, WriteOutput.Id(destination.Id), descendants, recycleBin: null), DbSource, null, []);
        }
        var result = await PostAsync<MoveResult>(AgentRoutes.Move(target.ContentRef), new MoveRequest { Parent = destination.ContentRef }, cancellationToken);
        return new WriteOutcome(Moved(result, descendants, recycleBin: null), AgentSource, null, []);
    }

    private async Task<WriteOutcome> DeleteAsync(DeleteOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = Movable(await EditableAsync(op.Ref, cancellationToken));
        if (target.Header.Deleted)
        {
            throw new ConflictException($"Content {target.Id} is already in the recycle bin.");
        }
        var descendants = await DescendantCountAsync(target.Header, cancellationToken);
        if (dryRun)
        {
            return new WriteOutcome(DryMove(target, null, descendants, recycleBin: true), DbSource, null, []);
        }
        var agent = await AgentAsync(cancellationToken);
        var result = await agent.SendAsync<MoveResult>(HttpMethod.Delete, AgentRoutes.Delete(target.ContentRef), null, cancellationToken);
        return new WriteOutcome(Moved(result, descendants, recycleBin: true), AgentSource, null, []);
    }

    /// <summary>
    /// The same rule the agent enforces, checked up front so a dry run gives the same answer: the root, the
    /// recycle bin, site start pages and asset roots are never moved or deleted.
    /// </summary>
    private Target Movable(Target target)
    {
        var sites = session.Model.Sites;
        var protectedIds = sites.All.SelectMany(site => new[] { SiteMap.StartPageId(site), SiteMap.AssetsRootId(site) })
            .Append(sites.GlobalAssetsRoot)
            .Append(sites.ContentAssetsRoot);
        var header = target.Header;
        if (header.ParentId is null || session.Model.TypeName(header.TypeId) == RecycleBinType || protectedIds.Contains(header.Id))
        {
            throw new RefusedException($"Content {header.Id} is a site root, start page, asset root or the recycle bin; opticli won't move or delete it.");
        }
        return target;
    }

    private MoveOutput DryMove(Target target, string? parent, int descendants, bool? recycleBin)
    {
        var identity = session.Identities.Describe(target.Header, null);
        return new MoveOutput(
            WriteOutput.Id(target.Id), identity.Guid, identity.Type, identity.Name, identity.Language, identity.Status,
            parent, target.Header.ParentId is { } previous ? WriteOutput.Id(previous) : null,
            Moved: false, DryRun: true, descendants, recycleBin);
    }

    private static MoveOutput Moved(MoveResult result, int descendants, bool? recycleBin) => new(
        WriteOutput.Id(result.Content.Id), result.Content.Guid, result.Content.Type, result.Content.Name, result.Content.Language, result.Content.Status,
        result.Parent, result.PreviousParent, Moved: true, DryRun: false, descendants, recycleBin);

    private static WriteOutcome Outcome(WriteOutput output, int? createdId)
    {
        var warnings = new List<string>();
        if (!output.DryRun && !output.Saved)
        {
            warnings.Add("Nothing changed, so no new version was saved.");
        }
        return new WriteOutcome(output, AgentSource, createdId, warnings);
    }

    /// <summary>
    /// The version the change must be based on: <paramref name="explicitVersion"/>, else the ref's own
    /// version, else the latest in the language, read now. The agent answers 409 if a newer one exists.
    /// </summary>
    private async Task<int?> BaseVersionAsync(Target target, LanguageBranch? language, int? explicitVersion, bool force, CancellationToken cancellationToken)
    {
        if (force)
        {
            return null;
        }
        if ((explicitVersion ?? target.Version) is { } version)
        {
            return version;
        }
        var latest = await VersionReader.LatestAsync(session.Db, session.Model, target.Id, (language ?? MasterLanguage(target)).Id, cancellationToken);
        return latest?.Id;
    }

    private LanguageBranch MasterLanguage(Target target) =>
        session.Model.Language(target.Header.MasterLanguageId)
        ?? throw new NotFoundException($"Content {target.Id} has no master language branch.");

    /// <summary><c>--lang</c>, else the language a URL ref selected, else null (the agent uses the master language).</summary>
    private LanguageBranch? LanguageFor(string? code, Target target) => session.Language(code) ?? target.UrlLanguage;

    private async Task<int> DescendantCountAsync(ContentHeader header, CancellationToken cancellationToken)
    {
        var prefix = $"{(header.ContentPath.Length == 0 ? "." : header.ContentPath)}{WriteOutput.Id(header.Id)}.%";
        var rows = await session.Db.QueryAsync("SELECT COUNT(*) AS N FROM tblContent WHERE ContentPath LIKE @prefix",
            r => r.GetInt32(0), cancellationToken, new SqlParameter("@prefix", prefix));
        return rows[0];
    }

    private async Task<T> PostAsync<T>(string route, object body, CancellationToken cancellationToken)
    {
        var agent = await AgentAsync(cancellationToken);
        return await agent.SendAsync<T>(HttpMethod.Post, route, body, cancellationToken);
    }

    private async Task<AgentClient> AgentAsync(CancellationToken cancellationToken) => _agent ??= await connect(cancellationToken);

    /// <summary>Content in this database (not provider content), which every edit needs to check names and versions.</summary>
    private async Task<Target> EditableAsync(string reference, CancellationToken cancellationToken, string what = "ref")
    {
        var target = await ResolveAsync(reference, what, cancellationToken);
        return target.Stored is not null
            ? target
            : throw new UsageException($"{reference} is content from a content provider; opticli can only change content stored in the CMS database.");
    }

    /// <exception cref="UsageException">Not a ref.</exception>
    /// <exception cref="NotFoundException">Nothing matches.</exception>
    private async Task<Target> ResolveAsync(string reference, string what, CancellationToken cancellationToken)
    {
        if (reference.StartsWith('$'))
        {
            throw new UsageException($"{what} '{reference}' refers to plan content; $ids only work inside `opticli apply`.");
        }
        var parsed = ContentRefParser.Parse(reference);
        if (parsed.Kind == ContentRefKind.Guid)
        {
            var ids = await ContentHeaderReader.IdsByGuidsAsync(session.Db, [parsed.Guid], cancellationToken);
            if (!ids.ContainsKey(parsed.Guid))
            {
                var provided = await ContentHeaderReader.ProviderContentAsync(session.Db, [parsed.Guid], cancellationToken);
                return provided.ContainsKey(parsed.Guid)
                    ? new Target(0, null, parsed.Guid.ToString("D"), null, null)
                    : throw new NotFoundException($"No content with GUID {parsed.Guid:D}.", "GUIDs are stable across environments, but the item may not exist in this database.");
            }
        }

        var located = await session.LocateAsync(reference, site, cancellationToken);
        var header = await session.HeaderAsync(located.Id, cancellationToken);
        return new Target(located.Id, located.VersionId, null, header, located.Url?.Language);
    }

    /// <param name="Guid">Set only for provider content, which is passed to the agent by GUID.</param>
    /// <param name="Stored">Null for provider content.</param>
    private sealed record Target(int Id, int? Version, string? Guid, ContentHeader? Stored, LanguageBranch? UrlLanguage)
    {
        /// <summary>The database header; only used on targets from <see cref="EditableAsync"/>, which have one.</summary>
        public ContentHeader Header => Stored ?? throw new InvalidOperationException("Provider content has no database header.");

        /// <summary>The content, without a version: what the agent's content-level routes and fields take.</summary>
        public string ContentRef => Guid ?? WriteOutput.Id(Id);

        /// <summary>With the version when the ref named one: drafts are then based on it.</summary>
        public string AgentRef => Version is { } version ? WriteOutput.VersionRef(Id, version) : ContentRef;
    }
}
