using System.Text.Json;
using OptiCli.Core.Configuration;

namespace OptiCli.Core.Azure;

/// <summary>
/// Whether a site's build has the CMS's Azure providers (a sealed run only offers production's event bus or blob storage
/// then), and what its settings call their topic and container.
/// </summary>
public static class SiteAzure
{
    public const string TopicSetting = "EPiServer:Cms:AzureEventProvider:TopicName";

    public const string ContainerSetting = "EPiServer:Cms:AzureBlobProvider:ContainerName";

    /// <summary>The CMS's topic when none is configured (<c>AzureEventProviderOptionsConfigurer</c>).</summary>
    public const string DefaultTopic = "MySiteEvents";

    /// <summary>The CMS's container when none is configured (<c>AzureBlobProviderOptions</c>).</summary>
    public const string DefaultContainer = "mysitemedia";

    /// <summary>CMS 13 has the event provider in <c>EPiServer.Events.Azure</c>, CMS 12 in <c>EPiServer.Azure</c>.</summary>
    public static bool HasEventProvider(string outputFolder) => Any(outputFolder, "EPiServer.Events.Azure.dll", "EPiServer.Azure.dll");

    /// <summary>CMS 13 has the blob provider in <c>EPiServer.Blobs.Azure</c>, CMS 12 in <c>EPiServer.Azure</c>.</summary>
    public static bool HasBlobProvider(string outputFolder) => Any(outputFolder, "EPiServer.Blobs.Azure.dll", "EPiServer.Azure.dll");

    /// <summary>The value the project's <c>appsettings*.json</c> give <paramref name="key"/>, when they agree on one; null otherwise.</summary>
    public static string? Setting(string projectDirectory, string key)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.Exists(projectDirectory) ? Directory.EnumerateFiles(projectDirectory, "appsettings*.json") : [])
        {
            try
            {
                using var document = JsonConfigFile.Parse(file);
                foreach (var (name, value) in JsonConfigFile.Flatten(document.RootElement))
                {
                    if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                    {
                        values.Add(value.Trim());
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // An unreadable settings file offers no default.
            }
        }
        return values.Count == 1 ? values.First() : null;
    }

    private static bool Any(string folder, params string[] files) => files.Any(f => File.Exists(Path.Combine(folder, f)));
}
