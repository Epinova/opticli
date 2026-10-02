using System.Globalization;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// The warning for a change based on an older version (<c>--from</c>) when newer versions exist: their changes aren't in
/// it, they stay as they are, and which version edit mode opens now.
/// </summary>
public static class LeftOutVersions
{
    /// <summary>At most this many versions are named; the rest are counted.</summary>
    public const int Shown = 5;

    /// <param name="from">What the change was based on, when <c>--from</c> chose it.</param>
    /// <param name="publishes">The change is published now (for a dry run: would be).</param>
    /// <returns>Null when the change leaves nothing out (or nothing was saved).</returns>
    public static string? Warning(WriteOutput output, FromVersion? from, bool publishes)
    {
        if (output.LeftOut is not { Count: > 0 } leftOut)
        {
            return null;
        }
        var one = leftOut.Count == 1;
        var basis = $"{output.BaseVersion}{(from?.IsPublished == true ? " (the published version)" : "")}";
        var versions = $"the newer version{(one ? "" : "s")} {Describe(leftOut)}";
        var stay = one ? "it stays as it is" : "they stay as they are";
        var later = $"publishing {(one ? "it" : "one of them")} later would put its changes live without this one";
        var primary = leftOut.FirstOrDefault(v => v.Primary)?.Version;
        var instead = primary is null ? "" : $" instead of {primary} (still in its version list)";
        if (output.DryRun)
        {
            return $"Based on {basis}, this would leave out {versions}: {(one ? "it would stay as it is" : "they would stay as they are")}, without this change, "
                + (publishes ? $"and {later}." : $"and edit mode would open the new version{instead}.");
        }
        return output.Published
            ? $"{output.Version} is published, based on {basis}, so it leaves out {versions}: {stay}, without this change, and {later}. Edit mode now opens the published version."
            : $"{output.Version} is based on {basis}, so it leaves out {versions}: {stay}, without this change. Edit mode now opens {output.Version}{instead}.";
    }

    /// <summary><c>123_14 (checkedOut, saved by editor@example.com 2025-01-27 09:00:00Z)</c>, newest first.</summary>
    public static string Describe(IReadOnlyList<LeftOutVersion> versions)
    {
        var named = versions.Take(Shown).Select(v =>
        {
            var who = string.IsNullOrWhiteSpace(v.SavedBy) ? "saved without a user name" : $"saved by {v.SavedBy}";
            return $"{v.Version} ({v.Status}, {who} {v.Saved.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)})";
        });
        var rest = versions.Count > Shown ? $" and {versions.Count - Shown} more" : "";
        return string.Join(", ", named) + rest;
    }
}
