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
public sealed record ProjectSettings(string? Connection, string? Output, int? Port, SavedDatabase? Database = null, bool Https = false);

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
/// <c>{"projects": {"/abs/project/dir": {"connection": "...", "output": "bin/Debug/net8.0/X.dll", "port": 5199, "database": {...}}}}</c>.
/// </summary>
public static class UserConfig
{
    private const string DatabaseKey = "database";

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
                    entry.Value.TryGetProperty("https", out var https) && https.ValueKind == JsonValueKind.True);
            }
        }
        return null;
    }

    /// <summary>Saves (or with null, removes) the project's chosen development database, keeping everything else in the file.</summary>
    /// <remarks>The file is rewritten as plain JSON, so comments in it are lost.</remarks>
    /// <exception cref="UsageException">The existing config file is not valid JSON.</exception>
    public static void SaveDatabase(string configFile, string projectDirectory, SavedDatabase? database)
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
            if (database is null)
            {
                return;
            }
            project = [];
            projects[key] = project;
        }

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

        Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
        Replace(configFile, root.ToJsonString(WriteOptions) + System.Environment.NewLine);
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
