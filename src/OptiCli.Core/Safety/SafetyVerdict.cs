namespace OptiCli.Core.Safety;

/// <summary>
/// Outcome of the local/remote check. <see cref="Server"/> and <see cref="Database"/> are the values
/// SqlClient would actually use, so they are safe to show (they never include credentials).
/// </summary>
/// <param name="IsValid">False when the string could not be parsed at all.</param>
/// <param name="FailoverPartner">The server SqlClient switches to when <see cref="Server"/> doesn't answer; null when none is set.</param>
public sealed record SafetyVerdict(bool IsLocal, string? Server, string? Database, string? Reason, bool IsValid = true, string? FailoverPartner = null);
