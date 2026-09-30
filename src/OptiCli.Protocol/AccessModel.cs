using System.Diagnostics.CodeAnalysis;

namespace OptiCli.Protocol;

/// <summary>Body of <see cref="AgentRoutes.Access"/>: change the access rights (ACL) of one content item.</summary>
/// <remarks>
/// <para>Levels are a comma list of <see cref="AccessLevels.Names"/> (<c>Read,Edit</c>) or <c>FullAccess</c>. A grant
/// sets the entry to exactly those levels, replacing any entry with the same name.</para>
/// <para>An item that inherits its ACL can't be changed without <see cref="BreakInheritance"/>, which first copies the
/// inherited entries onto it (what the edit UI does when "Inherit settings from parent item" is unticked).
/// <see cref="Inherit"/> drops the item's own entries so it inherits again; it can't be combined with changes.
/// Children that inherit follow automatically; nothing is applied to descendants.</para>
/// </remarks>
public sealed record AccessRequest
{
    /// <summary>Role name to levels.</summary>
    public IReadOnlyDictionary<string, string>? Grant { get; init; }

    /// <summary>User name to levels.</summary>
    public IReadOnlyDictionary<string, string>? GrantUsers { get; init; }

    /// <summary>Role or user names whose entries are removed. Applied before the grants.</summary>
    public IReadOnlyList<string>? Revoke { get; init; }

    public bool BreakInheritance { get; init; }

    public bool Inherit { get; init; }

    /// <summary>Accept role names the site doesn't know, e.g. roles an identity provider only creates on first sign-in.</summary>
    public bool AllowUnknownRole { get; init; }

    public bool DryRun { get; init; }
}

/// <summary>Response of <see cref="AgentRoutes.Access"/>.</summary>
public sealed record AccessResult
{
    public required ContentSummary Content { get; init; }

    public required AccessList Before { get; init; }

    /// <summary>For a dry run: what the ACL would be.</summary>
    public required AccessList After { get; init; }

    /// <summary>True when the ACL was written. Access rights aren't versioned, so nothing else records the change.</summary>
    public bool Saved { get; init; }

    public bool DryRun { get; init; }

    /// <summary>Warnings, e.g. a revoke for a name that has no entry.</summary>
    public IReadOnlyList<ValidationIssue>? Validation { get; init; }
}

/// <summary>The effective ACL of an item.</summary>
/// <param name="Inherited">True when the item has no entries of its own and uses <paramref name="From"/>'s.</param>
/// <param name="From">Ref of the item the entries are stored on: the item itself, or the nearest ancestor with its own.</param>
public sealed record AccessList(bool Inherited, string? From, IReadOnlyList<AccessEntry> Entries);

/// <param name="Kind">One of <see cref="AccessKinds"/>.</param>
/// <param name="Levels">Level names (<see cref="AccessLevels.Describe"/>): <c>FullAccess</c>, or the individual levels.</param>
/// <param name="Mask">The stored <c>AccessLevel</c> bits.</param>
public sealed record AccessEntry(string Name, string Kind, IReadOnlyList<string> Levels, int Mask)
{
    /// <summary>A stable order (by name, then kind), so the database read and the CMS's answer compare equal.</summary>
    public static IReadOnlyList<AccessEntry> Sorted(IEnumerable<AccessEntry> entries) =>
        entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Kind, StringComparer.Ordinal).ToList();
}

/// <summary>Entry kinds, from <c>SecurityEntityType</c> (stored as <c>tblContentAccess.IsRole</c>).</summary>
public static class AccessKinds
{
    public const string User = "user";
    public const string Role = "role";
    public const string VisitorGroup = "visitorGroup";

    public static string From(int entityType) => entityType switch
    {
        0 => User,
        1 => Role,
        2 => VisitorGroup,
        _ => $"type{entityType}",
    };
}

/// <summary><c>EPiServer.Security.AccessLevel</c> without referencing the CMS: names, bits, parsing.</summary>
public static class AccessLevels
{
    public const int Read = 1;
    public const int Create = 2;
    public const int Edit = 4;
    public const int Delete = 8;
    public const int Publish = 16;
    public const int Administer = 32;
    public const int FullAccess = 63;

    public const string FullAccessName = "FullAccess";

    /// <summary>The individual levels, lowest first.</summary>
    public static readonly IReadOnlyList<(string Name, int Bit)> Names =
    [
        ("Read", Read), ("Create", Create), ("Edit", Edit), ("Delete", Delete), ("Publish", Publish), ("Administer", Administer),
    ];

    public const string Syntax = "a comma list of Read, Create, Edit, Delete, Publish, Administer, or FullAccess";

    /// <summary>Parses <c>Read,Edit</c> or <c>FullAccess</c>, case-insensitively.</summary>
    public static bool TryParse(string? text, out int mask, [NotNullWhen(false)] out string? error)
    {
        mask = 0;
        error = null;
        var parts = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = $"No access levels given: use {Syntax}. To remove an entry, revoke it.";
            return false;
        }
        foreach (var part in parts)
        {
            if (part.Equals(FullAccessName, StringComparison.OrdinalIgnoreCase))
            {
                mask |= FullAccess;
                continue;
            }
            var level = Names.FirstOrDefault(n => n.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (level.Name is null)
            {
                error = $"'{part}' is not an access level: use {Syntax}.";
                return false;
            }
            mask |= level.Bit;
        }
        return true;
    }

    /// <summary><c>FullAccess</c> when every level is set, else the names of the levels that are.</summary>
    public static IReadOnlyList<string> Describe(int mask) =>
        (mask & FullAccess) == FullAccess
            ? [FullAccessName]
            : Names.Where(n => (mask & n.Bit) != 0).Select(n => n.Name).ToList();

    /// <summary>The form <see cref="TryParse"/> reads back.</summary>
    public static string Format(int mask) => string.Join(',', Describe(mask));
}
