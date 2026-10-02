using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tests.Support;

namespace OptiCli.Mcp.Tests.OAuth;

public class TokenServiceTests
{
    private const string Resource = "https://cms.example/episerver/opticli/mcp";

    private readonly InMemoryOAuthStore _store = new();
    private readonly ManualTime _time = new();
    private readonly GrantCache _cache;
    private readonly TokenService _tokens;
    private readonly Grant _grant;

    public TokenServiceTests()
    {
        _cache = new GrantCache(_time);
        _tokens = Tokens(new EphemeralDataProtectionProvider());
        _grant = new Grant
        {
            GrantId = Secrets.New(),
            ClientId = "mcp_client",
            ClientName = "Test client",
            UserName = "editor",
            Roles = ["WebEditors"],
            Scope = "content:read content:write",
            Resource = Resource,
            RefreshHash = Secrets.Hash("refresh"),
            Created = _time.GetUtcNow(),
            Expires = _time.GetUtcNow().AddDays(30),
        };
        _store.SaveGrantAsync(_grant, default).Wait();
    }

    private TokenService Tokens(IDataProtectionProvider protection) =>
        new(protection, _store, _cache, Options.Create(new OptiCliMcpOptions()), _time);

    [Fact]
    public async Task A_token_validates_as_the_editor()
    {
        var (token, expiresIn) = _tokens.Issue(_grant);
        Assert.StartsWith("oc_", token);
        Assert.Equal(3600, expiresIn);
        var principal = await _tokens.ValidateAsync(token, Resource, default);
        Assert.Equal("editor", principal!.Identity!.Name);
        Assert.True(principal.IsInRole("WebEditors"));
        Assert.Equal(_grant.GrantId, principal.FindFirstValue(McpClaims.Grant));
        Assert.Equal("content:read content:write", principal.FindFirstValue(McpClaims.Scope));
    }

    [Fact]
    public void The_token_is_opaque()
    {
        var (token, _) = _tokens.Issue(_grant);
        Assert.DoesNotContain(_grant.GrantId, token);
        Assert.DoesNotContain("editor", token);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (token, _) = _tokens.Issue(_grant);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Null(await _tokens.ValidateAsync(token, Resource, default));
    }

    [Fact]
    public async Task A_token_for_another_resource_is_refused()
    {
        var (token, _) = _tokens.Issue(_grant);
        Assert.Null(await _tokens.ValidateAsync(token, "https://other.example/episerver/opticli/mcp", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("oc_")]
    [InlineData("oc_not-a-protected-payload")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiJlZGl0b3IifQ.")]
    public async Task Malformed_tokens_are_refused(string token) => Assert.Null(await _tokens.ValidateAsync(token, Resource, default));

    [Fact]
    public async Task A_tampered_token_is_refused()
    {
        var (token, _) = _tokens.Issue(_grant);
        var tampered = token[..^4] + (token[^4] == 'A' ? 'B' : 'A') + token[^3..];
        Assert.Null(await _tokens.ValidateAsync(tampered, Resource, default));
    }

    [Fact]
    public async Task A_token_protected_with_other_keys_is_refused()
    {
        var (token, _) = Tokens(new EphemeralDataProtectionProvider()).Issue(_grant);
        Assert.Null(await _tokens.ValidateAsync(token, Resource, default));
    }

    [Fact]
    public async Task A_deleted_grant_revokes_its_tokens_once_evicted()
    {
        var (token, _) = _tokens.Issue(_grant);
        Assert.NotNull(await _tokens.ValidateAsync(token, Resource, default));
        await _store.DeleteGrantAsync(_grant.GrantId, default);
        _cache.Evict(_grant.GrantId);
        Assert.Null(await _tokens.ValidateAsync(token, Resource, default));
    }

    [Fact]
    public async Task Another_instance_notices_a_deleted_grant_within_30_seconds()
    {
        var (token, _) = _tokens.Issue(_grant);
        Assert.NotNull(await _tokens.ValidateAsync(token, Resource, default));
        await _store.DeleteGrantAsync(_grant.GrantId, default); // as if on another instance: no eviction here
        Assert.NotNull(await _tokens.ValidateAsync(token, Resource, default));
        _time.Advance(GrantCache.Lifetime);
        Assert.Null(await _tokens.ValidateAsync(token, Resource, default));
    }

    [Fact]
    public async Task An_expired_grant_refuses_a_live_token()
    {
        var shortGrant = _grant with { GrantId = Secrets.New(), Expires = _time.GetUtcNow().AddMinutes(10) };
        await _store.SaveGrantAsync(shortGrant, default);
        var (token, _) = _tokens.Issue(shortGrant);
        _time.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(await _tokens.ValidateAsync(token, Resource, default));
    }

    [Fact]
    public async Task Last_use_is_written_at_most_once_a_minute()
    {
        var (token, _) = _tokens.Issue(_grant);
        for (var i = 0; i < 5; i++)
        {
            await _tokens.ValidateAsync(token, Resource, default);
            _time.Advance(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(1, _store.Touches);
        _time.Advance(TimeSpan.FromSeconds(20));
        await _tokens.ValidateAsync(token, Resource, default);
        Assert.Equal(2, _store.Touches);
        Assert.Equal(_time.GetUtcNow(), (await _store.FindGrantAsync(_grant.GrantId, default))!.LastUsed);
    }
}
