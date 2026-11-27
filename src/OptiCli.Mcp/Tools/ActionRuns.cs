using EPiServer.Data;
using EPiServer.Data.Dynamic;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// The record of an action tool's run (<see cref="ActionRunner"/>), by its token: so a token runs once, and the same token
/// again answers with what the run did.
/// </summary>
/// <param name="Key">The token's SHA-256, hexadecimal: never the token.</param>
/// <param name="Editor">The editor who ran it.</param>
/// <param name="Arguments">The SHA-256 of the arguments as the tool read them.</param>
/// <param name="Started">When the run started.</param>
/// <param name="Expires">When the record may go: some time after its token expires.</param>
/// <param name="Finished">When the run ended; null while it runs, and for a run the site stopped during.</param>
/// <param name="Result">The result the run answered with (JSON); null until it ended.</param>
/// <param name="Failed">Whether that result was an error.</param>
internal sealed record ActionRunRecord(
    string Key, string Editor, string Arguments, DateTimeOffset Started, DateTimeOffset Expires,
    DateTimeOffset? Finished = null, string? Result = null, bool Failed = false);

/// <summary>Where the records of action tools' runs are kept; a test registers its own.</summary>
internal interface IActionRunStore
{
    /// <returns>The record of the run with this key; null for none.</returns>
    ActionRunRecord? Find(string key);

    /// <summary>
    /// Records <paramref name="run"/> as started, unless a run with its key is recorded already: what makes a token run
    /// once. Also deletes some records that have expired.
    /// </summary>
    /// <returns>Null when it was recorded; else the record that was there.</returns>
    ActionRunRecord? TryStart(ActionRunRecord run, DateTimeOffset now);

    /// <summary>Records how the run with <paramref name="key"/> ended.</summary>
    void Finish(string key, DateTimeOffset at, string result, bool failed);
}

/// <summary>The records in memory, for one instance: the tests'.</summary>
internal sealed class InMemoryActionRunStore : IActionRunStore
{
    private readonly Dictionary<string, ActionRunRecord> _runs = new(StringComparer.Ordinal);

    public ActionRunRecord? Find(string key)
    {
        lock (_runs)
        {
            return _runs.GetValueOrDefault(key);
        }
    }

    public ActionRunRecord? TryStart(ActionRunRecord run, DateTimeOffset now)
    {
        lock (_runs)
        {
            foreach (var expired in _runs.Values.Where(r => r.Expires < now).Select(r => r.Key).ToList())
            {
                _runs.Remove(expired);
            }
            if (_runs.TryGetValue(run.Key, out var existing))
            {
                return existing;
            }
            _runs[run.Key] = run;
            return null;
        }
    }

    public void Finish(string key, DateTimeOffset at, string result, bool failed)
    {
        lock (_runs)
        {
            if (_runs.TryGetValue(key, out var run))
            {
                _runs[key] = run with { Finished = at, Result = result, Failed = failed };
            }
        }
    }
}

/// <summary>
/// The records in the CMS's Dynamic Data Store, as <see cref="DdsOAuthStore"/> keeps clients, codes and grants: no table
/// or migration for the site, and shared by every instance of a load-balanced site.
/// </summary>
/// <remarks>
/// The Dynamic Data Store has no conditional insert, so starting a run is a find then a save under a lock, which
/// serialises it within the instance only: two instances given the same token within milliseconds could both run it.
/// </remarks>
internal sealed class DdsActionRunStore(DynamicDataStoreFactory stores) : IActionRunStore
{
    /// <summary>The most expired records one start deletes, so a start stays short whatever has piled up.</summary>
    internal const int MaxDeletedPerStart = 50;

    private static readonly object StartLock = new();

    public ActionRunRecord? Find(string key) => FindData(key)?.ToRecord();

    public ActionRunRecord? TryStart(ActionRunRecord run, DateTimeOffset now)
    {
        lock (StartLock)
        {
            var store = Store();
            var cutoff = now.UtcDateTime;
            foreach (var expired in store.Items<McpActionRunData>().Where(r => r.Expires < cutoff).Take(MaxDeletedPerStart).ToList())
            {
                store.Delete(expired.Id);
            }
            if (FindData(run.Key) is { } existing)
            {
                return existing.ToRecord();
            }
            store.Save(McpActionRunData.From(run));
            return null;
        }
    }

    public void Finish(string key, DateTimeOffset at, string result, bool failed)
    {
        lock (StartLock)
        {
            if (FindData(key) is { } data)
            {
                data.Finished = at.UtcDateTime;
                data.Result = result;
                data.Failed = failed;
                Store().Save(data);
            }
        }
    }

    private McpActionRunData? FindData(string key) =>
        Store().Find<McpActionRunData>(nameof(McpActionRunData.Key), key).FirstOrDefault();

    private DynamicDataStore Store() => stores.GetStore(typeof(McpActionRunData)) ?? stores.CreateStore(typeof(McpActionRunData));
}

/// <summary>An <see cref="ActionRunRecord"/> as the Dynamic Data Store keeps it.</summary>
[EPiServerDataStore(StoreName = "OptiCliMcpActionRuns", AutomaticallyCreateStore = true, AutomaticallyRemapStore = true)]
public sealed class McpActionRunData : IDynamicData
{
    /// <summary>The Dynamic Data Store's own id.</summary>
    public Identity Id { get; set; } = Identity.NewIdentity();

    /// <summary>The token's SHA-256.</summary>
    [EPiServerDataIndex]
    public string Key { get; set; } = "";

    /// <summary>The editor who ran it.</summary>
    public string Editor { get; set; } = "";

    /// <summary>The SHA-256 of the arguments.</summary>
    public string Arguments { get; set; } = "";

    /// <summary>When the run started; UTC.</summary>
    public DateTime Started { get; set; }

    /// <summary>When the record may go; UTC.</summary>
    public DateTime Expires { get; set; }

    /// <summary>When the run ended; UTC, null while it runs.</summary>
    public DateTime? Finished { get; set; }

    /// <summary>The run's result, JSON.</summary>
    public string? Result { get; set; }

    /// <summary>Whether the result was an error.</summary>
    public bool Failed { get; set; }

    internal static McpActionRunData From(ActionRunRecord run) => new()
    {
        Key = run.Key,
        Editor = run.Editor,
        Arguments = run.Arguments,
        Started = run.Started.UtcDateTime,
        Expires = run.Expires.UtcDateTime,
        Finished = run.Finished?.UtcDateTime,
        Result = run.Result,
        Failed = run.Failed,
    };

    internal ActionRunRecord ToRecord() => new(
        Key, Editor, Arguments, DdsOAuthStore.Utc(Started), DdsOAuthStore.Utc(Expires),
        Finished is { } finished ? DdsOAuthStore.Utc(finished) : null, Result, Failed);
}
