using System.Globalization;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Output;

/// <summary>A page of a list plus the cursor for the next one.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? Next);

public static class Paging
{
    public const int DefaultLimit = 50;

    /// <summary>Keeps <c>limit + 1</c> and <c>offset + limit</c> far from overflowing.</summary>
    public const int MaxLimit = 1_000_000;

    /// <summary>Offset paging over an in-memory list. The cursor is opaque to callers.</summary>
    /// <exception cref="UsageException">Bad limit or cursor.</exception>
    public static Page<T> Apply<T>(IReadOnlyList<T> all, int? limit, string? cursor)
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            throw new UsageException($"--limit must be between 1 and {MaxLimit} (got {size}).");
        }

        var offset = 0;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > all.Count))
        {
            throw new UsageException($"Invalid --cursor '{cursor}'.", "Pass the value of meta.next from the previous page.");
        }

        var items = all.Skip(offset).Take(size).ToList();
        var end = offset + items.Count;
        return new Page<T>(items, end < all.Count ? end.ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>
    /// The offset and size a query should fetch for <c>--limit</c>/<c>--cursor</c>, for lists paged in SQL.
    /// Fetch <c>Limit + 1</c> rows and pass them to <see cref="FromWindow"/>.
    /// </summary>
    /// <exception cref="UsageException">Bad limit or cursor.</exception>
    public static (int Offset, int Limit) Window(int? limit, string? cursor)
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            throw new UsageException($"--limit must be between 1 and {MaxLimit} (got {size}).");
        }
        var offset = 0;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > int.MaxValue - MaxLimit - 1))
        {
            throw new UsageException($"Invalid --cursor '{cursor}'.", "Pass the value of meta.next from the previous page.");
        }
        return (offset, size);
    }

    /// <summary>A page from up to <paramref name="limit"/> + 1 fetched rows; the extra row only signals that more exist.</summary>
    public static Page<T> FromWindow<T>(IReadOnlyList<T> fetched, int offset, int limit) =>
        new(fetched.Take(limit).ToList(), fetched.Count > limit ? (offset + limit).ToString(CultureInfo.InvariantCulture) : null);
}
