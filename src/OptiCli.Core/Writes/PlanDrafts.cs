using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>A plan operation that saved into the draft an earlier operation made, instead of a new version.</summary>
/// <param name="Version">The draft (<c>id_version</c>).</param>
/// <param name="Operation">The operation that made it, whose undo covers this change too.</param>
public sealed record SavedInto(string Version, int Operation);

/// <summary>
/// The drafts an <c>apply</c> run saved, per content, language branch and (CMS 13) variation, so that a later set, area or
/// composition operation on the same branch saves into the draft (<see cref="SetOperation.IntoVersion"/>) instead of a
/// new version each, as the edit UI keeps saving into one draft. A plan then leaves one draft per content and language.
/// </summary>
/// <remarks>
/// The key comes from what the site reports (content id, version and language of the result). A step that publishes,
/// schedules, sends for review, unpublishes, discards, deletes or removes the branch leaves no draft of the run to save
/// into, so the key is cleared. What is passed is only a candidate: the executor sends it when the database still has it
/// as the branch's latest version, a draft saved by opticli (<see cref="StillOurs"/>), and the agent checks the same again
/// before it saves; otherwise the step saves a new version, as it would without this.
/// </remarks>
internal sealed class PlanDrafts
{
    private readonly Dictionary<Key, Draft> _drafts = [];

    /// <param name="Language">Lower case; null for content that isn't localizable (the site reports no language).</param>
    /// <param name="Variation">CMS 13: the content variation's key, lower case; null for the content itself.</param>
    internal readonly record struct Key(int Content, string? Language, string? Variation)
    {
        public static Key Of(int content, string? language, string? variation) => new(content, Normalized(language), Normalized(variation));

        private static string? Normalized(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    /// <param name="Operation">The plan operation that made the version.</param>
    internal sealed record Draft(int Content, int Version, int Operation)
    {
        public string Ref => WriteOutput.VersionRef(Content, Version);
    }

    /// <summary>
    /// Whether the operation may save into an earlier draft: a set, area or composition that doesn't name what to base it
    /// on (<c>from</c>, <c>baseVersion</c>) and keeps the concurrency check (no <c>force</c>). A ref naming a version is
    /// checked by <see cref="ForAsync"/>.
    /// </summary>
    public static bool MaySaveInto(WriteOperation operation) => operation switch
    {
        SetOperation set => set is { From: null, BaseVersion: null, Force: false },
        AreaEdit area => area is { From: null, BaseVersion: null, Force: false },
        CompositionEdit composition => composition is { From: null, BaseVersion: null, Force: false },
        _ => false,
    };

    public static WriteOperation Into(WriteOperation operation, int version) => operation switch
    {
        SetOperation set => set with { IntoVersion = version },
        AreaEdit area => area with { IntoVersion = version },
        CompositionEdit composition => composition with { IntoVersion = version },
        _ => operation,
    };

    /// <summary>
    /// The draft of this run that <paramref name="operation"/> writes, resolved as the executor resolves it: its
    /// <c>lang</c>, else the language a URL ref selects, else the content's master language.
    /// </summary>
    /// <returns>Null when there is none, or the operation may not save into one (<see cref="MaySaveInto"/>, a ref naming a version).</returns>
    public async Task<Draft?> ForAsync(WriteOperation operation, ContentSession session, CancellationToken cancellationToken)
    {
        if (_drafts.Count == 0 || !MaySaveInto(operation))
        {
            return null;
        }
        var (reference, lang, variation) = operation switch
        {
            SetOperation set => (set.Ref, set.Lang, set.Variation),
            AreaEdit area => (area.Ref, area.Lang, null),
            CompositionEdit composition => (composition.Ref, composition.Lang, composition.Variation),
            _ => (null, null, null),
        };
        if (reference is null || PlanSimulation.PlanId(reference) is not null)
        {
            return null;
        }
        try
        {
            var located = await session.LocateAsync(reference, null, cancellationToken);
            if (located.VersionId is not null || !_drafts.Keys.Any(k => k.Content == located.Id))
            {
                return null;
            }
            var header = await session.HeaderAsync(located.Id, cancellationToken);
            var language = session.Language(lang) ?? located.Url?.Language ?? session.Model.Language(header.MasterLanguageId);
            return Find(Key.Of(located.Id, language?.Code, variation));
        }
        catch (OptiCliException)
        {
            // The operation's own run says what is wrong with its ref.
            return null;
        }
    }

    /// <summary>The draft for the branch; content that isn't localizable has one branch, which the site reports without a language.</summary>
    internal Draft? Find(Key key) =>
        _drafts.TryGetValue(key, out var draft) || _drafts.TryGetValue(key with { Language = null }, out draft) ? draft : null;

    /// <summary>Records what an operation that ran left: the draft it saved (or saved into), or no draft of the run.</summary>
    /// <param name="index">The operation's position in the plan.</param>
    /// <param name="offered">The draft it was given to save into (<see cref="ForAsync"/>), if any.</param>
    /// <returns>
    /// <c>Into</c>: it saved into <paramref name="offered"/>. <c>Note</c>: it was offered one, but the site saved a new version.
    /// </returns>
    public (SavedInto? Into, string? Note) After(WriteOperation operation, int index, object output, Draft? offered)
    {
        Ended = null;
        switch (output)
        {
            case WriteOutput { DryRun: false } written when ContentId(written.Ref) is { } content:
                if (operation is PublishOperation or UnpublishOperation or DiscardOperation)
                {
                    if (VersionId(written.Version) is { } named && Ending(written) is { } ending
                        && _drafts.Values.FirstOrDefault(d => d.Content == content && d.Version == named) is { } ended)
                    {
                        Ended = (ended, ending);
                    }
                    Clear(content, written.Language);
                    return (null, null);
                }
                if (!written.Saved || VersionId(written.Version) is not { } version)
                {
                    // Nothing changed: the draft, if any, is still what it was.
                    return (null, null);
                }
                var key = Key.Of(content, written.Language, VariationOf(operation));
                var into = offered is not null && written.IntoVersion == true && offered.Content == content && offered.Version == version;
                if (IsDraft(written))
                {
                    _drafts[key] = new Draft(content, version, into ? offered!.Operation : index);
                }
                else
                {
                    // Published, scheduled or sent for review: no draft of this run any more.
                    _drafts.Remove(key);
                    if (into && Ending(written) is { } ending)
                    {
                        Ended = (offered!, ending);
                    }
                }
                if (into)
                {
                    return (new SavedInto(offered!.Ref, offered.Operation), null);
                }
                return offered is null
                    ? (null, null)
                    : (null, $"Saved as a new version ({written.Version}), not into {offered.Ref}, the draft operation {offered.Operation} made: that draft was no longer the latest version, or no longer an unpublished draft of opticli's (someone saved or published in between; `opticli versions {content}` lists the versions).");
            case MoveOutput { Moved: true } deleted when operation is DeleteOperation && ContentId(deleted.Ref) is { } content:
                Clear(content, null);
                return (null, null);
            case RemoveLanguageOutput { Removed: true } removed when ContentId(removed.Ref) is { } content:
                Clear(content, removed.Language);
                return (null, null);
            default:
                return (null, null);
        }
    }

    /// <summary>
    /// The run's draft that the last operation passed to <see cref="After"/> published, scheduled, sent for review or
    /// discarded, and how (<c>published it</c>, ...); null when it ended none. The undos of the operations that saved it
    /// then no longer hold ("nothing live changed"): <see cref="Revise"/>.
    /// </summary>
    public (Draft Draft, string How)? Ended { get; private set; }

    /// <summary>
    /// After operation <paramref name="by"/> ended <paramref name="draft"/> (<see cref="Ended"/>): the undos of the
    /// operations before it that saved the draft (the set, area or composition that made it, and those that saved into
    /// it) point to <paramref name="by"/>'s undo, which reverses them all. The undo of a create, block, upload or translate
    /// that made it (delete, remove the branch) still holds, and is kept.
    /// </summary>
    public static void Revise(List<PlanStepResult> results, Draft draft, int by, string how)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (result.Index >= by || result.Status != PlanStepStatus.Saved || result.Result is not WriteOutput { Version: { } version } || version != draft.Ref)
            {
                continue;
            }
            if (result.SavedInto is { } into)
            {
                results[i] = result with { Undo = $"Saved into {draft.Ref}, the draft operation {into.Operation} made; operation {by} then {how} with this change: see operation {by}'s undo." };
            }
            else if (result.Index == draft.Operation && result.Op is "set" or "area" or "composition")
            {
                results[i] = result with { Undo = $"{draft.Ref} was saved as a draft; operation {by} then {how} with the changes saved into it since: see operation {by}'s undo." };
            }
        }
    }

    private static string? Ending(WriteOutput output) => output switch
    {
        { Discarded: true } => "discarded it",
        { ApprovalRequested: true } => "sent it for review",
        { ScheduledFor: not null } => "scheduled it to be published",
        { Published: true } => "published it",
        _ => null,
    };

    /// <summary>
    /// Whether the database still has <paramref name="version"/> as a draft to save into for the branch written: of that
    /// content, language branch and variation, unpublished (<c>checkedOut</c>), and saved by opticli. The caller checks
    /// that it is the latest version.
    /// </summary>
    internal static bool StillOurs(VersionInfo? version, int contentId, int languageId, string? variation) =>
        version is not null
        && version.ContentId == contentId
        && version.LanguageId == languageId
        && string.Equals(version.Variation?.Trim() ?? "", variation?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)
        && version.StatusValue == VersionStatus.CheckedOut
        && string.Equals(version.ChangedBy?.Trim(), AgentProtocol.PrincipalName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A draft the run may save into again: not published, scheduled or sent for review.</summary>
    private static bool IsDraft(WriteOutput output) =>
        output is { Published: false, ScheduledFor: null, ApprovalRequested: not true, Status: "checkedOut" };

    /// <summary>Forgets the content's drafts in a language (null: in every language).</summary>
    private void Clear(int content, string? language)
    {
        var code = Key.Of(content, language, null).Language;
        foreach (var key in _drafts.Keys.Where(k => k.Content == content && (code is null || k.Language is null || k.Language == code)).ToList())
        {
            _drafts.Remove(key);
        }
    }

    private static string? VariationOf(WriteOperation operation) => operation switch
    {
        SetOperation set => set.Variation,
        CompositionEdit composition => composition.Variation,
        _ => null,
    };

    private static int? ContentId(string? reference) =>
        int.TryParse(reference, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;

    private static int? VersionId(string? versionRef) =>
        versionRef?.Split('_') is [_, var version] && int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
}
