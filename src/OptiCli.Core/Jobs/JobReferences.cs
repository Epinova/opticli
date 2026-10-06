using System.Globalization;
using OptiCli.Core.Errors;
using OptiCli.Core.SourceScan;
using OptiCli.Core.Text;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Jobs;

/// <summary>Finds the job a <c>&lt;job&gt;</c> argument names.</summary>
public static class JobReferences
{
    public const string Syntax = "the job's id (GUID), its name, its class (full or short name), or a part of its name that only it has";

    /// <summary>
    /// In order: the id; the exact name (case-insensitive); the class, by full name (<c>EPiServer.Util.BlobCleanupJob</c>)
    /// or short name (<c>BlobCleanupJob</c>); a part of the name only one job has.
    /// </summary>
    /// <param name="sources">The jobs in the site's code, to say that one the database lacks isn't registered yet.</param>
    /// <exception cref="UsageException">Several jobs match.</exception>
    /// <exception cref="NotFoundException">None does.</exception>
    public static JobRow Resolve(string text, IReadOnlyList<JobRow> jobs, IReadOnlyList<ScheduledJobSource>? sources = null)
    {
        var wanted = text.Trim();
        if (Guid.TryParse(wanted, out var id) && jobs.FirstOrDefault(j => j.Id == id) is { } byId)
        {
            return byId;
        }
        IEnumerable<Func<JobRow, bool>> steps =
        [
            j => string.Equals(j.Name, wanted, StringComparison.OrdinalIgnoreCase),
            j => string.Equals(j.TypeName, wanted, StringComparison.Ordinal),
            j => string.Equals(ShortName(j.TypeName), wanted, StringComparison.OrdinalIgnoreCase),
            j => j.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase),
        ];
        foreach (var step in steps)
        {
            var matches = jobs.Where(step).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
            if (matches.Count > 1)
            {
                throw new UsageException(
                    $"'{wanted}' matches {matches.Count} jobs: {string.Join(", ", matches.Select(m => $"'{m.Name}' ({m.TypeName ?? "no class"}, {m.Id})"))}.",
                    "Give the full name in quotes, the class, or the id (`opticli jobs --all` lists them).");
            }
        }

        if (sources?.FirstOrDefault(s => (s.Guid is { } guid && guid.ToString("D").Equals(wanted, StringComparison.OrdinalIgnoreCase))
                || string.Equals(s.DisplayName, wanted, StringComparison.OrdinalIgnoreCase)
                || s.TypeName == wanted
                || string.Equals(ShortName(s.TypeName), wanted, StringComparison.OrdinalIgnoreCase)) is { } unregistered)
        {
            throw new NotFoundException(
                $"'{unregistered.DisplayName ?? unregistered.TypeName}' is in the code ({unregistered.File}:{unregistered.Line}) but not in the database yet.",
                UnregisteredHint);
        }
        throw new NotFoundException(
            $"No scheduled job is named '{wanted}'.",
            Suggestions.DidYouMean(wanted, jobs.Select(j => j.Name)) ?? "`opticli jobs --all` lists every job.");
    }

    public const string UnregisteredHint = "The site registers its jobs when it starts: run `opticli serve` once (or build and start it with the new code).";

    /// <summary>The class name without its namespace.</summary>
    public static string? ShortName(string? typeName) => typeName is null ? null : typeName[(typeName.LastIndexOfAny(['.', '+']) + 1)..];
}

/// <summary>The times <c>jobs log --since</c> and <c>jobs set --next</c> take.</summary>
public static class JobTimes
{
    public const string SinceSyntax = "a UTC date (yyyy-MM-dd or yyyy-MM-ddTHH:mm:ss; a Z or +01:00 suffix is honoured), or an age: 30m, 12h, 7d, 2w";

    public const string NextSyntax = $"now, or {PublishTimes.Syntax}";

    /// <exception cref="UsageException">Neither a date nor an age.</exception>
    public static DateTime ParseSince(string text, DateTime now)
    {
        var value = text.Trim();
        if (value.Length >= 2 && int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) && amount > 0)
        {
            TimeSpan? age = char.ToLowerInvariant(value[^1]) switch
            {
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                'w' => TimeSpan.FromDays(7 * amount),
                _ => null,
            };
            if (age is { } span)
            {
                return now - span;
            }
        }
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
            : throw new UsageException($"Invalid --since '{text}'.", $"Give {SinceSyntax}.");
    }

    /// <summary><c>now</c>, or a time (UTC unless it has an offset); one that has passed is allowed: the job is then overdue.</summary>
    /// <exception cref="UsageException">Not a time.</exception>
    public static DateTime ParseNext(string text, DateTime now)
    {
        if (string.Equals(text.Trim(), "now", StringComparison.OrdinalIgnoreCase))
        {
            return now;
        }
        return PublishTimes.TryParse(text, out var at)
            ? at.UtcDateTime
            : throw new UsageException($"--next '{text}' is not a time.", $"Give {NextSyntax}.");
    }
}
