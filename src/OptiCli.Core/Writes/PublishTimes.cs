using System.Globalization;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Writes;

/// <summary>Times for <c>--publish-at</c>: ISO 8601, in UTC unless they carry an offset.</summary>
public static class PublishTimes
{
    public const string Syntax = "an ISO 8601 date and time, UTC unless it has an offset: 2025-03-01T08:00Z or 2025-03-01T09:00+01:00";

    public static bool TryParse(string? text, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at);

    /// <exception cref="UsageException">Not a time, or not in the future.</exception>
    public static DateTimeOffset Parse(string text, string option, DateTimeOffset now)
    {
        if (!TryParse(text, out var at))
        {
            throw new UsageException($"{option} '{text}' is not a time.", $"Give {Syntax}.");
        }
        return Future(at, option, now);
    }

    /// <exception cref="UsageException"><paramref name="at"/> isn't after <paramref name="now"/>.</exception>
    public static DateTimeOffset Future(DateTimeOffset at, string what, DateTimeOffset now) => at > now
        ? at
        : throw new UsageException($"{what} ({at.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)}) isn't in the future.", "Publish it now with --publish instead, or give a later time.");
}
