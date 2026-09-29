namespace OptiCli.Core.Safety;

/// <summary>
/// Outcome of the local/remote check. <see cref="Server"/> and <see cref="Database"/> are the values
/// SqlClient would actually use, so they are safe to show (they never include credentials).
/// </summary>
/// <param name="IsValid">False when the string could not be parsed at all.</param>
public sealed record SafetyVerdict(bool IsLocal, string? Server, string? Database, string? Reason, bool IsValid = true);
