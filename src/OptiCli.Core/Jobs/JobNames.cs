using OptiCli.Core.SourceScan;

namespace OptiCli.Core.Jobs;

/// <summary>
/// The names admin mode shows for scheduled jobs. CMS 12 stores that name in <c>tblScheduledItem.Name</c>; CMS 13 stores
/// the class name there (<c>PageArchiveJob</c>) and takes the shown name from a localization or the job's attribute at
/// run time, which a database read can't see. So on CMS 13 the CMS's own jobs get their name from
/// <see cref="Cms"/>, and the site's jobs the <c>DisplayName</c> of their attribute in the site's source; any other job
/// keeps its class name. A job is always found by its class name too (<see cref="JobReferences"/>).
/// </summary>
public static class JobNames
{
    /// <summary>
    /// The CMS's own jobs by id, with the English names admin mode shows: the same names CMS 12 stored (read from a CMS
    /// 12.29 database), and, for the job CMS 13 added, its localization in EPiServer.UI 13.3.0
    /// (<c>/admin/databasejob/variationcleanupjob/displayname</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<Guid, string> Cms = new Dictionary<Guid, string>
    {
        [Guid.Parse("8bd1ac63-9ed3-42e1-9b63-76498ab5ac94")] = "Optimizely Notifications",
        [Guid.Parse("b32597bc-1a69-4095-b215-8fc6c1e5722a")] = "Change Log Auto Truncate",
        [Guid.Parse("6bce1827-f306-476a-b766-2b35838f6ea0")] = "Link Validation",
        [Guid.Parse("4b61cdb6-cc46-417c-a1ad-a5020a32e1d3")] = "Notification Dispatcher",
        [Guid.Parse("c9bad721-5a61-4df3-8eb9-a57fcb981ce1")] = "Notification Message Truncate",
        [Guid.Parse("338f0124-8a37-41c5-b4d3-285901735212")] = "Remove Permanent Editing",
        [Guid.Parse("bbf2eccd-2861-45f6-845c-b4d8cc5377df")] = "Remove Abandoned BLOBs",
        [Guid.Parse("e652f3bd-f550-40e8-8743-2c39cda651dc")] = "Remove Unrelated Content Assets",
        [Guid.Parse("17f4a400-75e5-4449-a0ae-c44bfa50a213")] = "Publish Delayed Content Versions",
        [Guid.Parse("a42f6137-0bcf-4a88-bbd3-0ef219b7eafa")] = "Automatic Emptying of Trash",
        [Guid.Parse("656e747e-b2cb-4930-83dc-5d8d97aeaabb")] = "Trim Content Versions",
        [Guid.Parse("63c7f148-12b1-4cdf-a2ca-8458208c6c26")] = "Archive Function",
        [Guid.Parse("9b72af8b-a26d-4c68-9a1e-1da242edf6fd")] = "Monitored Tasks Auto Truncate",
        [Guid.Parse("f1b8e71c-5e6f-41c2-ab3e-d8fd85ef6c6d")] = "Clear Thumbnail Properties",
        [Guid.Parse("7f422978-49cf-4bc4-9757-105fb252a1c1")] = "Remove Unused Content Variations",
    };

    /// <summary>
    /// The name to show for a job whose stored name is its class name (CMS 13): the CMS's name for one of its own jobs,
    /// else the <c>DisplayName</c> in the site's source, else the stored name.
    /// </summary>
    /// <param name="sources">The jobs in the site's source; null or empty when it wasn't scanned.</param>
    public static string Readable(Guid id, string storedName, string? typeName, IReadOnlyList<ScheduledJobSource>? sources)
    {
        if (Cms.TryGetValue(id, out var name))
        {
            return name;
        }
        return sources is not null && ScheduledJobSources.Match(sources, id, typeName)?.DisplayName is { Length: > 0 } shown ? shown : storedName;
    }
}
