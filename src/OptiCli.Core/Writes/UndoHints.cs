namespace OptiCli.Core.Writes;

/// <summary>How to reverse a write that was saved, as a command where one exists.</summary>
public static class UndoHints
{
    /// <returns>Null when nothing was saved.</returns>
    public static string? For(WriteOperation operation, object output) => (operation, output) switch
    {
        (_, WriteOutput { Saved: false }) or (_, MoveOutput { Moved: false }) => null,
        (CreateOperation or BlockCreateOperation, WriteOutput created) =>
            $"opticli delete {created.Ref} (moves it to the recycle bin)",
        (TranslateOperation, WriteOutput branch) =>
            $"Language branch '{branch.Language}' was created ({branch.Version}); opticli can't remove a branch, delete it in the CMS edit UI if unwanted.",
        (PublishOperation, WriteOutput published) =>
            $"{published.Version} is now published; to go back, publish the previously published version: opticli versions {published.Ref}, then opticli publish {published.Ref} --version <id>",
        (_, WriteOutput { Published: true } saved) =>
            $"{saved.Version} was published; to go back, re-publish the version it was based on: opticli publish {saved.Ref} --version {VersionId(saved.BaseVersion)}",
        (_, WriteOutput draft) =>
            $"{draft.Version} is an unpublished draft, so nothing live changed; the version it was based on ({draft.BaseVersion}) is unchanged.",
        (DeleteOperation, MoveOutput deleted) =>
            $"opticli move {deleted.Ref} --to {deleted.PreviousParent} (restores it from the recycle bin)",
        (_, MoveOutput moved) =>
            $"opticli move {moved.Ref} --to {moved.PreviousParent}",
        _ => null,
    };

    private static string VersionId(string? versionRef) =>
        versionRef?.Split('_') is [_, var version] ? version : "<id>";
}
