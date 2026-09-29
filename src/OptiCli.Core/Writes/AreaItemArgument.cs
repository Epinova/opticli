using System.Globalization;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Writes;

/// <summary>How <c>area remove|move</c> name an item: a plain number is its position, <c>ref:123</c> (or any non-numeric ref) the content it shows.</summary>
public static class AreaItemArgument
{
    public const string RefPrefix = "ref:";

    public static (int? Index, string? Ref) Parse(string value)
    {
        if (value.StartsWith(RefPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var reference = value[RefPrefix.Length..];
            return reference.Length > 0 ? (null, reference) : throw new UsageException("'ref:' needs a content ref after it, e.g. ref:123.");
        }
        return value.Length > 0 && value.All(char.IsAsciiDigit) ? (Position(value), null) : (null, value);
    }

    /// <exception cref="UsageException">Not a non-negative integer.</exception>
    public static int Position(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            ? position
            : throw new UsageException($"'{value}' is not a zero-based position.");
}
