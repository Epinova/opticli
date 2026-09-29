using System.Globalization;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Content;

public enum VersionKind
{
    /// <summary>The primary version: published, or the latest draft of a branch that was never published.</summary>
    Published,

    /// <summary>The newest saved version, published or not.</summary>
    Latest,

    /// <summary>One version by id.</summary>
    Specific,
}

/// <summary><c>--version published|latest|&lt;id&gt;</c>.</summary>
public sealed record VersionSelector(VersionKind Kind, int? Id = null)
{
    public static readonly VersionSelector Published = new(VersionKind.Published);

    /// <exception cref="UsageException">Not one of the accepted forms.</exception>
    public static VersionSelector Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "published" => Published,
        "latest" => new VersionSelector(VersionKind.Latest),
        var text when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 => new VersionSelector(VersionKind.Specific, id),
        _ => throw new UsageException($"Invalid --version '{value}'.", "Use published, latest or a version id (see `opticli versions <ref>`)."),
    };
}
