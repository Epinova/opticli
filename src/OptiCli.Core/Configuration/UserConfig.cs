using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Configuration;

/// <summary>Per-project settings from the user-level opticli config file, keyed by project directory.</summary>
/// <param name="Output">Site build output DLL, relative to the project directory (used by <c>serve</c>).</param>
/// <param name="Database">The development database the user chose (<c>opticli db use</c>).</param>
/// <param name="Https">Run <c>serve</c> with <c>--https</c> by default.</param>
/// <param name="PrimaryHosts">
/// The saved <c>sites primary</c> mapping (<c>sites primary --save</c>): <c>Site A</c> or <c>Site A@nb</c> to the host.
/// What <c>sites primary --from-config</c> applies after a restore.
/// </param>
public sealed record ProjectSettings(string? Connection, string? Output, int? Port, SavedDatabase? Database = null, bool Https = false, IReadOnlyDictionary<string, SavedPrimaryHost>? PrimaryHosts = null);

/// <summary>
/// One entry of the saved <c>sites primary</c> mapping. Saved as the plain host string (<c>"localhost:5001"</c>) unless
/// it has an option: then <c>{"host": "localhost:5001", "keepEdit": true, "keepSiteUrl": true}</c>.
/// </summary>
/// <param name="Host">The host, as <c>https://host</c> (or <c>http://</c>) when the scheme was given.</param>
/// <param name="KeepEdit"><c>--keep-edit</c> was given for it.</param>
/// <param name="KeepSiteUrl"><c>--keep-site-url</c> was given for it.</param>
public sealed record SavedPrimaryHost(string Host, bool KeepEdit = false, bool KeepSiteUrl = false);

/// <summary>
/// The candidate the user chose as the project's development database. <see cref="Id"/> is what counts; the rest
/// says what it was, for when it no longer matches.
/// </summary>
/// <param name="Location">Where the connection string was found (a file path).</param>
/// <param name="ChosenVia"><c>prompt</c> (the interactive question) or <c>command</c> (<c>opticli db use &lt;id&gt;</c>).</param>
public sealed record SavedDatabase(
    string Id,
    ConnectionSource Source,
    string Location,
    string? Key,
    string? Profile,
    string? Server,
    string? Database,
    bool Local,
    DateTimeOffset ChosenAt,
    string ChosenVia)
{
    public const string ViaPrompt = "prompt";

    public const string ViaCommand = "command";

    public static SavedDatabase From(ConnectionCandidate candidate, string via, DateTimeOffset now) => new(
        candidate.Id ?? throw new ArgumentException("Only a selectable candidate can be saved.", nameof(candidate)),
        candidate.Source,
        candidate.Location,
        candidate.Key,
        candidate.Profile,
        candidate.Server,
        candidate.Database,
        candidate.IsLocal == true,
        now,
        via);
}

/// <summary>
/// <c>~/.config/opticli/config.json</c>:
/// <c>{"projects": {"/abs/project/dir": {"connection": "...", "output": "bin/Debug/net8.0/X.dll", "port": 5199, "database": {...},
/// "sites": {"primary": {"Site A": "localhost:5001"}}}}}</c>.
/// </summary>
public static class UserConfig
{
    private const string DatabaseKey = "database";

    private const string SitesKey = "sites";

    private const string PrimaryKey = "primary";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <returns>The settings for <paramref name="projectDirectory"/>, or null when there are none.</returns>
    /// <exception cref="UsageException">The config file is not valid JSON.</exception>
    public static ProjectSettings? ForProject(string configFile, string projectDirectory)
    {
        if (!File.Exists(configFile))
        {
            return null;
        }

        using var document = Invalid(configFile, () => JsonConfigFile.Parse(configFile));
        if (!document.RootElement.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var wanted = Normalize(projectDirectory);
        foreach (var entry in projects.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Object && TryNormalize(entry.Name) is { } key && PathsEqual(key, wanted))
            {
                return new ProjectSettings(
                    GetString(entry.Value, "connection"),
                    GetString(entry.Value, "output"),
                    entry.Value.TryGetProperty("port", out var port) && port.TryGetInt32(out var number) ? number : null,
                    entry.Value.TryGetProperty(DatabaseKey, out var database) ? ReadDatabase(database) : null,
                    entry.Value.TryGetProperty("https", out var https) && https.ValueKind == JsonValueKind.True,
                    ReadPrimaryHosts(entry.Value));
            }
        }
        return null;
    }

    /// <summary>Saves (or with null, removes) the project's chosen development database, keeping everything else in the file.</summary>
    /// <remarks>The file is rewritten as plain JSON, so comments in it are lost.</remarks>
    /// <exception cref="UsageException">The existing config file is not valid JSON.</exception>
    public static void SaveDatabase(string configFile, string projectDirectory, SavedDatabase? database) =>
        UpdateProject(configFile, projectDirectory, create: database is not null, project =>
        {
            if (database is null)
            {
                project.Remove(DatabaseKey);
            }
            else
            {
                var saved = new JsonObject
                {
                    ["id"] = database.Id,
                    ["source"] = database.Source.ToString(),
                    ["location"] = database.Location,
                    ["key"] = database.Key,
                    ["profile"] = database.Profile,
                    ["server"] = database.Server,
                    ["database"] = database.Database,
                    ["local"] = database.Local,
                    ["chosenAt"] = database.ChosenAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ["chosenVia"] = database.ChosenVia,
                };
                foreach (var empty in saved.Where(p => p.Value is null).Select(p => p.Key).ToList())
                {
                    saved.Remove(empty);
                }
                project[DatabaseKey] = saved;
            }
        });

    /// <summary>
    /// Saves <c>sites primary</c> pairs to the project's mapping (<see cref="ProjectSettings.PrimaryHosts"/>): every entry
    /// for the sites in <paramref name="sites"/> (<c>Site A</c>, and <c>Site A@lang</c> for each of
    /// <paramref name="languages"/>) is replaced by <paramref name="entries"/>; other sites' entries stay. Keeps everything
    /// else in the file.
    /// </summary>
    /// <remarks>The file is rewritten as plain JSON, so comments in it are lost.</remarks>
    /// <exception cref="UsageException">The existing config file is not valid JSON.</exception>
    public static void SavePrimaryHosts(string configFile, string projectDirectory, IReadOnlyCollection<string> sites, IReadOnlyCollection<string> languages, IReadOnlyList<KeyValuePair<string, SavedPrimaryHost>> entries) =>
        UpdateProject(configFile, projectDirectory, create: entries.Count > 0, project =>
        {
            var mapping = PrimaryMapping(project);
            RemoveEntries(mapping, sites, languages);
            foreach (var (key, value) in entries)
            {
                if (!value.KeepEdit && !value.KeepSiteUrl)
                {
                    mapping[key] = value.Host;
                    continue;
                }
                var entry = new JsonObject { ["host"] = value.Host };
                if (value.KeepEdit)
                {
                    entry["keepEdit"] = true;
                }
                if (value.KeepSiteUrl)
                {
                    entry["keepSiteUrl"] = true;
                }
                mapping[key] = entry;
            }
        });

    /// <summary>
    /// Removes the mapping's entries for <paramref name="site"/> (a site name with all its <c>@lang</c> entries, or one
    /// key such as <c>Site A@nb</c>), e.g. for a site that was deleted or renamed. Keeps everything else in the file.
    /// </summary>
    /// <returns>The keys removed.</returns>
    /// <exception cref="UsageException">The existing config file is not valid JSON.</exception>
    public static IReadOnlyList<string> ForgetPrimaryHosts(string configFile, string projectDirectory, string site, IReadOnlyCollection<string> languages)
    {
        var removed = new List<string>();
        if (UserConfig.ForProject(configFile, projectDirectory)?.PrimaryHosts is null)
        {
            return removed;
        }
        UpdateProject(configFile, projectDirectory, create: false, project => removed.AddRange(RemoveEntries(PrimaryMapping(project), [site], languages)));
        return removed;
    }

    private static JsonObject PrimaryMapping(JsonObject project)
    {
        if (project[SitesKey] is not JsonObject section)
        {
            section = [];
            project[SitesKey] = section;
        }
        if (section[PrimaryKey] is not JsonObject mapping)
        {
            mapping = [];
            section[PrimaryKey] = mapping;
        }
        return mapping;
    }

    private static List<string> RemoveEntries(JsonObject mapping, IReadOnlyCollection<string> sites, IReadOnlyCollection<string> languages)
    {
        var keys = mapping.Select(p => p.Key).Where(k => sites.Any(site => IsEntryFor(k, site, languages))).ToList();
        foreach (var key in keys)
        {
            mapping.Remove(key);
        }
        return keys;
    }

    /// <summary>
    /// <c>Site A</c> and <c>Site A@nb</c> (with <c>nb</c> one of <paramref name="languages"/>) are entries for the site
    /// <c>Site A</c>; <c>Site A@Home</c> is another site's.
    /// </summary>
    internal static bool IsEntryFor(string key, string site, IReadOnlyCollection<string> languages) =>
        key.Equals(site, StringComparison.OrdinalIgnoreCase)
        || (key.Length > site.Length + 1
            && key.StartsWith(site, StringComparison.OrdinalIgnoreCase)
            && key[site.Length] == '@'
            && languages.Contains(key[(site.Length + 1)..], StringComparer.OrdinalIgnoreCase));

    /// <param name="create">Add the project when the file doesn't have it; otherwise a missing project is left alone.</param>
    private static void UpdateProject(string configFile, string projectDirectory, bool create, Action<JsonObject> change)
    {
        var root = File.Exists(configFile)
            ? Invalid(configFile, () => JsonNode.Parse(File.ReadAllText(configFile), documentOptions: ReadOptions) as JsonObject ?? throw new JsonException("The root is not a JSON object."))
            : [];
        if (root["projects"] is not JsonObject projects)
        {
            projects = [];
            root["projects"] = projects;
        }

        var wanted = Normalize(projectDirectory);
        var key = projects.Select(p => p.Key).FirstOrDefault(k => TryNormalize(k) is { } existing && PathsEqual(existing, wanted)) ?? wanted;
        if (projects[key] is not JsonObject project)
        {
            if (!create)
            {
                return;
            }
            project = [];
            projects[key] = project;
        }

        change(project);

        Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
        Replace(configFile, root.ToJsonString(WriteOptions) + System.Environment.NewLine);
    }

    private static IReadOnlyDictionary<string, SavedPrimaryHost>? ReadPrimaryHosts(JsonElement project)
    {
        if (!project.TryGetProperty(SitesKey, out var sites) || sites.ValueKind != JsonValueKind.Object
            || !sites.TryGetProperty(PrimaryKey, out var primary) || primary.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var mapping = new Dictionary<string, SavedPrimaryHost>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in primary.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
            {
                mapping[entry.Name] = new SavedPrimaryHost(entry.Value.GetString()!);
            }
            else if (entry.Value.ValueKind == JsonValueKind.Object && GetString(entry.Value, "host") is { } host)
            {
                mapping[entry.Name] = new SavedPrimaryHost(
                    host,
                    entry.Value.TryGetProperty("keepEdit", out var keepEdit) && keepEdit.ValueKind == JsonValueKind.True,
                    entry.Value.TryGetProperty("keepSiteUrl", out var keepSiteUrl) && keepSiteUrl.ValueKind == JsonValueKind.True);
            }
        }
        return mapping.Count > 0 ? mapping : null;
    }

    /// <summary>
    /// Writes through a temporary file next to it (unique, so two opticli runs can't mix their writes) that is renamed
    /// over it. On Unix the temporary file is created with the config file's mode, user-only for a new one: the config
    /// may hold a connection string.
    /// </summary>
    internal static void Replace(string configFile, string content)
    {
        var temporary = $"{configFile}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        UnixFileMode? mode = null;
        if (!OperatingSystem.IsWindows())
        {
            mode = File.Exists(configFile) ? File.GetUnixFileMode(configFile) : UnixFileMode.UserRead | UnixFileMode.UserWrite;
            options.UnixCreateMode = mode.Value & (UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        try
        {
            using (var writer = new StreamWriter(new FileStream(temporary, options), new System.Text.UTF8Encoding(false)))
            {
                writer.Write(content);
            }
            if (mode is { } original && !OperatingSystem.IsWindows())
            {
                // Exactly the original mode, which the umask may have narrowed at creation; never wider before this.
                File.SetUnixFileMode(temporary, original);
            }
            File.Move(temporary, configFile, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static T Invalid<T>(string configFile, Func<T> parse)
    {
        try
        {
            return parse();
        }
        catch (JsonException ex)
        {
            throw new UsageException($"{configFile} is not valid JSON: {ex.Message}", "Fix the file, or delete it (opticli asks for the development database again).");
        }
    }

    private static SavedDatabase? ReadDatabase(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || GetString(element, "id") is not { } id
            || GetString(element, "location") is not { } location
            || !Enum.TryParse<ConnectionSource>(GetString(element, "source"), ignoreCase: true, out var source))
        {
            return null;
        }
        return new SavedDatabase(
            id,
            source,
            location,
            GetString(element, "key"),
            GetString(element, "profile"),
            GetString(element, "server"),
            GetString(element, "database"),
            element.TryGetProperty("local", out var local) && local.ValueKind == JsonValueKind.True,
            DateTimeOffset.TryParse(GetString(element, "chosenAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue,
            GetString(element, "chosenVia") ?? SavedDatabase.ViaCommand);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Null for a project key that isn't a path (empty, invalid characters): it can't match any project.</summary>
    private static string? TryNormalize(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
