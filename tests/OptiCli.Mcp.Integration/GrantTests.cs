using System.Net;
using OptiCli.Mcp.Integration.Support;

namespace OptiCli.Mcp.Integration;

/// <summary>
/// A connection (a grant) after sign-in: refresh tokens rotate, revoking it cuts the client off at once, and an editor
/// who loses their role is caught at the next refresh.
/// </summary>
/// <remarks>
/// A refresh is made to happen by giving the SDK client a spoiled access token, which the site answers with 401
/// <c>invalid_token</c>, as it would an expired one: then the tests need no short-lived tokens or waiting.
/// </remarks>
[Collection(McpSiteCollection.Name)]
public sealed class GrantTests
{
    private const string Spoiled = "oc_spoiled";

    [McpSiteFact]
    public async Task Refresh_tokens_rotate_and_a_used_one_is_refused()
    {
        await using var session = await McpSession.ConnectAsync(TestUsers.Editor);
        var first = session.Tokens.Current!;
        Assert.StartsWith("oc_", first.AccessToken);
        Assert.StartsWith("ocr_", first.RefreshToken);

        session.Tokens.ReplaceAccessToken(Spoiled);
        session.AllowSignIn = false;
        Assert.Equal(TestUsers.Editor, (await session.OkAsync("whoami")).GetProperty("name").GetString());
        Assert.Equal([HttpStatusCode.OK], session.Traffic.TokenRequests("refresh_token"));
        var second = session.Tokens.Current!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.Equal(1, session.Browser.SignIns);

        var (again, rotated) = await session.RefreshAsync(second.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.NotEqual(second.RefreshToken, rotated.GetProperty("refresh_token").GetString());
        Assert.Equal("content:read content:write content:publish", rotated.GetProperty("scope").GetString());
        Assert.Equal("Bearer", rotated.GetProperty("token_type").GetString());
        Assert.True(rotated.GetProperty("expires_in").GetInt32() > 0);

        // The one before the current refresh token, used again after it was rotated out (RFC 9700): it leaked, or the
        // client lost track. The site can't tell, so the whole connection ends, the newest tokens with it.
        var (status, reused) = await session.RefreshAsync(second.RefreshToken!);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_grant"), (status, reused.GetProperty("error").GetString()));
        Assert.Contains("revoked", reused.GetProperty("error_description").GetString());
        var (newest, _) = await session.RefreshAsync(rotated.GetProperty("refresh_token").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, newest);
        using (var old = await session.PostMcpAsync(rotated.GetProperty("access_token").GetString()))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        }
    }

    [McpSiteFact]
    public async Task A_refresh_for_fewer_scopes_gets_a_token_for_those_only()
    {
        await using var session = await McpSession.ConnectAsync(TestUsers.Editor);
        var (status, narrowed) = await session.RefreshAsync(session.Tokens.Current!.RefreshToken!, "content:read");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("content:read", narrowed.GetProperty("scope").GetString());
        session.Tokens.ReplaceAccessToken(narrowed.GetProperty("access_token").GetString()!);
        session.AllowSignIn = false;
        var refused = await session.ErrorAsync("create_content", new { type = "StandardPage", name = "x", parent = "1", dryRun = true });
        Assert.Equal("missingScope", refused.GetProperty("reason").GetString());
        // The connection keeps what the editor approved.
        var (_, full) = await session.RefreshAsync(narrowed.GetProperty("refresh_token").GetString()!);
        Assert.Equal("content:read content:write content:publish", full.GetProperty("scope").GetString());
    }

    [McpSiteFact]
    public async Task Revoking_a_connection_on_the_connections_page_cuts_the_client_off_at_once()
    {
        await using var session = await McpSession.ConnectAsync(TestUsers.Editor);
        await session.OkAsync("whoami");
        var tokens = session.Tokens.Current!;
        using (var before = await session.PostMcpAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }
        var page = await session.Browser.ConnectionsAsync(CancellationToken.None);
        Assert.Contains($"<code>{tokens.ClientId}</code>", page);

        Assert.Equal(1, await session.Browser.RevokeAsync(tokens.ClientId!, CancellationToken.None));

        // The access token, an hour from expiring, stops working on its next use.
        using (var after = await session.PostMcpAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
            Assert.Contains("error=\"invalid_token\"", after.Headers.WwwAuthenticate.ToString());
        }
        var (status, refused) = await session.RefreshAsync(tokens.RefreshToken!);
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_grant"), (status, refused.GetProperty("error").GetString()));
        // The client finds its token refused and its refresh too: only signing in again would help.
        session.AllowSignIn = false;
        var cutOff = await Assert.ThrowsAnyAsync<Exception>(() => session.CallAsync("whoami"));
        Assert.Contains("The client tried to sign in again", SignInTests.Flatten(cutOff));
        Assert.DoesNotContain($"<code>{tokens.ClientId}</code>", await session.Browser.ConnectionsAsync(CancellationToken.None));
    }

    [McpSiteFact]
    public async Task An_editor_whose_account_was_disabled_gets_no_new_token_and_the_connection_is_deleted()
    {
        await using var session = await McpSession.ConnectAsync(TestUsers.Product);
        var tokens = session.Tokens.Current!;
        await using (await TestUserRoles.ForTestSite().DisableAsync(TestUsers.Product))
        {
            // The roles are unchanged: it's the account the refresh catches.
            var (status, refused) = await session.RefreshAsync(tokens.RefreshToken!);
            Assert.Equal((HttpStatusCode.BadRequest, "invalid_grant"), (status, refused.GetProperty("error").GetString()));
            Assert.Contains("disabled", refused.GetProperty("error_description").GetString());
            using var old = await session.PostMcpAsync(tokens.AccessToken);
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        }
    }

    [McpSiteFact]
    public async Task An_editor_who_lost_their_role_gets_no_new_token_at_the_next_refresh_and_cannot_connect_again()
    {
        await using var session = await McpSession.ConnectAsync(TestUsers.Product);
        await session.OkAsync("whoami");
        var tokens = session.Tokens.Current!;

        await using (await TestUserRoles.ForTestSite().RemoveAsync(TestUsers.Product, "ProductEditors"))
        {
            // The access token keeps the roles of its sign-in until it expires (AccessTokenLifetime): by design.
            Assert.Equal(TestUsers.Product, (await session.OkAsync("whoami")).GetProperty("name").GetString());

            // At the refresh, the role is looked up again: no token, and the connection is deleted.
            session.Tokens.ReplaceAccessToken(Spoiled);
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => session.CallAsync("whoami"));
            Assert.Equal([HttpStatusCode.BadRequest], session.Traffic.TokenRequests("refresh_token"));
            using (var old = await session.PostMcpAsync(tokens.AccessToken))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
            }

            // The client then tries to connect again, and the browser still has the login cookie from before, with the
            // role in it: the consent page checks the role as it is now.
            Assert.Contains("No access", SignInTests.Flatten(refused));
            Assert.Equal(1, session.Browser.SignIns);
        }

        // With the role back, the editor can connect again.
        await using var again = await McpSession.ConnectAsync(TestUsers.Product);
        Assert.Equal(TestUsers.Product, (await again.OkAsync("whoami")).GetProperty("name").GetString());
    }
}
