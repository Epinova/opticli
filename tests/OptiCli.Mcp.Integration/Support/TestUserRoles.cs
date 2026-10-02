using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace OptiCli.Mcp.Integration.Support;

/// <summary>
/// Takes a test user's role away and gives it back, or disables their account and enables it again, in the test site's
/// own ASP.NET Identity tables: Alloy's user admin has no API an HTTP client could use. The connection string is the test site's own, read from its appsettings, so
/// no other database is ever touched. McpFixture also restores every test user's roles and account when the site starts.
/// </summary>
internal sealed class TestUserRoles(string connectionString)
{
    public static TestUserRoles ForTestSite()
    {
        foreach (var file in new[] { "appsettings.Development.json", "appsettings.json" })
        {
            var path = Path.Combine(McpSiteSettings.SiteDirectory, file);
            if (!File.Exists(path))
            {
                continue;
            }
            using var settings = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (settings.RootElement.TryGetProperty("ConnectionStrings", out var strings) && strings.TryGetProperty("EPiServerDB", out var value))
            {
                return new TestUserRoles(value.GetString()!);
            }
        }
        throw new InvalidOperationException($"No EPiServerDB connection string in {McpSiteSettings.SiteDirectory}/appsettings*.json.");
    }

    /// <summary>Removes <paramref name="user"/> from <paramref name="role"/>; dispose the result to add them back.</summary>
    public async Task<IAsyncDisposable> RemoveAsync(string user, string role)
    {
        var removed = await ExecuteAsync("""
            DELETE ur FROM AspNetUserRoles ur
            JOIN AspNetUsers u ON u.Id = ur.UserId JOIN AspNetRoles r ON r.Id = ur.RoleId
            WHERE u.UserName = @user AND r.Name = @role
            """, user, role);
        if (removed != 1)
        {
            throw new InvalidOperationException($"{user} wasn't in {role}: is this the MCP test site's database?");
        }
        return new Restore(this, user, role);
    }

    /// <summary>Disables <paramref name="user"/>'s account (not approved, as the CMS's user admin does); dispose the result to enable it again.</summary>
    public async Task<IAsyncDisposable> DisableAsync(string user)
    {
        if (await ExecuteAsync("UPDATE AspNetUsers SET IsApproved = 0 WHERE UserName = @user AND IsApproved = 1", user, "") != 1)
        {
            throw new InvalidOperationException($"{user} wasn't an enabled user: is this the MCP test site's database?");
        }
        return new Enable(this, user);
    }

    private async Task<int> ExecuteAsync(string sql, string user, string role)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@user", user);
        command.Parameters.AddWithValue("@role", role);
        return await command.ExecuteNonQueryAsync();
    }

    private sealed class Enable(TestUserRoles roles, string user) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await roles.ExecuteAsync("UPDATE AspNetUsers SET IsApproved = 1 WHERE UserName = @user", user, "");
    }

    private sealed class Restore(TestUserRoles roles, string user, string role) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await roles.ExecuteAsync("""
            INSERT INTO AspNetUserRoles (UserId, RoleId)
            SELECT u.Id, r.Id FROM AspNetUsers u, AspNetRoles r
            WHERE u.UserName = @user AND r.Name = @role
              AND NOT EXISTS (SELECT 1 FROM AspNetUserRoles x WHERE x.UserId = u.Id AND x.RoleId = r.Id)
            """, user, role);
    }
}
