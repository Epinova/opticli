using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>How to reverse a write that was saved, as a command where one exists.</summary>
public static class UndoHints
{
    /// <returns>Null when nothing was saved.</returns>
    public static string? For(WriteOperation operation, object output) => (operation, output) switch
    {
        (_, WriteOutput { Restored: true } restored) =>
            $"{restored.Ref} was moved back out of the recycle bin (opticli delete {restored.Ref} returns it there){(restored.Saved ? $"; {Version(restored)}" : ".")}",
        (_, WriteOutput { Saved: false }) or (_, MoveOutput { Moved: false }) or (_, AccessOutput { Saved: false }) => null,
        (AccessOperation, AccessOutput access) => Access(access),
        (CreateOperation or BlockCreateOperation or UploadOperation, WriteOutput { Existing: not true } created) =>
            $"opticli delete {created.Ref} (moves it to the recycle bin)",
        (TranslateOperation, WriteOutput { Existing: not true } branch) =>
            $"Language branch '{branch.Language}' was created ({branch.Version}); opticli can't remove a branch, delete it in the CMS edit UI if unwanted.",
        (PublishOperation, WriteOutput published) =>
            $"{published.Version} is now published; to go back, publish the previously published version: opticli versions {published.Ref}, then opticli publish {published.Ref} --version <id>",
        (_, WriteOutput saved) => Version(saved),
        (DeleteOperation, MoveOutput deleted) =>
            $"opticli move {deleted.Ref} --to {deleted.PreviousParent} (restores it from the recycle bin)",
        (_, MoveOutput moved) =>
            $"opticli move {moved.Ref} --to {moved.PreviousParent}",
        _ => null,
    };

    private static string Version(WriteOutput saved) => saved.Published
        ? $"{saved.Version} was published; to go back, re-publish the version it was based on: opticli publish {saved.Ref} --version {VersionId(saved.BaseVersion)}"
        : $"{saved.Version} is an unpublished draft, so nothing live changed; the version it was based on ({saved.BaseVersion}) is unchanged.";

    /// <summary>
    /// The command that turns <c>after</c> back into <c>before</c>: access rights aren't versioned, so this is the only
    /// record of what they were.
    /// </summary>
    private static string? Access(AccessOutput output)
    {
        var (before, after) = (output.Before, output.After);
        if (before.Inherited)
        {
            return after.Inherited ? null : $"opticli access {output.Ref} --inherit (it inherited from {before.From} before)";
        }

        var parts = new List<string> { $"opticli access {output.Ref}" };
        if (after.Inherited)
        {
            parts.Add("--break-inheritance");
        }
        // Entries only in after are revoked; entries whose levels differ are granted again, which replaces them.
        foreach (var gone in after.Entries.Where(a => !before.Entries.Any(b => Same(a, b))))
        {
            parts.Add($"--revoke {Quote(gone.Name)}");
        }
        var unrestorable = new List<string>();
        foreach (var entry in before.Entries.Where(b => !after.Entries.Any(a => Same(a, b) && a.Mask == b.Mask)))
        {
            switch (entry.Kind)
            {
                case AccessKinds.Role:
                    parts.Add($"--grant {Quote($"{entry.Name}={AccessLevels.Format(entry.Mask)}")}");
                    break;
                case AccessKinds.User:
                    parts.Add($"--user {Quote($"{entry.Name}={AccessLevels.Format(entry.Mask)}")}");
                    break;
                default:
                    unrestorable.Add(entry.Name);
                    break;
            }
        }
        var command = parts.Count > 1 ? string.Join(' ', parts) : null;
        return unrestorable.Count == 0
            ? command
            : $"{command ?? "Nothing opticli can restore"}; restore {string.Join(", ", unrestorable)} (visitor groups) in the CMS edit UI";
    }

    private static bool Same(AccessEntry a, AccessEntry b) =>
        a.Kind == b.Kind && a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase);

    private static string Quote(string value) =>
        value.All(c => char.IsLetterOrDigit(c) || c is '=' or ',' or '_' or '-' or '.' or '@') ? value : $"'{value.Replace("'", "'\\''")}'";

    private static string VersionId(string? versionRef) =>
        versionRef?.Split('_') is [_, var version] ? version : "<id>";
}
