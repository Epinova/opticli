using System.Net;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Tests.OAuth;

public class PkceTests
{
    // Computed independently: base64url(SHA-256(ASCII(verifier))), no padding.
    private const string Verifier = "dBjftJeZ4CVP-mJ92K1hJLf5MvFPgfwV8NxqlJbo8YA";
    private const string Challenge = "YwBwYNpsa-XZrshS7MfaDoPayEGmRh2dosOHBKjMokU";

    [Fact]
    public void The_challenge_is_base64url_sha256_of_the_verifier() => Assert.Equal(Challenge, Pkce.Challenge(Verifier));

    [Fact]
    public void The_right_verifier_matches() => Assert.True(Pkce.Matches(Verifier, Challenge));

    [Fact]
    public void Another_verifier_does_not_match() => Assert.False(Pkce.Matches(Verifier[..^1] + "B", Challenge));

    [Theory]
    [InlineData("short")]
    [InlineData("dBjftJeZ4CVP-mJ92K1hJLf5MvFPgfwV8NxqlJbo8Y")] // 42 characters
    [InlineData("dBjftJeZ4CVP-mJ92K1hJLf5MvFPgfwV8NxqlJbo8YA dBjftJeZ4CVP")] // a space
    public void A_malformed_verifier_never_matches(string verifier) => Assert.False(Pkce.Matches(verifier, Pkce.Challenge(verifier)));

    [Fact]
    public void A_verifier_longer_than_128_characters_never_matches()
    {
        var verifier = new string('a', 129);
        Assert.False(Pkce.Matches(verifier, Pkce.Challenge(verifier)));
    }

    [Fact]
    public void The_plain_method_is_not_accepted_as_a_challenge() =>
        // A plain challenge is the verifier itself: 43 to 128 characters, rarely exactly a SHA-256's 43.
        Assert.False(Pkce.IsValidChallenge(Verifier + "x"));

    [Theory]
    [InlineData(Challenge, true)]
    [InlineData("YwBwYNpsa-XZrshS7MfaDoPayEGmRh2dosOHBKjMok", false)]
    [InlineData("YwBwYNpsa+XZrshS7MfaDoPayEGmRh2dosOHBKjMokU", false)]
    public void Challenges_are_43_base64url_characters(string challenge, bool valid) => Assert.Equal(valid, Pkce.IsValidChallenge(challenge));
}

public class SecretsTests
{
    [Fact]
    public void Secrets_are_random_and_url_safe()
    {
        var a = Secrets.New();
        Assert.Equal(43, a.Length);
        Assert.NotEqual(a, Secrets.New());
        Assert.DoesNotContain(a, c => c is '+' or '/' or '=');
    }

    [Fact]
    public void Hashes_are_sha256_hex() =>
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", Secrets.Hash("hello"));
}

public class RedirectUrisTests
{
    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("http://127.0.0.1:53682/callback", true)]
    [InlineData("http://localhost:1234/callback", true)]
    [InlineData("http://[::1]:1234/callback", true)]
    [InlineData("http://example.com/callback", false)]
    [InlineData("https://example.com/callback#fragment", false)]
    [InlineData("https://user:pass@example.com/callback", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("myapp://callback", false)]
    [InlineData("/relative", false)]
    public void Only_https_or_loopback_http_may_be_registered(string uri, bool allowed) => Assert.Equal(allowed, RedirectUris.IsAllowed(uri));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://claude.ai/api/mcp/auth_callback/", false)]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://claude.ai/api/mcp/auth_callback?x=1", false)]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://claude.ai:8443/api/mcp/auth_callback", false)]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "https://CLAUDE.ai/api/mcp/auth_callback", false)]
    [InlineData("https://claude.ai/cb", "https://claude.ai.evil.example/cb", false)]
    [InlineData("https://claude.ai/cb", "https://claude.ai/cb@evil.example", false)]
    [InlineData("http://127.0.0.1:53682/callback", "http://127.0.0.1:61000/callback", true)]
    [InlineData("http://127.0.0.1/callback", "http://127.0.0.1:61000/callback", true)]
    [InlineData("http://localhost:53682/callback", "http://localhost:4000/callback", true)]
    [InlineData("http://127.0.0.1:53682/callback", "http://127.0.0.1:61000/other", false)]
    [InlineData("http://127.0.0.1:53682/callback", "http://localhost:53682/callback", false)]
    [InlineData("http://127.0.0.1:53682/callback", "https://127.0.0.1:53682/callback", false)]
    [InlineData("http://127.0.0.1:53682/callback", "http://127.0.0.1:61000/callback?x=1", false)]
    [InlineData("https://example.com/callback", "https://example.com:444/callback", false)]
    public void Matching_is_exact_except_for_a_loopback_port(string registered, string requested, bool matches) =>
        Assert.Equal(matches, RedirectUris.Matches(registered, requested));

    [Fact]
    public void An_empty_redirect_never_matches() => Assert.False(RedirectUris.AnyMatches(["https://claude.ai/cb"], ""));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("https://claude.com/api/mcp/auth_callback", true)]
    [InlineData("https://Claude.AI/api/mcp/auth_callback", true)]
    [InlineData("http://127.0.0.1:53682/callback", true)]
    [InlineData("https://127.0.0.1:53682/callback", true)]
    [InlineData("http://localhost:4000/cb", true)]
    [InlineData("http://[::1]:4000/cb", true)]
    [InlineData("https://attacker.example/cb", false)]
    [InlineData("https://claude.ai.attacker.example/cb", false)]
    [InlineData("https://www.claude.ai/cb", false)]
    [InlineData("https://claude.ai./cb", false)]
    [InlineData("http://claude.ai/cb", false)]
    [InlineData("https://claude.ai/cb#x", false)]
    public void Codes_go_to_the_listed_hosts_over_https_or_to_the_editors_own_machine(string uri, bool permitted) =>
        Assert.Equal(permitted, RedirectUris.IsPermitted(uri, ["claude.ai", "claude.com"]));

    [Fact]
    public void The_wildcard_permits_any_https_host_but_still_not_plain_http()
    {
        Assert.True(RedirectUris.IsPermitted("https://attacker.example/cb", ["*"]));
        Assert.False(RedirectUris.IsPermitted("http://attacker.example/cb", ["*"]));
        Assert.True(RedirectUris.IsPermitted("http://127.0.0.1/cb", []));
        Assert.False(RedirectUris.IsPermitted("https://claude.ai/cb", []));
    }
}

public class NetworkAddressesTests
{
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("160.79.104.10", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.1.2.3", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata services
    [InlineData("100.64.0.1", false)] // CGNAT
    [InlineData("100.127.255.255", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:127.0.0.1", false)] // IPv4-mapped
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::a9fe:a9fe", false)] // NAT64 of 169.254.169.254
    [InlineData("64:ff9b::808:808", true)] // NAT64 of 8.8.8.8
    [InlineData("2002:0a00:0001::1", false)] // 6to4 of 10.0.0.1
    [InlineData("2001:0:4136:e378::1", false)] // Teredo
    [InlineData("::10.0.0.1", false)] // IPv4-compatible
    public void Only_public_addresses_are_public(string address, bool isPublic) =>
        Assert.Equal(isPublic, NetworkAddresses.IsPublic(IPAddress.Parse(address)));
}

public class ScopesTests
{
    private static readonly OptiCliMcpOptions NoPublish = new();
    private static readonly OptiCliMcpOptions WithPublish = new() { AllowPublish = true };

    [Fact]
    public void No_scope_asked_for_gets_everything_the_site_offers()
    {
        Assert.Equal("content:read content:write", Scopes.Grantable(null, NoPublish));
        Assert.Equal("content:read content:write content:publish", Scopes.Grantable("", WithPublish));
    }

    [Fact]
    public void Publish_is_left_out_on_a_site_without_publishing() =>
        Assert.Equal("content:read content:write", Scopes.Grantable("content:publish content:write content:read", NoPublish));

    [Fact]
    public void Unknown_scopes_are_left_out() => Assert.Equal("content:read", Scopes.Grantable("openid content:read admin", NoPublish));

    [Fact]
    public void Nothing_offered_is_nothing() => Assert.Equal("", Scopes.Grantable("content:publish", NoPublish));
}
