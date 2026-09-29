using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Serve;

/// <summary>Renders the site's environment variables for <c>opticli env</c>.</summary>
public static class EnvFormat
{
    public const string Shell = "shell";
    public const string PowerShell = "powershell";
    public const string Dotenv = "dotenv";
    public const string Json = "json";
    public const string LaunchSettings = "launchSettings";

    public static readonly IReadOnlyList<string> All = [Shell, PowerShell, Dotenv, Json, LaunchSettings];

    /// <param name="comments">Lines shown as comments where the format has them.</param>
    /// <exception cref="UsageException">Unknown format (json is rendered by the caller as an envelope).</exception>
    public static string Render(string format, IReadOnlyList<KeyValuePair<string, string>> variables, int port, IReadOnlyList<string> comments)
    {
        var text = new StringBuilder();
        switch (format)
        {
            case Shell:
                Comments(text, "#", comments);
                foreach (var (name, value) in variables)
                {
                    text.Append("export ").Append(name).Append('=').Append(ShellQuote(value)).Append('\n');
                }
                break;
            case PowerShell:
                Comments(text, "#", comments);
                foreach (var (name, value) in variables)
                {
                    text.Append("$env:").Append(name).Append(" = '").Append(value.Replace("'", "''", StringComparison.Ordinal)).Append("'\n");
                }
                break;
            case Dotenv:
                Comments(text, "#", comments);
                foreach (var (name, value) in variables)
                {
                    text.Append(name).Append("=\"").Append(value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append("\"\n");
                }
                break;
            case LaunchSettings:
                // A profile fragment: applicationUrl replaces ASPNETCORE_URLS, which launch profiles override anyway.
                var profile = new JsonObject
                {
                    ["commandName"] = "Project",
                    ["applicationUrl"] = SiteEnvironment.Url(port),
                    ["environmentVariables"] = new JsonObject(variables.Select(v => KeyValuePair.Create(v.Key, (JsonNode?)JsonValue.Create(v.Value)))),
                };
                text.Append(new JsonObject { ["opticli"] = profile }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })).Append('\n');
                break;
            default:
                throw new UsageException($"Unknown format '{format}'.", $"Use one of {string.Join(", ", All)}.");
        }
        return text.ToString();
    }

    /// <summary>POSIX single quotes: nothing inside is special except the quote itself.</summary>
    public static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static void Comments(StringBuilder text, string marker, IReadOnlyList<string> comments)
    {
        foreach (var comment in comments)
        {
            text.Append(marker).Append(' ').Append(comment).Append('\n');
        }
    }
}
