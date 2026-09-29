namespace OptiCli.Core.Content;

/// <summary>
/// <c>tblWorkContent.Status</c> and <c>tblContentLanguage.Status</c> (EPiServer.Core.VersionStatus).
/// </summary>
public enum VersionStatus
{
    NotCreated = 0,
    Rejected = 1,
    CheckedOut = 2,
    CheckedIn = 3,
    Published = 4,
    PreviouslyPublished = 5,
    DelayedPublish = 6,
    AwaitingApproval = 7,
}

public static class VersionStatuses
{
    /// <summary>Statuses of versions that hold changes nobody has published yet.</summary>
    public static readonly IReadOnlyList<VersionStatus> Unpublished =
        [VersionStatus.Rejected, VersionStatus.CheckedOut, VersionStatus.CheckedIn, VersionStatus.DelayedPublish, VersionStatus.AwaitingApproval];

    /// <summary>The comma-separated numbers of <see cref="Unpublished"/>, for SQL <c>IN (...)</c> lists.</summary>
    public static string UnpublishedSql { get; } = string.Join(",", Unpublished.Select(s => (int)s));

    public static VersionStatus From(int? value) =>
        value is { } number && Enum.IsDefined(typeof(VersionStatus), number) ? (VersionStatus)number : VersionStatus.NotCreated;

    /// <summary>
    /// The version a branch's primary values (<c>tblContentProperty</c>) belong to. Published branches: the published
    /// version, <c>tblContentLanguage.Version</c>. Branches never published: the CMS keeps the primary values in step
    /// with the common draft while <c>tblContentLanguage.Version</c> may still name the first version saved, so the
    /// common draft wins when there is one.
    /// </summary>
    public static int? PrimaryVersion(VersionStatus branchStatus, int? storedVersion, int? commonDraft) =>
        branchStatus == VersionStatus.Published ? storedVersion : commonDraft ?? storedVersion;

    /// <summary>camelCase name used in output (<c>published</c>, <c>checkedOut</c>, ...).</summary>
    public static string Name(VersionStatus status)
    {
        var name = status.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
