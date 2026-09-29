using System.Globalization;

namespace OptiCli.Integration;

/// <summary>Environment variables that point the integration tests at a site and size the run.</summary>
internal static class SiteSettings
{
    public const string ProjectVariable = "OPTICLI_IT_PROJECT";
    public const string SampleVariable = "OPTICLI_IT_SAMPLE";
    public const string DraftsVariable = "OPTICLI_IT_DRAFTS";
    public const string SeedVariable = "OPTICLI_IT_SEED";
    public const string ReportVariable = "OPTICLI_IT_REPORT";

    public static string? ProjectDirectory => Variable(ProjectVariable);

    /// <summary>How many content items (language branches) to compare.</summary>
    public static int Sample => Number(SampleVariable, 200);

    /// <summary>How many of the most recent drafts to compare as well, version by version.</summary>
    public static int Drafts => Number(DraftsVariable, 10);

    /// <summary>Changes which items are sampled; fixed by default so runs are repeatable.</summary>
    public static int Seed => Number(SeedVariable, 12345);

    /// <summary>Optional file the mismatch report is written to (it also goes to the test output); every mismatch goes next to it as .jsonl.</summary>
    public static string? ReportPath => Variable(ReportVariable);

    private static string? Variable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value.Trim() : null;

    private static int Number(string name, int fallback) =>
        int.TryParse(Variable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;
}
