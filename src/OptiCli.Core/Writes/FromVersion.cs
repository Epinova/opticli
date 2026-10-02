using System.Globalization;
using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// What a set or area change is based on instead of the latest version (<c>--from</c>, a plan's <c>from</c>): the
/// published version of the branch, or one version. The concurrency check still compares with the latest version.
/// </summary>
/// <param name="Version">The version id; null for the published version.</param>
/// <param name="ContentId">The content the value names, when given as <c>123_456</c>; it must be the one written.</param>
public sealed record FromVersion(int? Version, int? ContentId = null)
{
    public const string Syntax = "published, or a version (456 or 123_456)";

    public static readonly FromVersion Published = new(Version: null);

    public bool IsPublished => Version is null;

    /// <summary>As the agent takes it (<see cref="DraftRequest.From"/>).</summary>
    public string Request => Version?.ToString(CultureInfo.InvariantCulture) ?? DraftRequest.FromPublished;

    public static bool TryParse(string? text, out FromVersion from)
    {
        from = Published;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        if (value.Equals(DraftRequest.FromPublished, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var parts = value.Split('_');
        if (parts.Length > 2 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0))
        {
            return false;
        }
        from = parts.Length == 2
            ? new FromVersion(int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[0], CultureInfo.InvariantCulture))
            : new FromVersion(int.Parse(parts[0], CultureInfo.InvariantCulture));
        return true;
    }

    /// <exception cref="UsageException">Neither <c>published</c> nor a version.</exception>
    public static FromVersion Parse(string text, string option) => TryParse(text, out var from)
        ? from
        : throw new UsageException($"{option} '{text}' is not a version: give {Syntax}, as `opticli versions` shows them.");

    /// <exception cref="UsageException">The ref names a version too, or the value names another content's version.</exception>
    public void Check(int contentId, int? refVersion)
    {
        if (refVersion is { } pinned)
        {
            throw new UsageException($"The ref names version {pinned} and --from says {this}; give one of them.",
                $"{WriteOutput.VersionRef(contentId, pinned)} bases the change on that version, which must be the latest; --from bases it on any version, and the change still fails with a conflict if a newer one is saved meanwhile.");
        }
        if (ContentId is { } other && other != contentId)
        {
            throw new UsageException($"--from {this} is a version of {other}, not of {contentId}.");
        }
    }

    public override string ToString() => Version is { } version
        ? ContentId is { } id ? WriteOutput.VersionRef(id, version) : version.ToString(CultureInfo.InvariantCulture)
        : DraftRequest.FromPublished;
}
