using System.Text.Json;

namespace OptiCli.Mcp.Integration.Support;

/// <summary>
/// Environment variables that point the end-to-end tests at the MCP test site (tests/fixtures/mcp/setup.sh builds and
/// starts it).
/// </summary>
internal static class McpSiteSettings
{
    /// <summary>The site's origin, e.g. <c>http://127.0.0.1:5180</c>.</summary>
    public const string UrlVariable = "OPTICLI_MCP_IT_URL";

    /// <summary>The fixture's <c>App_Data/mcp-test-users.json</c>: user name to password.</summary>
    public const string UsersVariable = "OPTICLI_MCP_IT_USERS";

    /// <summary>Optional: the same site started a second time with <c>--no-publish</c> (<c>OptiCli__Mcp__AllowPublish=false</c>).</summary>
    public const string NoPublishUrlVariable = "OPTICLI_MCP_IT_NO_PUBLISH_URL";

    public static Uri? Url => Origin(UrlVariable);

    public static Uri? NoPublishUrl => Origin(NoPublishUrlVariable);

    public static string? UsersFile => Variable(UsersVariable);

    /// <summary>The site's own directory: the users file is in its App_Data.</summary>
    public static string SiteDirectory => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(UsersFile!)))!;

    /// <summary>The fixture's test users, by name; their roles are in McpFixture.cs.</summary>
    public static string Password(string user)
    {
        var users = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(UsersFile!))!;
        return users.TryGetValue(user, out var password)
            ? password
            : throw new InvalidOperationException($"{UsersFile} has no password for {user}: is the site built with tests/fixtures/mcp/setup.sh?");
    }

    private static Uri? Origin(string name) => Variable(name) is { } value ? new Uri(value.TrimEnd('/') + "/") : null;

    private static string? Variable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value.Trim() : null;
}

/// <summary>The fixture's users (tests/fixtures/mcp/McpFixture.cs).</summary>
internal static class TestUsers
{
    /// <summary>WebAdmins and WebEditors: everything, and every editor's connections.</summary>
    public const string Admin = "mcp-admin";

    /// <summary>WebEditors: Read to Publish from the root down, but not the hidden page.</summary>
    public const string Editor = "mcp-editor";

    /// <summary>ProductEditors only: may read and edit the hidden page, not publish it.</summary>
    public const string Product = "mcp-product";

    /// <summary>No role: the role gate turns them away.</summary>
    public const string Visitor = "mcp-visitor";

    /// <summary>Alloy's "Alloy Meet", which the fixture hides from everyone but administrators and product editors.</summary>
    public const string HiddenPage = "456929c5-d6b8-46c5-b339-896be5ccfddc";

    /// <summary>The edge-case site's approval root (EdgeCasesSetup.ApprovalRoot): an approval sequence applies below it.</summary>
    public const string ApprovalRoot = "6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02";
}

/// <summary>
/// A fact that needs the MCP test site: it runs only when <see cref="McpSiteSettings.UrlVariable"/> and
/// <see cref="McpSiteSettings.UsersVariable"/> are set, and is reported as skipped otherwise, so a plain
/// <c>dotnet test</c> stays green and fast.
/// </summary>
public sealed class McpSiteFactAttribute : FactAttribute
{
    public McpSiteFactAttribute()
    {
        if (McpSiteSettings.Url is null || McpSiteSettings.UsersFile is null)
        {
            Skip = $"Needs the MCP test site: build it with tests/fixtures/mcp/setup.sh, then set {McpSiteSettings.UrlVariable} and {McpSiteSettings.UsersVariable}.";
        }
    }
}

/// <summary>A fact for the second configuration, publishing off: it also needs <see cref="McpSiteSettings.NoPublishUrlVariable"/>.</summary>
public sealed class NoPublishSiteFactAttribute : FactAttribute
{
    public NoPublishSiteFactAttribute()
    {
        if (McpSiteSettings.NoPublishUrl is null || McpSiteSettings.UsersFile is null)
        {
            Skip = $"Needs the MCP test site started with publishing off (tests/fixtures/mcp/serve.sh <site> --port 5181 --no-publish): set {McpSiteSettings.NoPublishUrlVariable} and {McpSiteSettings.UsersVariable}.";
        }
    }
}
