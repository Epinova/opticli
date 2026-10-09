using System.Globalization;
using Microsoft.Data.SqlClient;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Drift;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
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
/// The agent only accepts ids, versions and GUIDs, so URLs are resolved here; content from a content provider (not in
/// this database) is passed to the site by GUID, also when it was given as <c>63__provider</c>.
/// Publish and delete have no dry run in the agent; for those the checks run here, against the database. A move's dry run
/// asks the agent, which knows which types may go below the new parent.
/// </remarks>
/// <param name="projectDirectory">The site project, to show where an uploaded file landed on disk.</param>
/// <param name="updateExisting">
/// Make steps safe to run again (<c>apply --update-existing</c>): content that exists with a step's GUID is updated,
/// an <c>area add</c> of an item already there, a <c>translate</c> to an existing branch (which becomes a <c>set</c>),
/// a <c>publish</c> of a published version and a <c>delete</c> of deleted content change nothing.
/// </param>
/// <param name="confirmDraft">
/// Asks a person at a terminal whether a publish should also put live the unpublished changes someone else saved, or a
/// discard delete them (the agent's message, <see cref="PendingDraft"/> and the question); true sends the write again, confirmed. Null: such a publish
/// fails with a <c>conflict</c> whose details list the changes.
/// </param>
/// <param name="confirmReferences">
/// Asks a person at a terminal whether to delete content that other content references (the message and the
/// references); true deletes it. Null: such a delete fails with a <c>conflict</c> whose details list them.
/// </param>
/// <param name="confirm">Asks a person at a terminal a yes/no question (the message, then the question). Null: fail with a <c>conflict</c> instead.</param>
/// <param name="acceptDrift">
/// The drift fingerprint the user confirmed (<c>--accept-drift</c>): writes go ahead against a shared database whose
/// content model differs from the build's, as long as the differences are still those.
/// </param>
/// <param name="confirmDrift">
/// Asks a person at a terminal whether to write although the build and the shared database differ (the report); true
/// confirms its fingerprint. Null: such a write fails with <c>drift</c>.
/// </param>
/// <param name="sharedDatabase">Whether drift is checked; default: when <paramref name="session"/>'s database is remote.</param>
public sealed class WriteExecutor(
    ContentSession session,
    Func<CancellationToken, Task<AgentClient>> connect,
    string? site = null,
    string? projectDirectory = null,
    bool updateExisting = false,
    Func<string, PendingDraft, string, bool>? confirmDraft = null,
    Func<string, Queries.IncomingReferences, bool>? confirmReferences = null,
    Func<string, string, bool>? confirm = null,
    string? acceptDrift = null,
    Func<DriftReport, bool>? confirmDrift = null,
    bool? sharedDatabase = null)
{
    public bool UpdateExisting => updateExisting;

    public const string AgentSource = "agent";
    public const string DbSource = "db";

    /// <summary>Content type of the recycle bin (<c>ContentReference.WasteBasket</c>).</summary>
    private const string RecycleBinType = "SysRecycleBin";

    /// <summary>What to do about a publish that would include someone else's unpublished changes.</summary>
    public const string PendingDraftHint =
        "details.draft lists the unpublished changes and who saved them. Ask the user whether they should go live too, and only if so run again with --include-draft (in a plan: \"includeDraft\": true on the step). Without --publish the change is saved as a draft and nothing goes live; `opticli publish <ref> --version <id>` publishes one version as it is.";

    private AgentClient? _agent;

    /// <summary>The drift check passed for this run (nothing differs, or the user confirmed it).</summary>
    private bool _driftAccepted;

    /// <exception cref="ContentValidationException">A dry run found the change would fail validation (details: the dry-run result).</exception>
    public async Task<WriteOutcome> RunAsync(WriteOperation operation, bool dryRun, CancellationToken cancellationToken)
    {
        if (operation.PublishAt is not null)
        {
            if (operation is not (SetOperation or AreaEdit or CompositionEdit or CreateOperation or PublishOperation))
            {
                throw new UsageException($"{operation.Kind} can't be scheduled; --publish-at works on set, area, composition, create and publish.");
            }
            if (operation is SetOperation { Publish: true } or AreaEdit { Publish: true } or CompositionEdit { Publish: true } or CreateOperation { Publish: true })
            {
                throw new UsageException("Give --publish (now) or --publish-at (later), not both.");
            }
            if (operation.RequestApproval)
            {
                throw new UsageException("A review request can't be scheduled: the reviewers decide when it is published.", "Give --publish-at or --request-approval, not both.");
            }
        }
        if (!dryRun)
        {
            await RequireDriftAcceptedAsync(cancellationToken);
        }
        var outcome = operation switch
        {
            SetOperation set => await SetAsync(set, dryRun, cancellationToken),
            AreaEdit area => await AreaAsync(area, dryRun, cancellationToken),
            CompositionEdit composition => await CompositionAsync(composition, dryRun, cancellationToken),
            CreateOperation create => await CreateAsync(create, dryRun, cancellationToken),
            BlockCreateOperation block => await BlockAsync(block, dryRun, cancellationToken),
            UploadOperation upload => await UploadAsync(upload, dryRun, cancellationToken),
            TranslateOperation translate => await TranslateAsync(translate, dryRun, cancellationToken),
            PublishOperation publish => await PublishAsync(publish, dryRun, cancellationToken),
            UnpublishOperation unpublish => await UnpublishAsync(unpublish, dryRun, cancellationToken),
            DiscardOperation discard => await DiscardAsync(discard, dryRun, cancellationToken),
            MoveOperation move => await MoveAsync(move, dryRun, cancellationToken),
            DeleteOperation delete => await DeleteAsync(delete, dryRun, cancellationToken),
            RestoreOperation restore => await RestoreAsync(restore, dryRun, cancellationToken),
            AccessOperation access => await AccessAsync(access, dryRun, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown operation {operation.GetType().Name}."),
        };

        if (outcome.Output is WriteOutput { DryRun: true, PendingDraft: { } draft })
        {
            var then = operation.IncludeDraft
                ? $"--include-draft {(operation is DiscardOperation ? "discards it all the same" : "publishes them too")}"
                : "the real run fails with a conflict unless --include-draft (ask the user first)";
            var what = operation is DiscardOperation ? $"discarding would delete {draft.Describe()} for good" : $"publishing would also put live {draft.Describe()}";
            outcome = outcome with { Warnings = [.. outcome.Warnings, $"pendingDraft: {what}; {then}."] };
        }
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
        op.From?.Check(target.Id, target.Version);
        // CMS 13: "composition" in the values is an experience's (or section's) whole Visual Builder composition.
        var (split, composition) = CompositionInput.Split(session.Model, target.Header.TypeId, op.Properties);
        var properties = PropertyNameCheck.Prepare(session.Model, target.Header.TypeId, split);
        RequireVariations(op.Variation);
        var language = LanguageFor(op.Lang, target);
        var masterFirst = await MasterFirstAsync(target, language, op, dryRun, cancellationToken);
        var areaOps = new List<AreaOperation>();
        foreach (var edit in op.AreaEdits ?? [])
        {
            areaOps.Add(await AreaOperationAsync(edit, target, cancellationToken));
        }
        var compositionOps = new List<CompositionOperation>();
        foreach (var edit in op.CompositionEdits ?? [])
        {
            compositionOps.Add(await CompositionOperationAsync(edit, target, cancellationToken));
        }
        var request = new DraftRequest
        {
            Lang = language?.Code,
            Name = op.Name,
            Properties = PropertyArguments.ToRequest(properties),
            AreaOps = areaOps.Count > 0 ? areaOps : null,
            Composition = composition is null ? null : await CompositionInput.RootAsync(session.Model, composition, Resolvers(cancellationToken)),
            CompositionOps = compositionOps.Count > 0 ? compositionOps : null,
            Variation = op.Variation?.Trim(),
            Publish = op.Publish,
            IncludeDraft = op.IncludeDraft,
            RequestApproval = op.RequestApproval,
            PublishAt = op.PublishAt?.UtcDateTime,
            DryRun = dryRun,
            BaseVersion = await BaseVersionAsync(target, language, op.BaseVersion, op.Force, cancellationToken, op.Variation),
            From = op.From?.Request,
        };
        if (request.Properties is null && request.Name is null && request.Composition is null && op.AreaEdits is null && op.CompositionEdits is null)
        {
            throw new UsageException("Nothing to set.", $"Give properties ({PropertyArguments.Syntax}), --values or --name.");
        }
        return WithWarning(await DraftAsync(target, request, op.From, op.Publishes, cancellationToken), masterFirst);
    }

    private async Task<WriteOutcome> AreaAsync(AreaEdit op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await EditableAsync(op.Ref, cancellationToken);
        op.From?.Check(target.Id, target.Version);
        var edit = await AreaOperationAsync(op, target, cancellationToken);
        var language = LanguageFor(op.Lang, target);
        var masterFirst = await MasterFirstAsync(target, language, op, dryRun, cancellationToken);
        var request = new DraftRequest
        {
            Lang = language?.Code,
            AreaOps = [edit],
            Publish = op.Publish,
            IncludeDraft = op.IncludeDraft,
            RequestApproval = op.RequestApproval,
            PublishAt = op.PublishAt?.UtcDateTime,
            DryRun = dryRun,
            BaseVersion = await BaseVersionAsync(target, language, op.BaseVersion, op.Force, cancellationToken),
            From = op.From?.Request,
        };
        return WithWarning(await DraftAsync(target, request, op.From, op.Publishes, cancellationToken), masterFirst);
    }

    private async Task<WriteOutcome> CompositionAsync(CompositionEdit op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await EditableAsync(op.Ref, cancellationToken);
        op.From?.Check(target.Id, target.Version);
        RequireVariations(op.Variation);
        var edit = await CompositionOperationAsync(op, target, cancellationToken);
        var language = LanguageFor(op.Lang, target);
        var masterFirst = await MasterFirstAsync(target, language, op, dryRun, cancellationToken);
        var request = new DraftRequest
        {
            Lang = language?.Code,
            CompositionOps = [edit],
            Variation = op.Variation?.Trim(),
            Publish = op.Publish,
            IncludeDraft = op.IncludeDraft,
            RequestApproval = op.RequestApproval,
            PublishAt = op.PublishAt?.UtcDateTime,
            DryRun = dryRun,
            BaseVersion = await BaseVersionAsync(target, language, op.BaseVersion, op.Force, cancellationToken, op.Variation),
            From = op.From?.Request,
        };
        return WithWarning(await DraftAsync(target, request, op.From, op.Publishes, cancellationToken), masterFirst);
    }

    /// <summary>A composition edit as the agent takes it, with its shape, the content's type, block types and property names checked.</summary>
    private async Task<CompositionOperation> CompositionOperationAsync(CompositionEdit op, Target target, CancellationToken cancellationToken)
    {
        if (CompositionEdits.Problem(op) is { } problem)
        {
            throw new UsageException($"composition {op.Action}: {problem}", CompositionEdits.Syntax);
        }
        if (!Properties.Compositions.IsLayouted(session.Model, target.Header.TypeId))
        {
            throw new UsageException($"{target.Id} is a {session.Model.TypeName(target.Header.TypeId)}, which has no Visual Builder composition; only experiences and sections (CMS 13) have one.",
                "`opticli types --kind experience` lists the experience types.");
        }
        var value = op.Action switch
        {
            CompositionOps.Add => await CompositionInput.NodeAsync(session.Model, op.Value!, op.NodeType, "the new node", Resolvers(cancellationToken)),
            CompositionOps.Set => CompositionInput.Change(session.Model, op.Value!, "the change"),
            _ => null,
        };
        return new CompositionOperation { Op = op.Action, Node = op.Node, Parent = op.Parent, At = op.At, Value = value };
    }

    /// <summary>How a composition's shared blocks and blueprints are named to the agent: by content ref and by GUID.</summary>
    private CompositionInput.Resolvers Resolvers(CancellationToken cancellationToken) => new(
        async reference => (await ResolveAsync(reference, "shared block", cancellationToken)).ContentRef,
        async blueprint => (await BlueprintReader.FindAsync(session, blueprint, cancellationToken)).Guid);

    private void RequireVariations(string? variation)
    {
        if (variation is not null && !session.Model.Schema.Variations)
        {
            throw new UsageException("Content variations are CMS 13's; this site's database has none.", "Leave out --variation.");
        }
    }

    /// <summary>An area edit as the agent takes it, with its property and item checked.</summary>
    private async Task<AreaOperation> AreaOperationAsync(AreaEdit op, Target target, CancellationToken cancellationToken)
    {
        var property = PropertyNameCheck.RequireContentArea(session.Model, target.Header.TypeId, op.Property);
        var edit = op.Action switch
        {
            AreaOps.Add when op.Type is not null => InlineAdd(op, property.Name),
            AreaOps.Add => new AreaOperation
            {
                Op = AreaOps.Add,
                Property = property.Name,
                Ref = (await ResolveAsync(op.Item ?? throw new UsageException("area add needs the block to add: its ref, or --type for a new inline block."), "block", cancellationToken)).ContentRef,
                At = op.At,
                DisplayOption = op.Display,
                IfMissing = updateExisting,
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
        if (edit.Op != AreaOps.Add && (op.Type is not null || op.Values is not null || op.Name is not null))
        {
            throw new UsageException($"area {op.Action} takes no type, values or name; those are for adding an inline block.");
        }
        return edit;
    }

    /// <summary>
    /// <c>area add --type</c>: a new inline block with its values (names checked against the type, <c>get</c>'s shape
    /// unwrapped), as the agent takes it. With <c>--update-existing</c> an inline block of the type with those values
    /// counts as already there.
    /// </summary>
    private AreaOperation InlineAdd(AreaEdit op, string property)
    {
        if (op.Item is not null)
        {
            throw new UsageException("area add takes a block ref or --type (a new inline block), not both.", "Prop=value arguments after --type are the inline block's values.");
        }
        var type = PropertyNameCheck.BlockType(session.Model, op.Type!, "--type");
        var values = op.Values is null ? null : PropertyNameCheck.InlineValues(session.Model, type, op.Values, "");
        return new AreaOperation
        {
            Op = AreaOps.Add,
            Property = property,
            Type = type.Name,
            Values = PropertyArguments.ToRequest(values),
            Name = op.Name,
            At = op.At,
            DisplayOption = op.Display,
            IfMissing = updateExisting,
        };
    }

    /// <param name="from">What <c>--from</c> based the change on, for the warning about the newer versions it leaves out.</param>
    private async Task<WriteOutcome> DraftAsync(Target target, DraftRequest request, FromVersion? from, bool publishes, CancellationToken cancellationToken)
    {
        WriteResult result;
        try
        {
            result = await PublishingPostAsync(AgentRoutes.Draft(target.AgentRef), request, r => r with { IncludeDraft = true }, cancellationToken);
        }
        catch (ConflictException ex) when (ex.Details is AgentErrorDetails { CurrentVersion: { } current })
        {
            // The agent's hint speaks protocol ("baseVersion"); say it in command-line terms.
            throw new ConflictException(ex.Message, ConflictHint(WriteOutput.VersionRef(target.Id, current), from)) { Details = ex.Details };
        }
        var output = WriteOutput.From(result);
        var outcome = Outcome(output, null);
        if (LeftOutVersions.Warning(output, from, publishes) is { } leftOut)
        {
            outcome = outcome with { Warnings = [.. outcome.Warnings, leftOut] };
        }
        return await WithSimpleAddressCheckAsync(outcome, result, target.Header, self: true, cancellationToken);
    }

    /// <summary>What to do when someone saved <paramref name="latest"/> after the version the change expected to be the latest.</summary>
    public static string ConflictHint(string latest, FromVersion? from) => from is null
        ? $"Someone saved {latest} after the version this change was based on. Look at it (opticli get {latest}), then run the command again: without --base-version it is based on the latest version; to base it on an older one and leave the newer ones out, use --from <version> (or --from published); --force skips the check."
        : $"Someone saved {latest} after the version this change expected to be the latest. Look at it (opticli get {latest}), then run the command again: --from still bases the change on {(from.IsPublished ? "the published version" : $"version {from}")}, and without --base-version it is checked against the latest version; --force skips the check.";

    /// <param name="header">The page written, or the parent of new content (<paramref name="self"/> false).</param>
    private async Task<WriteOutcome> WithSimpleAddressCheckAsync(WriteOutcome outcome, WriteResult result, ContentHeader? header, bool self, CancellationToken cancellationToken)
    {
        if (header is null)
        {
            return outcome;
        }
        var warnings = await SimpleAddressCheck.WarningsAsync(
            session, result, header.AncestorIds.Append(header.Id).ToList(), self ? header.Id : result.Content?.Id, cancellationToken);
        return warnings.Count == 0 ? outcome : outcome with { Warnings = [.. outcome.Warnings, .. warnings] };
    }

    private async Task<WriteOutcome> CreateAsync(CreateOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var parent = await ResolveAsync(op.Parent, "parent", cancellationToken);
        // CMS 13: from a blueprint, whose type it is.
        var blueprint = op.Blueprint is { } named ? await BlueprintReader.FindAsync(session, named, cancellationToken) : null;
        var type = string.IsNullOrWhiteSpace(op.Type) && blueprint is not null
            ? session.Model.Type(blueprint.TypeId) ?? throw new NotFoundException($"The blueprint {blueprint.Id} has a content type the database doesn't have.")
            : session.Model.RequireType(op.Type);
        if (blueprint is not null && blueprint.TypeId != type.Id)
        {
            throw new UsageException($"The blueprint {blueprint.Id} ('{blueprint.Name}') is {session.Model.TypeName(blueprint.TypeId)}, not {type.Name}.", "Leave out --type: content made from a blueprint has its type.");
        }
        if (type.Kind == ContentKind.Media)
        {
            throw new UsageException($"{type.Name} is a media type; created this way it would be a media item without a file.",
                "Use `opticli upload <file> --parent <folder>` (or --for <page>); --type picks the media type.");
        }
        var (split, composition) = CompositionInput.Split(session.Model, type.Id, op.Properties);
        var properties = PropertyNameCheck.Prepare(session.Model, type.Id, split);
        var request = new CreateRequest
        {
            Parent = parent.ContentRef,
            Type = type.Name,
            Name = op.Name,
            Lang = session.Language(op.Lang)?.Code,
            Properties = PropertyArguments.ToRequest(properties),
            Composition = composition is null ? null : await CompositionInput.RootAsync(session.Model, composition, Resolvers(cancellationToken)),
            Blueprint = blueprint?.Guid,
            Publish = op.Publish,
            DryRun = dryRun,
            Guid = op.ContentGuid,
            UpdateExisting = updateExisting,
            IncludeDraft = op.IncludeDraft,
            RequestApproval = op.RequestApproval,
            PublishAt = op.PublishAt?.UtcDateTime,
            ParentType = dryRun ? op.PlannedParentType : null,
        };
        return await CreatedAsync(request, type.Name, cancellationToken, parent.Stored);
    }

    private async Task<WriteOutcome> BlockAsync(BlockCreateOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        if ((op.For is null) == (op.Parent is null))
        {
            throw new UsageException("Give exactly one of --for <ref> (the content's \"For this page\" folder) or --parent <folder>.");
        }
        var type = session.Model.RequireType(op.Type);
        if (!type.Kind.IsBlock())
        {
            throw new UsageException($"{type.Name} is a {type.Kind.ToString().ToLowerInvariant()} type, not a block type.", "Use `opticli create` for pages and folders.");
        }
        var properties = PropertyNameCheck.Prepare(session.Model, type.Id, op.Properties);
        var request = new CreateRequest
        {
            Parent = op.Parent is null ? null : (await ResolveAsync(op.Parent, "parent", cancellationToken)).ContentRef,
            ForContent = op.For is null ? null : (await ResolveAsync(op.For, "--for", cancellationToken)).ContentRef,
            Type = type.Name,
            Name = op.Name,
            Lang = session.Language(op.Lang)?.Code,
            Properties = PropertyArguments.ToRequest(properties),
            Publish = op.Publish,
            DryRun = dryRun,
            Guid = op.ContentGuid,
            UpdateExisting = updateExisting,
            IncludeDraft = op.IncludeDraft,
            RequestApproval = op.RequestApproval,
        };
        return await CreatedAsync(request, type.Name, cancellationToken);
    }

    private async Task<WriteOutcome> UploadAsync(UploadOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        if (op.Replace is not null)
        {
            return await ReplaceAsync(op, dryRun, cancellationToken);
        }
        if ((op.For is null) == (op.Parent is null))
        {
            throw new UsageException("Give exactly one of --for <ref> (the content's \"For this page\" folder) or --parent <folder>.");
        }
        var file = MediaFiles.Check(op.File);
        ContentTypeInfo? type = null;
        if (op.Type is not null)
        {
            type = session.Model.RequireType(op.Type);
            if (type.Kind != ContentKind.Media)
            {
                throw new UsageException($"{type.Name} is a {type.Kind.ToString().ToLowerInvariant()} type, not a media type.", "See `opticli types --kind media`.");
            }
            PropertyNameCheck.Check(session.Model, type.Id, op.Properties);
        }
        var request = new UploadRequest
        {
            Parent = op.Parent is null ? null : (await ResolveAsync(op.Parent, "parent", cancellationToken)).ContentRef,
            ForContent = op.For is null ? null : (await ResolveAsync(op.For, "--for", cancellationToken)).ContentRef,
            // The name the user gave the file, even when it is a link to a file with another name.
            FileName = Path.GetFileName(op.File),
            Name = op.Name,
            Type = type?.Name,
            Properties = PropertyArguments.ToRequest(op.Properties),
            Data = dryRun ? null : Convert.ToBase64String(await File.ReadAllBytesAsync(file.FullName, cancellationToken)),
            Publish = op.Publish,
            DryRun = dryRun,
            Guid = op.ContentGuid,
            UpdateExisting = updateExisting,
            IncludeDraft = op.IncludeDraft,
            RequestApproval = op.RequestApproval,
        };

        var result = await PublishingPostAsync(AgentRoutes.Media, request, r => r with { IncludeDraft = true }, cancellationToken);
        var output = Created(result, result.MediaType ?? type?.Name, request.Name ?? request.FileName, request.Parent, request.Guid) with
        {
            Upload = new UploadInfo(file.FullName, file.Length, result.Saved && !result.Existing && result.Content is { } saved ? await BlobAsync(saved.Id, cancellationToken) : null),
        };
        return Outcome(output, CreatedId(result));
    }

    private async Task<WriteOutcome> ReplaceAsync(UploadOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        if (op.For is not null || op.Parent is not null || op.Type is not null || op.ContentGuid is not null)
        {
            throw new UsageException("--replace replaces the file of existing media; it takes no --for, --parent or --type (the media keeps its type).");
        }
        var file = MediaFiles.Check(op.File);
        var target = await EditableAsync(op.Replace!, cancellationToken, "--replace");
        if (session.Model.Kind(target.Header.TypeId) != ContentKind.Media)
        {
            throw new UsageException($"{target.Id} is a {session.Model.Kind(target.Header.TypeId).ToString().ToLowerInvariant()}, not media, so it has no file to replace.");
        }
        PropertyNameCheck.Check(session.Model, target.Header.TypeId, op.Properties);
        var request = new UploadRequest
        {
            Replace = target.ContentRef,
            FileName = Path.GetFileName(op.File),
            Name = op.Name,
            Properties = PropertyArguments.ToRequest(op.Properties),
            Data = dryRun ? null : Convert.ToBase64String(await File.ReadAllBytesAsync(file.FullName, cancellationToken)),
            Publish = op.Publish,
            DryRun = dryRun,
            IncludeDraft = op.IncludeDraft,
            RequestApproval = op.RequestApproval,
        };
        var result = await PublishingPostAsync(AgentRoutes.Media, request, r => r with { IncludeDraft = true }, cancellationToken);
        var output = WriteOutput.From(result) with
        {
            // The database's file is the published version's: only shown once the new one is.
            Upload = new UploadInfo(file.FullName, file.Length, result is { Saved: true, Published: true } && result.Content is { } saved ? await BlobAsync(saved.Id, cancellationToken) : null),
        };
        var outcome = Outcome(output, null);
        var note = dryRun
            ? $"The file of {target.Id} would be replaced by {Path.GetFileName(op.File)} ({file.Length} bytes) in a new version{(op.Publish ? ", published" : ", a draft")}; changes lists only properties."
            : $"{output.Version} has the new file{(output.Published ? "" : "; the published version keeps the old one until it is published")}.";
        return outcome with { Warnings = [note, .. outcome.Warnings.Where(w => !w.StartsWith("Nothing changed", StringComparison.Ordinal))] };
    }

    /// <summary>Where the site stored a media item's file, read back from the database.</summary>
    private async Task<Queries.BlobLocation?> BlobAsync(int id, CancellationToken cancellationToken)
    {
        var header = await ContentHeaderReader.ByIdAsync(session.Db, id, cancellationToken);
        return header?.LanguageRow(null)?.BlobUri is { } uri ? Queries.BlobLocator.Locate(uri, projectDirectory) : null;
    }

    /// <param name="parent">The parent's header, for the simple address check.</param>
    private async Task<WriteOutcome> CreatedAsync(CreateRequest request, string typeName, CancellationToken cancellationToken, ContentHeader? parent = null)
    {
        var result = await PublishingPostAsync(AgentRoutes.Create, request, r => r with { IncludeDraft = true }, cancellationToken);
        return await WithSimpleAddressCheckAsync(
            Outcome(Created(result, typeName, request.Name, request.Parent, request.Guid), CreatedId(result)), result, parent, self: false, cancellationToken);
    }

    /// <param name="guid">The GUID asked for, shown for a dry run, which has no content yet.</param>
    private static WriteOutput Created(WriteResult result, string? typeName, string? name, string? parent, Guid? guid)
    {
        var output = WriteOutput.From(result, typeName, name, parent);
        return output with
        {
            Guid = output.Guid ?? guid,
            Existing = result.Existing ? true : null,
            Restored = result.Restored ? true : null,
        };
    }

    /// <summary>What a plan's <c>$id</c> binds to: new content once saved, existing content also when unchanged.</summary>
    private static int? CreatedId(WriteResult result) =>
        (result.Saved || result.Existing) && !result.DryRun ? result.Content?.Id : null;

    private async Task<WriteOutcome> TranslateAsync(TranslateOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await EditableAsync(op.Ref, cancellationToken);
        var language = session.Language(op.Lang) ?? throw new UsageException("translate needs --lang <code>.");
        if (op.Remove)
        {
            return await RemoveLanguageAsync(op, target, language, dryRun, cancellationToken);
        }
        // An existing branch is updated by a set or publish below, which check it themselves.
        var masterFirst = updateExisting && target.Header.Languages.ContainsKey(language.Id)
            ? null
            : await MasterFirstAsync(target, language, op, dryRun, cancellationToken, creatingBranch: true);
        var blocks = op.WithBlocks ? await AssetBlocksAsync(target.Header, cancellationToken) : [];
        if (op.WithBlocks && !dryRun)
        {
            // Every block is checked before anything is saved, so a block that can't be translated stops it all.
            await TranslateBlocksAsync(blocks, op, language, dryRun: true, cancellationToken);
        }

        WriteOutcome outcome;
        if (updateExisting && target.Header.Languages.ContainsKey(language.Id))
        {
            var existing = op.Properties is not null || op.Name is not null
                ? await SetAsync(new SetOperation(op.Ref, op.Properties, op.Name, language.Code, op.Publish, Force: true) { IncludeDraft = op.IncludeDraft, RequestApproval = op.RequestApproval }, dryRun, cancellationToken)
                : op.Publish || op.RequestApproval
                    ? await PublishAsync(new PublishOperation(op.Ref, Lang: language.Code) { IncludeDraft = op.IncludeDraft, RequestApproval = op.RequestApproval }, dryRun, cancellationToken)
                    : await LatestUnchangedAsync(target, language, dryRun, $"The {language.Code} branch already exists; nothing to do.", cancellationToken);
            outcome = existing with { Output = ((WriteOutput)existing.Output) with { Existing = true } };
        }
        else
        {
            var properties = PropertyNameCheck.Prepare(session.Model, target.Header.TypeId, op.Properties);
            var request = new LanguageBranchRequest
            {
                Lang = language.Code,
                Name = op.Name,
                Properties = PropertyArguments.ToRequest(properties),
                Publish = op.Publish,
                RequestApproval = op.RequestApproval,
                DryRun = dryRun,
            };
            var result = await PostAsync<WriteResult>(AgentRoutes.Languages(target.ContentRef), request, cancellationToken);
            outcome = WithWarning(await WithSimpleAddressCheckAsync(Outcome(WriteOutput.From(result), null), result, target.Header, self: true, cancellationToken), masterFirst);
        }
        if (!op.WithBlocks)
        {
            return outcome;
        }
        var translated = await TranslateBlocksAsync(blocks, op, language, dryRun, cancellationToken);
        return outcome with { Output = ((WriteOutput)outcome.Output) with { Blocks = translated } };
    }

    /// <summary>The blocks in the content's "For this page" folder, and in folders below it.</summary>
    private async Task<IReadOnlyList<ContentHeader>> AssetBlocksAsync(ContentHeader owner, CancellationToken cancellationToken)
    {
        var ids = await session.Db.QueryAsync("""
            SELECT a.pkID
            FROM tblContent f
            JOIN tblContent a ON a.ContentPath LIKE f.ContentPath + CAST(f.pkID AS varchar(12)) + '.%'
            WHERE f.ContentOwnerID = @owner AND f.Deleted = 0 AND a.Deleted = 0
            """, r => r.GetInt32(0), cancellationToken, new SqlParameter("@owner", owner.Guid));
        var headers = await ContentHeaderReader.ByIdsAsync(session.Db, ids, cancellationToken);
        return headers.Values.Where(h => session.Model.Kind(h.TypeId).IsBlock()).OrderBy(h => h.Id).ToList();
    }

    /// <summary>
    /// Each block without the branch gets it, published with the content's <c>--publish</c> (and through its own approval
    /// sequence with <c>--request-approval</c>); a review request alone leaves the blocks as drafts.
    /// </summary>
    private async Task<IReadOnlyList<BlockTranslation>> TranslateBlocksAsync(IReadOnlyList<ContentHeader> blocks, TranslateOperation op, LanguageBranch language, bool dryRun, CancellationToken cancellationToken)
    {
        var results = new List<BlockTranslation>();
        foreach (var block in blocks)
        {
            var identity = session.Identities.Describe(block, null);
            var reference = WriteOutput.Id(block.Id);
            if (block.Languages.ContainsKey(language.Id))
            {
                results.Add(new BlockTranslation(reference, identity.Name, identity.Type, null, "exists"));
                continue;
            }
            try
            {
                var result = await PostAsync<WriteResult>(AgentRoutes.Languages(reference),
                    new LanguageBranchRequest { Lang = language.Code, Publish = op.Publish, RequestApproval = op.RequestApproval && op.Publish, DryRun = dryRun }, cancellationToken);
                if (dryRun && op.Publish && !op.RequestApproval)
                {
                    // Once the block turned out to be localizable. A real run dry-runs every block first, so this is
                    // checked before anything is saved there too, and isn't worded as a dry run.
                    await MasterFirstAsync(new Target(block.Id, null, null, block, null), language, new TranslateOperation(reference, language.Code, Publish: true),
                        dryRun: false, cancellationToken, creatingBranch: true);
                }
                var output = WriteOutput.From(result);
                results.Add(new BlockTranslation(reference, identity.Name, identity.Type, output.Version, "translated"));
            }
            catch (UsageException ex) when (ex.Message.Contains("not localizable", StringComparison.Ordinal))
            {
                results.Add(new BlockTranslation(reference, identity.Name, identity.Type, null, "notLocalizable"));
            }
            catch (OptiCliException ex)
            {
                throw OptiCliException.Create(ex.Code, $"Block {reference} ('{identity.Name}') in the \"For this page\" folder can't be translated: {ex.Message}", ex.Hint, ex.Details);
            }
        }
        return results;
    }

    private async Task<WriteOutcome> RemoveLanguageAsync(TranslateOperation op, Target target, LanguageBranch language, bool dryRun, CancellationToken cancellationToken)
    {
        if (op.Properties is not null || op.Name is not null || op.Publish || op.RequestApproval || op.WithBlocks)
        {
            throw new UsageException("--remove deletes the branch; it takes no properties, --name, --publish, --request-approval or --with-blocks.");
        }
        var route = AgentRoutes.RemoveLanguage(target.ContentRef);
        var result = await PostAsync<RemoveLanguageResult>(route, new RemoveLanguageRequest { Lang = language.Code, DryRun = true }, cancellationToken);
        var message = $"Removing the '{result.Language}' branch of {target.Id} deletes its {result.Versions} version(s) for good{(result.Published ? ", and takes the content offline in that language" : "")}.";
        if (!dryRun)
        {
            if (!op.Confirm)
            {
                if (confirm is null)
                {
                    throw new ConflictException(message, RemoveBranchHint)
                    {
                        Details = new { reason = RemoveBranchReason, language = result.Language, result.Versions, result.Published },
                    };
                }
                if (!confirm(message, $"Remove the '{result.Language}' branch?"))
                {
                    throw new ConflictException("Not removed, as answered; nothing was changed.", "The branch stays as it is.");
                }
            }
            result = await PostAsync<RemoveLanguageResult>(route, new RemoveLanguageRequest { Lang = language.Code }, cancellationToken);
        }
        var output = new RemoveLanguageOutput(WriteOutput.Id(result.Content.Id), result.Content.Guid, result.Content.Type, result.Content.Name,
            result.Language, result.Versions, result.Published, result.Removed, result.DryRun);
        return new WriteOutcome(output, AgentSource, null, dryRun ? [$"{message} This can't be undone; the real run needs --confirm (ask the user first)."] : []);
    }

    public const string RemoveBranchReason = "removesBranch";

    public const string RemoveBranchHint =
        "Removing a language branch can't be undone. Ask the user, and only if so run again with --confirm (in a plan: \"confirm\": true on the step).";

    private async Task<WriteOutcome> PublishAsync(PublishOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await EditableAsync(op.Ref, cancellationToken);
        if (op.Version is { } given && target.Version is { } inRef && given != inRef)
        {
            throw new UsageException($"The ref names version {inRef} but --version says {given}.");
        }
        var language = LanguageFor(op.Lang, target);
        var versionId = op.Version ?? target.Version;
        var branch = language;
        if (versionId is { } named)
        {
            // A named version is published in its own language; one that isn't there is reported below.
            var own = await VersionReader.ByIdAsync(session.Db, session.Model, named, cancellationToken);
            branch = own is not null && own.ContentId == target.Id ? session.Model.Language(own.LanguageId) : null;
        }
        var masterFirst = await MasterFirstAsync(target, branch, op, dryRun, cancellationToken);

        var request = new PublishRequest { Version = versionId, Lang = language?.Code, IncludeDraft = op.IncludeDraft, RequestApproval = op.RequestApproval, PublishAt = op.PublishAt?.UtcDateTime };
        if (!dryRun && !updateExisting)
        {
            var result = await PublishingPostAsync(AgentRoutes.Publish(target.ContentRef), request, r => r with { IncludeDraft = true }, cancellationToken);
            return WithWarning(Outcome(WriteOutput.From(result), null), masterFirst);
        }

        var version = versionId is { } id
            ? await VersionReader.ByIdAsync(session.Db, session.Model, id, cancellationToken)
            : await VersionReader.LatestAsync(session.Db, session.Model, target.Id, (language ?? MasterLanguage(target)).Id, cancellationToken);
        if (version is null || version.ContentId != target.Id)
        {
            throw new NotFoundException($"Content {target.Id} has no version {(versionId is { } missing ? missing.ToString(CultureInfo.InvariantCulture) : "in that language")}.", $"List them with `opticli versions {target.Id}`.");
        }
        if (op.PublishAt is { } at)
        {
            PublishTimes.Future(at, "--publish-at", DateTimeOffset.UtcNow);
        }
        if (version.StatusValue == VersionStatus.Published && !(version.StopPublish <= DateTime.UtcNow))
        {
            if (updateExisting)
            {
                return Unchanged(target, version, dryRun, $"{version.Ref} is already published; nothing to do.");
            }
            throw new ConflictException($"Version {version.Ref} is already the published version.");
        }
        if (!dryRun)
        {
            var result = await PublishingPostAsync(AgentRoutes.Publish(target.ContentRef), request, r => r with { IncludeDraft = true }, cancellationToken);
            return WithWarning(Outcome(WriteOutput.From(result), null), masterFirst);
        }
        var what = $"{target.Id} ('{version.Name}')";
        var sequence = await ApprovalReader.ResolveAsync(session.Db, session.Model, target.Header, cancellationToken);
        var forReview = ApprovalRules.Check(sequence, op, what);
        if (forReview && version.StatusValue == VersionStatus.AwaitingApproval)
        {
            throw new ConflictException($"{what} is in review: version {version.Ref} already awaits approval.", ApprovalRules.InReviewHint)
            {
                Details = new AgentErrorDetails(null, null) { Reason = AgentErrorReasons.InReview },
            };
        }
        var identity = session.Identities.Describe(target.Header, session.Model.Language(version.LanguageId), version.Id, version.StatusValue, version.Name);
        var output = new WriteOutput(
            WriteOutput.Id(target.Id), version.Ref, identity.Guid, identity.Type, identity.Name, identity.Language, identity.Status,
            target.Header.ParentId is { } parent ? WriteOutput.Id(parent) : null,
            Saved: false, Published: false, DryRun: true, Valid: true, BaseVersion: version.Ref, Changes: [], Validation: null)
        {
            // A named version is what the user chose to put live; the latest may hold someone else's draft.
            PendingDraft = versionId is null && !forReview ? await PendingDraftReader.FindAsync(session, version, cancellationToken) : null,
        };
        List<string> notes = ["Dry-run publish checks only that the version exists and isn't published yet; the CMS validates it when it is actually published."];
        if (forReview)
        {
            notes.Add($"It would be sent for review instead of published: its approval sequence ({sequence!.Describe()}) starts, and nothing goes live.");
        }
        if (op.PublishAt is { } scheduled)
        {
            notes.Add($"It would be scheduled: the CMS's scheduled job publishes it at {scheduled.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)}.");
        }
        // A named version needs no confirmation, but what else it puts live is worth knowing.
        if (versionId is not null && !forReview && await PendingDraftReader.FindAsync(session, version, cancellationToken) is { } others)
        {
            notes.Add($"For information: {version.Ref} also holds {others.Describe()}; naming the version confirms that they go live.");
        }
        if (masterFirst is not null)
        {
            notes.Add(masterFirst);
        }
        return new WriteOutcome(output, DbSource, null, notes);
    }

    /// <summary>
    /// <see cref="MasterLanguageRule"/> for a write that publishes (or requests approval of) <paramref name="language"/> of
    /// the target, before anything is saved.
    /// </summary>
    /// <param name="language">The branch written; null: the master branch.</param>
    /// <param name="creatingBranch">The write creates the branch (translate), so it needn't exist yet.</param>
    /// <returns>A warning for a publish the CMS defers; null when the rule is met or doesn't apply.</returns>
    /// <exception cref="ContentValidationException">A publish now, before the master branch was ever published.</exception>
    private async Task<string?> MasterFirstAsync(Target target, LanguageBranch? language, WriteOperation op, bool dryRun, CancellationToken cancellationToken, bool creatingBranch = false)
    {
        if (language is null || !(op.Publishes || op.RequestApproval) || (dryRun && op.MasterPublishedBy is not null)
            || language.Id == target.Header.MasterLanguageId || target.Header.MasterPublished)
        {
            return null;
        }
        // Headers are cached for the session: an earlier step of a plan may have published the master branch since.
        var header = await ContentHeaderReader.ByIdAsync(session.Db, target.Id, cancellationToken) ?? target.Header;
        if (header.MasterPublished || (!creatingBranch && !header.Languages.ContainsKey(language.Id)))
        {
            return null;
        }
        // With a review request, content without an approval sequence is published as usual.
        var deferred = op.PublishAt is not null
            || (op.RequestApproval && (!op.Publishes || await ApprovalReader.ResolveAsync(session.Db, session.Model, header, cancellationToken) is not null));
        var master = session.Model.Language(header.MasterLanguageId)?.Code ?? header.MasterLanguageId.ToString(CultureInfo.InvariantCulture);
        return MasterLanguageRule.Check(target.Id, $"{target.Id} ('{header.LanguageRow(language.Id)?.Name}')", language.Code, master, op, deferred, dryRun);
    }

    private static WriteOutcome WithWarning(WriteOutcome outcome, string? warning) =>
        warning is null ? outcome : outcome with { Warnings = [.. outcome.Warnings, warning] };

    /// <summary>The latest version of <paramref name="language"/>, reported as a step that changed nothing.</summary>
    private async Task<WriteOutcome> LatestUnchangedAsync(Target target, LanguageBranch language, bool dryRun, string message, CancellationToken cancellationToken)
    {
        var version = await VersionReader.LatestAsync(session.Db, session.Model, target.Id, language.Id, cancellationToken)
            ?? throw new NotFoundException($"Content {target.Id} has no version in {language.Code}.", $"List them with `opticli versions {target.Id}`.");
        return Unchanged(target, version, dryRun, message);
    }

    private WriteOutcome Unchanged(Target target, VersionInfo version, bool dryRun, string message)
    {
        var current = session.Identities.Describe(target.Header, session.Model.Language(version.LanguageId), version.Id, version.StatusValue, version.Name);
        return new WriteOutcome(
            new WriteOutput(WriteOutput.Id(target.Id), version.Ref, current.Guid, current.Type, current.Name, current.Language, current.Status,
                target.Header.ParentId is { } parentId ? WriteOutput.Id(parentId) : null,
                Saved: false, Published: false, DryRun: dryRun, Valid: true, BaseVersion: version.Ref, Changes: [], Validation: null),
            DbSource, null, [message]);
    }

    private async Task<WriteOutcome> UnpublishAsync(UnpublishOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await EditableAsync(op.Ref, cancellationToken);
        var language = LanguageFor(op.Lang, target);
        var result = await PostAsync<WriteResult>(AgentRoutes.Unpublish(target.ContentRef), new UnpublishRequest { Lang = language?.Code, DryRun = dryRun }, cancellationToken);
        var output = WriteOutput.From(result);
        List<string> warnings = dryRun
            ? [$"It would be offline from the moment it runs: a copy of {output.BaseVersion} that stops publishing then is published. Drafts stay as they are."]
            : [$"Offline now: {output.Version}, a copy of {output.PreviouslyPublished} that stops publishing now, is published. Drafts stay as they are."];
        return new WriteOutcome(output, AgentSource, null, warnings);
    }

    private async Task<WriteOutcome> DiscardAsync(DiscardOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await EditableAsync(op.Ref, cancellationToken);
        if (op.Version is { } given && target.Version is { } inRef && given != inRef)
        {
            throw new UsageException($"The ref names version {inRef} but --version says {given}.");
        }
        var request = new DiscardRequest { Version = op.Version ?? target.Version, Lang = LanguageFor(op.Lang, target)?.Code, IncludeDraft = op.IncludeDraft, DryRun = dryRun };
        var result = await PublishingPostAsync(AgentRoutes.Discard(target.ContentRef), request, r => r with { IncludeDraft = true }, cancellationToken,
            "Discard these changes for good?", "Not discarded, as answered; nothing was changed.");
        var output = WriteOutput.From(result);
        List<string> warnings = dryRun
            ? [$"Discarding can't be undone: {output.Version} would be deleted for good; changes shows what it holds compared with {output.BaseVersion}."]
            : [];
        return new WriteOutcome(output, AgentSource, null, warnings);
    }

    private async Task<WriteOutcome> MoveAsync(MoveOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await MovableAsync(await EditableAsync(op.Ref, cancellationToken), cancellationToken);
        var destination = await EditableAsync(op.To, cancellationToken, "--to");
        if (destination.Id == target.Id || destination.Header.AncestorIds.Contains(target.Id))
        {
            throw new UsageException($"Can't move {target.Id} below itself.");
        }
        var descendants = await DescendantCountAsync(target.Header, cancellationToken);
        // A dry run asks the site too: whether the type may go below the new parent is the CMS's answer.
        MoveResult result;
        try
        {
            result = await PostAsync<MoveResult>(AgentRoutes.Move(target.ContentRef), new MoveRequest { Parent = destination.ContentRef, DryRun = dryRun }, cancellationToken);
        }
        catch (ContentValidationException ex)
        {
            throw new ContentValidationException(ex.Message,
                "Move it below a parent whose type allows it (the parent type's [AvailableContentTypes], or its settings in admin mode); nothing was moved.")
            { Details = ex.Details };
        }
        return new WriteOutcome(Moved(result, descendants, recycleBin: null, dryRun), AgentSource, null, []);
    }

    private async Task<WriteOutcome> DeleteAsync(DeleteOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await MovableAsync(await EditableAsync(op.Ref, cancellationToken), cancellationToken);
        if (target.Header.Deleted)
        {
            if (updateExisting)
            {
                return new WriteOutcome(DryMove(target, null, 0, recycleBin: true) with { DryRun = dryRun }, DbSource, null,
                    [$"Content {target.Id} is already in the recycle bin; nothing to do."]);
            }
            throw new ConflictException($"Content {target.Id} is already in the recycle bin.");
        }
        var descendants = await DescendantCountAsync(target.Header, cancellationToken);
        var warnings = await AssetFolderWarningAsync(target.Header, cancellationToken);
        var references = await IncomingReferenceReader.FindAsync(session, target.Header, cancellationToken);
        if (references.Count > 0)
        {
            warnings.Add($"referenced: {references.Describe()}; they would point into the recycle bin (broken links, missing blocks). "
                + (op.IgnoreReferences ? "--ignore-references deletes it all the same." : "The real run stops unless --ignore-references (ask the user first)."));
        }
        if (dryRun)
        {
            return new WriteOutcome(DryMove(target, null, descendants, recycleBin: true).WithReferences(references), DbSource, null, warnings);
        }
        if (references.Count > 0 && !op.IgnoreReferences)
        {
            var message = $"Content {target.Id}{(descendants > 0 ? $" or its {descendants} descendant(s)" : "")} is referenced: {references.Describe()}. Deleting it leaves them pointing into the recycle bin.";
            if (confirmReferences is null)
            {
                throw Referenced(message, references);
            }
            if (!confirmReferences(message, references))
            {
                throw new ConflictException("Not deleted, as answered; nothing was changed.", ReferencedHint)
                {
                    Details = new ReferencedDetails(IncomingReferences.Reason, references.References, references.Count),
                };
            }
        }
        var agent = await AgentAsync(cancellationToken);
        var result = await agent.SendAsync<MoveResult>(HttpMethod.Delete, AgentRoutes.Delete(target.ContentRef), null, cancellationToken);
        return new WriteOutcome(Moved(result, descendants, recycleBin: true, dryRun: false).WithReferences(references), AgentSource, null, warnings);
    }

    /// <summary>
    /// Moves content out of the recycle bin through the site, which finds the parent the CMS stored when it was deleted
    /// (<c>--to</c> overrides it). What can be told from the database is checked first: that the item is in the recycle bin,
    /// and is what was deleted (not something below it). A dry run asks the site too, which knows the stored parent and
    /// which types may go below it.
    /// </summary>
    private async Task<WriteOutcome> RestoreAsync(RestoreOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        Target target;
        try
        {
            target = await EditableAsync(op.Ref, cancellationToken);
        }
        catch (NotFoundException ex) when (ContentRefParser.TryParse(op.Ref, out var parsed, out _) && parsed.Kind == ContentRefKind.Url)
        {
            throw new NotFoundException(ex.Message, "Content in the recycle bin has no URL: give its id or GUID (`opticli trash` lists them).");
        }
        // Read again rather than from the session's cache: an earlier step of a plan may have deleted it.
        var header = await ContentHeaderReader.ByIdAsync(session.Db, target.Id, cancellationToken) ?? target.Header;
        if (!header.Deleted)
        {
            if (updateExisting)
            {
                var identity = session.Identities.Describe(header, null);
                return new WriteOutcome(
                    new RestoreOutput(WriteOutput.Id(target.Id), identity.Guid, identity.Type, identity.Name, identity.Language, identity.Status,
                        header.ParentId is { } parent ? WriteOutput.Id(parent) : "", null, "unchanged", null, Restored: false, DryRun: dryRun, Descendants: 0),
                    DbSource, null, [$"Content {target.Id} is not in the recycle bin; nothing to do."]);
            }
            throw new ConflictException($"Content {target.Id} is not in the recycle bin.", "`opticli trash` lists what is.");
        }
        await session.Identities.LoadAsync(header.AncestorIds, [], cancellationToken);
        var deletedRoot = header.AncestorIds.SkipWhile(id => session.Identities.Header(id) is not { } h || session.Model.TypeName(h.TypeId) != TrashReader.RecycleBinType).Skip(1).FirstOrDefault();
        if (deletedRoot != 0)
        {
            var name = session.Identities.ById(deletedRoot, null).Name;
            throw new UsageException(
                $"Content {target.Id} is in the recycle bin because {deletedRoot} ('{name}') above it was deleted.",
                $"Restore {deletedRoot}, which brings {target.Id} back with it (`opticli restore {deletedRoot}`); then move {target.Id} if it should go elsewhere.");
        }
        string? to = null;
        if (op.To is not null)
        {
            var destination = await EditableAsync(op.To, cancellationToken, "--to");
            if (destination.Id == target.Id || destination.Header.AncestorIds.Contains(target.Id))
            {
                throw new UsageException($"Can't restore {target.Id} below itself.");
            }
            to = destination.ContentRef;
        }
        var descendants = await DescendantCountAsync(header, cancellationToken);
        RestoreResult result;
        try
        {
            result = await PostAsync<RestoreResult>(AgentRoutes.Restore(target.ContentRef), new RestoreRequest { Parent = to, DryRun = dryRun }, cancellationToken);
        }
        catch (NotFoundException ex) when (ex.Message.StartsWith("No agent route", StringComparison.Ordinal))
        {
            throw new NotFoundException("The site's agent is older than this opticli and can't restore content.", AgentErrors.OutOfDateHint);
        }
        var content = result.Content;
        var output = new RestoreOutput(WriteOutput.Id(content.Id), content.Guid, content.Type, content.Name, content.Language, content.Status,
            result.Parent, result.PreviousParent, op.To is null ? "originalParent" : "to", result.StoredParent, result.Restored, result.DryRun, descendants);
        var warnings = new List<string>();
        if (content.Status == "published")
        {
            warnings.Add(RestoredLiveWarning(WriteOutput.Id(content.Id), session.Model.Kind(header.TypeId).IsPage(), result.Parent, dryRun));
        }
        if (op.To is not null && result.StoredParent is { } stored && stored != result.Parent)
        {
            warnings.Add(dryRun
                ? $"It would be restored below {result.Parent} as --to says, not below {stored}, where it was before it was deleted."
                : $"Restored below {result.Parent} as --to says, not below {stored}, where it was before it was deleted.");
        }
        return new WriteOutcome(output, AgentSource, null, warnings);
    }

    /// <summary>
    /// What a restore of published content means: a page is live again at its URL; blocks, media and folders have no URL
    /// of their own, so they are only published again (where something uses them, it shows them again).
    /// </summary>
    internal static string RestoredLiveWarning(string id, bool page, string parent, bool dryRun) => (page, dryRun) switch
    {
        (true, true) => $"{id} has a published version: once restored it is live again, at its URL below {parent}.",
        (true, false) => $"{id} has a published version, so it is live again, at its URL below {parent}.",
        (false, true) => $"{id} has a published version: once restored it is published again, and shows wherever it is used.",
        (false, false) => $"{id} has a published version, so it is published again, and shows wherever it is used.",
    };

    private async Task<WriteOutcome> AccessAsync(AccessOperation op, bool dryRun, CancellationToken cancellationToken)
    {
        var target = await EditableAsync(op.Ref, cancellationToken);
        var grants = op.Grant ?? new Dictionary<string, string>();
        var userGrants = op.GrantUsers ?? new Dictionary<string, string>();
        foreach (var (name, levels) in grants.Concat(userGrants))
        {
            if (!AccessLevels.TryParse(levels, out _, out var error))
            {
                throw new UsageException($"{name}: {error}");
            }
        }
        var changes = grants.Count + userGrants.Count + (op.Revoke?.Count ?? 0) > 0;
        if (op.Inherit && (changes || op.BreakInheritance))
        {
            throw new UsageException("--inherit drops the item's own entries; it can't be combined with --grant, --user, --revoke or --break-inheritance.");
        }
        if (!changes && !op.Inherit && !op.BreakInheritance)
        {
            throw new UsageException("Nothing to change.", "Give --grant Role=Levels, --user Name=Levels, --revoke Name, --break-inheritance or --inherit.");
        }

        var request = new AccessRequest
        {
            Grant = grants.Count > 0 ? grants : null,
            GrantUsers = userGrants.Count > 0 ? userGrants : null,
            Revoke = op.Revoke is { Count: > 0 } revoke ? revoke : null,
            BreakInheritance = op.BreakInheritance,
            Inherit = op.Inherit,
            AllowUnknownRole = op.AllowUnknownRole,
            DryRun = dryRun,
        };
        var result = await PostAsync<AccessResult>(AgentRoutes.Access(target.ContentRef), request, cancellationToken);
        var output = new AccessOutput(WriteOutput.Id(result.Content.Id), result.Content.Guid, result.Content.Type, result.Content.Name,
            result.Saved, result.DryRun, result.Before, result.After);
        var warnings = (result.Validation ?? []).Select(v => v.Message).ToList();
        if (!dryRun && !result.Saved)
        {
            warnings.Add("The access rights were already like that, so nothing was saved.");
        }
        return new WriteOutcome(output, AgentSource, null, warnings);
    }

    /// <summary>
    /// The same rule the agent enforces, checked up front so a dry run gives the same answer: the root, the
    /// recycle bin, site start pages and asset roots, and anything that contains one, are never moved or deleted.
    /// </summary>
    private async Task<Target> MovableAsync(Target target, CancellationToken cancellationToken)
    {
        var sites = session.Model.Sites;
        var protectedIds = sites.All.SelectMany(site => new[] { SiteMap.StartPageId(site), SiteMap.AssetsRootId(site) })
            .Append(sites.GlobalAssetsRoot)
            .Append(sites.ContentAssetsRoot)
            .OfType<int>()
            .Distinct()
            .ToList();
        var header = target.Header;
        if (header.ParentId is null || session.Model.TypeName(header.TypeId) == RecycleBinType || protectedIds.Contains(header.Id))
        {
            throw new RefusedException($"Content {header.Id} is a site root, start page, asset root or the recycle bin; opticli won't move or delete it.");
        }
        var headers = await ContentHeaderReader.ByIdsAsync(session.Db, protectedIds, cancellationToken);
        if (ContainedProtected(header.Id, protectedIds.Select(id => headers.GetValueOrDefault(id))) is { } below)
        {
            throw new RefusedException($"Content {header.Id} contains {below}, a site root, start page or asset root; opticli won't move or delete it.");
        }
        return target;
    }

    /// <returns>The first of <paramref name="protectedContent"/> that has <paramref name="id"/> among its ancestors; null for none.</returns>
    public static int? ContainedProtected(int id, IEnumerable<ContentHeader?> protectedContent) =>
        protectedContent.OfType<ContentHeader>().FirstOrDefault(p => p.AncestorIds.Contains(id))?.Id;

    private MoveOutput DryMove(Target target, string? parent, int descendants, bool? recycleBin)
    {
        var identity = session.Identities.Describe(target.Header, null);
        return new MoveOutput(
            WriteOutput.Id(target.Id), identity.Guid, identity.Type, identity.Name, identity.Language, identity.Status,
            parent, target.Header.ParentId is { } previous ? WriteOutput.Id(previous) : null,
            Moved: false, DryRun: true, descendants, recycleBin);
    }

    private static MoveOutput Moved(MoveResult result, int descendants, bool? recycleBin, bool dryRun) => new(
        WriteOutput.Id(result.Content.Id), result.Content.Guid, result.Content.Type, result.Content.Name, result.Content.Language, result.Content.Status,
        result.Parent, result.PreviousParent, Moved: !dryRun, DryRun: dryRun, descendants, recycleBin);

    private static WriteOutcome Outcome(WriteOutput output, int? createdId)
    {
        var warnings = new List<string>();
        if (!output.DryRun && !output.Saved)
        {
            warnings.Add("Nothing changed, so no new version was saved.");
        }
        if (output.SiteError is { } siteError)
        {
            warnings.Add($"{output.Version ?? output.Ref} was saved, but the site's own code failed after saving it: {siteError}. The save stands; the site's log has the details (`opticli serve --logs`).");
        }
        return new WriteOutcome(output, AgentSource, createdId, warnings);
    }

    /// <summary>
    /// The version the change must be based on: <paramref name="explicitVersion"/>, else the ref's own
    /// version, else the latest in the language, read now. The agent answers 409 if a newer one exists. With
    /// <c>--from</c> (whose ref names no version), the change is based on that version and this is only checked.
    /// </summary>
    /// <param name="variation">CMS 13: the content variation changed, whose newest version is the latest (none for a variation that has no version yet).</param>
    private async Task<int?> BaseVersionAsync(Target target, LanguageBranch? language, int? explicitVersion, bool force, CancellationToken cancellationToken, string? variation = null)
    {
        if (force)
        {
            return null;
        }
        if ((explicitVersion ?? target.Version) is { } version)
        {
            return version;
        }
        var branch = (language ?? MasterLanguage(target)).Id;
        var latest = variation is { } key
            ? await VersionReader.VariationAsync(session.Db, session.Model, target.Id, branch, key.Trim(), latest: true, cancellationToken)
            : await VersionReader.LatestAsync(session.Db, session.Model, target.Id, branch, cancellationToken);
        return latest?.Id;
    }

    private LanguageBranch MasterLanguage(Target target) =>
        session.Model.Language(target.Header.MasterLanguageId)
        ?? throw new NotFoundException($"Content {target.Id} has no master language branch.");

    /// <summary><c>--lang</c>, else the language a URL ref selected, else null (the agent uses the master language).</summary>
    private LanguageBranch? LanguageFor(string? code, Target target) => session.Language(code) ?? target.UrlLanguage;

    /// <summary>
    /// The CMS leaves an item's "For this page" folder where it is when the item goes to the recycle bin (it is removed
    /// with the item when the bin is emptied, and is still there if the item is restored). Say so when it has content.
    /// </summary>
    private async Task<List<string>> AssetFolderWarningAsync(ContentHeader header, CancellationToken cancellationToken)
    {
        var prefix = $"{(header.ContentPath.Length == 0 ? "." : header.ContentPath)}{WriteOutput.Id(header.Id)}.%";
        var rows = await session.Db.QueryAsync("""
            SELECT f.pkID, (SELECT COUNT(*) FROM tblContent c WHERE c.fkParentID = f.pkID AND c.Deleted = 0) AS Items
            FROM tblContent f
            JOIN tblContent o ON o.ContentGUID = f.ContentOwnerID
            WHERE f.Deleted = 0 AND (o.pkID = @id OR o.ContentPath LIKE @prefix)
            """, r => (Folder: r.GetInt32(0), Items: r.GetInt32(1)), cancellationToken,
            new SqlParameter("@id", header.Id), new SqlParameter("@prefix", prefix));
        var used = rows.Where(r => r.Items > 0).ToList();
        if (used.Count == 0)
        {
            return [];
        }
        var folders = used.Count == 1
            ? $"Its \"For this page\" folder ({used[0].Folder}, {used[0].Items} item(s)) stays"
            : $"The \"For this page\" folders of {header.Id} and its descendants ({used.Count} folders, {used.Sum(r => r.Items)} items: {string.Join(", ", used.Select(r => r.Folder))}) stay";
        return [$"{folders} where they are, as in the CMS: they are removed with their pages when the recycle bin is emptied, and are still there if {header.Id} is restored. `opticli delete <folder>` moves one to the recycle bin too."];
    }

    private async Task<int> DescendantCountAsync(ContentHeader header, CancellationToken cancellationToken)
    {
        var prefix = $"{(header.ContentPath.Length == 0 ? "." : header.ContentPath)}{WriteOutput.Id(header.Id)}.%";
        var rows = await session.Db.QueryAsync("SELECT COUNT(*) AS N FROM tblContent WHERE ContentPath LIKE @prefix",
            r => r.GetInt32(0), cancellationToken, new SqlParameter("@prefix", prefix));
        return rows[0];
    }

    /// <summary>
    /// Posts a write that may publish. When the agent answers that the publish would also put someone else's unpublished
    /// changes live, <c>confirmDraft</c> asks (and the write is sent again, confirmed); otherwise that is a conflict
    /// whose hint names the option that confirms.
    /// </summary>
    /// <param name="question">What the prompt asks, and the hint says when the answer is no.</param>
    private async Task<WriteResult> PublishingPostAsync<TRequest>(string route, TRequest request, Func<TRequest, TRequest> confirmed, CancellationToken cancellationToken,
        string question = "Publish these changes too?", string declined = "Not published, as answered; nothing was saved.")
        where TRequest : notnull
    {
        try
        {
            return await PostAsync<WriteResult>(route, request, cancellationToken);
        }
        catch (ConflictException ex) when (ex.Details is AgentErrorDetails { Draft: { } draft })
        {
            var discarding = request is DiscardRequest;
            if (confirmDraft is null)
            {
                throw new ConflictException(ex.Message, discarding ? DiscardDraftHint : PendingDraftHint) { Details = ex.Details };
            }
            if (!confirmDraft(ex.Message, draft, question))
            {
                throw new ConflictException(declined, discarding
                    ? "The version stays as it is."
                    : "Without --publish the change is saved as a draft and nothing goes live; `opticli publish <ref> --version <id>` publishes one version as it is.")
                { Details = ex.Details };
            }
            return await PostAsync<WriteResult>(route, confirmed(request), cancellationToken);
        }
    }

    public const string ReferencedHint =
        "details.references lists what references it (`opticli where-used <ref>` shows more). Ask the user whether to delete it anyway, and only if so run again with --ignore-references (in a plan: \"ignoreReferences\": true on the step); or first remove the references (`opticli area <ref> <Prop> remove ref:<id>`, `opticli set`).";

    /// <summary>The conflict a delete of referenced content fails with.</summary>
    public static ConflictException Referenced(string message, IncomingReferences references) => new(message, ReferencedHint)
    {
        Details = new ReferencedDetails(IncomingReferences.Reason, references.References, references.Count),
    };

    public const string DiscardDraftHint =
        "details.draft shows the version, who saved it and what it changed. Discarding deletes it for good: ask the user, and only if so run again with --include-draft (in a plan: \"includeDraft\": true on the step).";

    /// <summary>A plan step whose dry run found someone else's unpublished changes, without <c>"includeDraft": true</c>.</summary>
    public static ConflictException UnconfirmedDraft(PendingDraft draft, WriteOperation operation) => operation is DiscardOperation
        ? new($"Discarding would delete {draft.Describe()} for good.", DiscardDraftHint)
        {
            Details = new AgentErrorDetails(null, null) { Reason = PendingDraft.Reason, Draft = draft },
        }
        : new($"Publishing would also put live {draft.Describe()}.", PendingDraftHint)
    {
        Details = new AgentErrorDetails(null, null) { Reason = PendingDraft.Reason, Draft = draft },
    };

    private async Task<T> PostAsync<T>(string route, object body, CancellationToken cancellationToken)
    {
        var agent = await AgentAsync(cancellationToken);
        try
        {
            return await agent.SendAsync<T>(HttpMethod.Post, route, body, cancellationToken);
        }
        catch (OptiCliException ex) when (ApprovalRules.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    private async Task<AgentClient> AgentAsync(CancellationToken cancellationToken) => _agent ??= await connect(cancellationToken);

    /// <summary>
    /// Before the first write that isn't a dry run: against a shared database whose content model differs from the
    /// build's, the user must confirm the differences, by their fingerprint (<c>--accept-drift</c>) or at the prompt.
    /// The site agent enforces the same rule; checking here first stops before anything else is sent, and lets a plan ask
    /// once before its first step. Against a local database there is nothing to compare, so the agent isn't asked (an
    /// agent from an older opticli couldn't answer).
    /// </summary>
    /// <exception cref="DriftException">Not confirmed; details is the drift report.</exception>
    /// <exception cref="RefusedException">Shared database, and the agent is too old to compare.</exception>
    public async Task RequireDriftAcceptedAsync(CancellationToken cancellationToken)
    {
        if (_driftAccepted || !(sharedDatabase ?? !session.Db.ConnectionString.IsLocal))
        {
            return;
        }
        var agent = await AgentAsync(cancellationToken);
        var report = await agent.DriftAsync(cancellationToken);
        if (report.Fingerprint is { } fingerprint)
        {
            var given = acceptDrift?.Trim();
            if (!string.Equals(given, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                if (confirmDrift is null || (given is not null && given.Length > 0))
                {
                    throw Drifted(report, given);
                }
                if (!confirmDrift(report))
                {
                    throw new DriftException("Not written, as answered; nothing was saved.", DriftText.Advice(report.Ahead));
                }
            }
            agent.AcceptDrift(fingerprint);
        }
        _driftAccepted = true;
    }

    /// <summary>The error a write fails with while the build and the shared database differ.</summary>
    /// <param name="accepted">A fingerprint the caller gave that no longer matches.</param>
    public static DriftException Drifted(DriftReport report, string? accepted)
    {
        var changed = string.IsNullOrEmpty(accepted)
            ? ""
            : $" --accept-drift {accepted} no longer matches: the differences changed since, and are now {report.Fingerprint}.";
        return new DriftException(
            $"This build and the shared database differ: {report.Describe()}. A write would run this build's code (models, validators, event handlers) against content the deployed site serves.{changed}",
            AgentErrors.DriftHint)
        {
            Details = report,
        };
    }

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
        if (parsed.Kind == ContentRefKind.Provider)
        {
            return await ContentHeaderReader.ProviderGuidAsync(session.Db, parsed.Id, parsed.Provider!, cancellationToken) is { } mapped
                ? new Target(0, null, mapped.ToString("D"), null, null)
                : throw ContentLocator.ProviderNotFound(parsed);
        }
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
