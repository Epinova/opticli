using System.Text.Json;
using OptiCli.Core.Configuration;

namespace OptiCli.Core.Queries;

/// <param name="Uri">As stored: <c>epi.fx.blob://default/{container}/{file}</c>.</param>
/// <param name="Root">The file blob provider's folder, resolved against the project directory.</param>
/// <param name="RootSource">Where <paramref name="Root"/> came from (a setting key and file, or the CMS default).</param>
/// <param name="Path">The file on disk, when the provider is the file provider and the project is known.</param>
public sealed record BlobLocation(string Uri, string? Provider, string? Container, string? File, string? Root, string? RootSource, string? Path, bool? Exists, long? Size);

/// <summary>
/// Maps a media item's blob URI to the file the site's <c>FileBlobProvider</c> keeps it in, reading the
/// provider path the way CMS 12 does: <c>EPiServer:Cms:FileBlobProvider:Path</c> (default
/// <c>[appDataPath]\blobs</c>), with <c>[appDataPath]</c> from <c>...:AppDataPath</c> (default <c>App_Data</c>).
/// </summary>
public static class BlobLocator
{
    public const string Scheme = "epi.fx.blob";
    private const string AppDataToken = "[appDataPath]";
    private const string DefaultAppData = "App_Data";
    private const string DefaultPath = AppDataToken + @"\blobs";

    private static readonly string[] PathKeys = ["EPiServer:Cms:FileBlobProvider:Path", "EPiServer:Framework:FileBlobProvider:Path"];

    /// <summary>Only appsettings.json and the Development override are read, as for connection strings.</summary>
    private static readonly string[] SettingsFiles = ["appsettings.json", "appsettings.Development.json"];

    public static (string? Provider, string? Container, string? File) ParseUri(string uri)
    {
        if (!System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != Scheme)
        {
            return (null, null, null);
        }
        var parts = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (parsed.Host, parts.Length > 1 ? parts[0] : null, parts.Length > 0 ? System.Uri.UnescapeDataString(parts[^1]) : null);
    }

    /// <param name="settings">Flattened configuration, later files overriding earlier ones.</param>
    public static (string Root, string Source) ResolveRoot(IReadOnlyDictionary<string, (string? Value, string File)> settings, string projectDirectory)
    {
        var (path, source) = PathKeys.Select(k => settings.TryGetValue(k, out var v) && v.Value is not null ? (v.Value, $"{k} in {v.File}") : default)
            .FirstOrDefault(v => v.Value is not null);
        if (path is null)
        {
            (path, source) = (DefaultPath, "CMS default");
        }

        var appData = settings
            .Where(s => s.Key.StartsWith("EPiServer:", StringComparison.OrdinalIgnoreCase) && s.Key.EndsWith(":AppDataPath", StringComparison.OrdinalIgnoreCase) && s.Value.Value is not null)
            .Select(s => s.Value.Value)
            .FirstOrDefault() ?? DefaultAppData;

        var expanded = path.Replace(AppDataToken, appData, StringComparison.OrdinalIgnoreCase).Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar);
        return (System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDirectory, expanded)), source!);
    }

    public static BlobLocation Locate(string uri, string? projectDirectory)
    {
        var (provider, container, file) = ParseUri(uri);
        if (provider is null || projectDirectory is null)
        {
            return new BlobLocation(uri, provider, container, file, null, null, null, null, null);
        }

        var (root, source) = ResolveRoot(ReadSettings(projectDirectory), projectDirectory);
        var path = container is null || file is null ? null : System.IO.Path.Combine(root, container, file);
        var info = path is null ? null : new FileInfo(path);
        return new BlobLocation(uri, provider, container, file, root, source, path, info?.Exists, info is { Exists: true } ? info.Length : null);
    }

    private static Dictionary<string, (string? Value, string File)> ReadSettings(string projectDirectory)
    {
        var settings = new Dictionary<string, (string?, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SettingsFiles)
        {
            var file = System.IO.Path.Combine(projectDirectory, name);
            if (!System.IO.File.Exists(file))
            {
                continue;
            }
            try
            {
                using var document = JsonConfigFile.Parse(file);
                foreach (var (key, value) in JsonConfigFile.Flatten(document.RootElement))
                {
                    settings[key] = (value, name);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // An unreadable settings file just means the default path applies.
            }
        }
        return settings;
    }
}
