using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using EPiServer.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Preview;
using OptiCli.Mcp.Tests.Support;
using OptiCli.Mcp.Tools;

namespace OptiCli.Mcp.Tests;

/// <summary>
/// The assistant's preview links without a CMS: the signed payload, and the middleware in front of a fake CMS preview
/// that records what reached it. What the CMS itself renders is the end-to-end tests' (PreviewLinkTests there).
/// </summary>
public sealed class PreviewLinkTests
{
    private const string Version = "123_456";

    private const string PreviewPath = "/EPiServer/CMS/Content/en/about-us,,123_456/";

    [Fact]
    public void A_link_names_one_version_in_one_language_for_one_connection_and_expires()
    {
        var links = Links(TimeProvider.System);
        var request = new DefaultHttpContext { Request = { Scheme = "https", Host = new HostString("editors.example.com") } }.Request;

        var (url, expires) = links.Issue(request, "grant-1", "editor", Version, "en");
        var (other, _) = links.Issue(request, "grant-1", "editor", Version, "en");

        Assert.StartsWith("https://editors.example.com/episerver/opticli/preview/", url, StringComparison.Ordinal);
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
        var link = links.Read(Token(url))!;
        Assert.Equal(("grant-1", "editor", Version, "en", "editors.example.com"), (link.GrantId, link.UserName, link.Version, link.Language, link.Host));
        Assert.True(PreviewLinks.IsFor(link, new HostString("EDITORS.example.com")));
        Assert.False(PreviewLinks.IsFor(link, new HostString("evil.example.com")));
        Assert.False(PreviewLinks.IsFor(link, new HostString("editors.example.com:8443")));
        Assert.Equal(expires.ToUnixTimeSeconds(), link.Expires.ToUnixTimeSeconds());
        // Every link is its own, and none carries anything readable or the CMS's preview token.
        Assert.NotEqual(url, other);
        Assert.DoesNotContain("editor", Token(url));
        Assert.DoesNotContain("preview_token", url);
        Assert.Equal("", links.Read(Token(links.Issue(request, "grant-1", "editor", "7_8", null).Url))!.Language);
    }

    [Fact]
    public void A_tampered_truncated_expired_or_foreign_payload_is_refused()
    {
        var protection = new EphemeralDataProtectionProvider();
        var links = Links(TimeProvider.System, protection);
        var request = new DefaultHttpContext { Request = { Scheme = "http", Host = new HostString("localhost") } }.Request;
        var token = Token(links.Issue(request, "grant-1", "editor", Version, "en").Url);
        Assert.NotNull(links.Read(token));

        var middle = token.Length / 2;
        Assert.Null(links.Read(token[..middle] + (token[middle] == 'A' ? 'B' : 'A') + token[(middle + 1)..]));
        Assert.Null(links.Read(token[..10] + (token[10] == 'A' ? 'B' : 'A') + token[11..]));
        Assert.Null(links.Read(token[..^8]));
        Assert.Null(links.Read(""));
        Assert.Null(links.Read("not a link"));
        Assert.Null(links.Read(new string('A', PreviewLinks.MaxLength + 1)));

        // Issued longer ago than it lasts.
        var past = new ManualTime();
        past.Advance(DateTimeOffset.UtcNow - past.GetUtcNow() - TimeSpan.FromMinutes(11));
        Assert.Null(links.Read(Token(Links(past, protection).Issue(request, "grant-1", "editor", Version, "en").Url)));

        // The same payload, protected for another purpose (an access token's), or naming content rather than a version.
        string Payload(string version) => JsonSerializer.Serialize(new { g = "grant-1", u = "editor", v = version, l = "en", h = "localhost", iat = 0, n = "x" });
        var later = DateTimeOffset.UtcNow.AddMinutes(5);
        Assert.Null(links.Read(protection.CreateProtector("OptiCli.Mcp.AccessToken.v1").ToTimeLimitedDataProtector().Protect(Payload(Version), later)));
        var ours = protection.CreateProtector("OptiCli.Mcp.PreviewLink.v1").ToTimeLimitedDataProtector();
        Assert.NotNull(links.Read(ours.Protect(Payload(Version), later)));
        Assert.Null(links.Read(ours.Protect(Payload("123"), later)));
        Assert.Null(links.Read(ours.Protect(Payload("not a ref"), later)));
        // An access token isn't a preview link either.
        var tokens = new TokenService(protection, new InMemoryOAuthStore(), new GrantCache(TimeProvider.System), Options.Create(new OptiCliMcpOptions()), TimeProvider.System);
        var (access, _) = tokens.Issue(new Grant
        {
            GrantId = "grant-1", ClientId = "c", ClientName = "c", UserName = "editor", Roles = [], Scope = Scopes.Read, Resource = "r",
            RefreshHash = "h", Created = DateTimeOffset.UtcNow, RefreshIssued = DateTimeOffset.UtcNow, Expires = DateTimeOffset.UtcNow.AddDays(1),
        });
        Assert.Null(links.Read(access));
        Assert.Null(links.Read(access[TokenService.Prefix.Length..]));
    }

    [Fact]
    public async Task The_preview_runs_as_the_editor_on_the_cms_preview_path_without_the_requests_credentials_and_sets_no_cookie()
    {
        await using var preview = await PreviewSite.StartAsync();
        var link = await preview.LinkAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, link);
        request.Headers.Add("Cookie", ".AspNetCore.Identity.Application=someone-elses");
        request.Headers.Add("Authorization", "preview_token abc");
        request.Headers.Add("Proxy-Authorization", "Basic abc");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("X-Api-Key", "someone-elses");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("Accept-Language", "sv");
        // A form a GET could carry, which a site's model binding would read whatever the method.
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = "anything" });

        using var response = await preview.Site.Client().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<h1>The draft</h1>", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType!.ToString());

        var got = preview.Downstream.Got!;
        // The CMS's preview of the version the link names, with its own query and nothing of the request's.
        Assert.Equal((PreviewPath, "?epieditmode=false"), (got.Path, got.Query));
        Assert.Equal(PreviewPath + "?epieditmode=false", got.RawTarget);
        Assert.DoesNotContain("Cookie", got.Headers);
        Assert.DoesNotContain("Authorization", got.Headers);
        Assert.DoesNotContain("Proxy-Authorization", got.Headers);
        Assert.DoesNotContain("X-Requested-With", got.Headers);
        Assert.DoesNotContain("X-Api-Key", got.Headers);
        Assert.DoesNotContain("Content-Type", got.Headers);
        Assert.Contains("Accept-Language", got.Headers);
        Assert.Contains("X-Forwarded-Proto", got.Headers);
        Assert.Equal(0, got.Cookies);
        Assert.Equal((false, 0), (got.HasForm, got.BodyLength));
        // The editor of the link's connection, never an MCP editor.
        Assert.Equal(("editor", PreviewLinkMiddleware.AuthenticationType), (got.User, got.AuthenticationType));
        Assert.False(got.IsMcpEditor);
        var (version, language, editor) = Assert.Single(preview.Versions.Calls);
        Assert.Equal((ContentReference.Parse(Version), "en"), (version, language));
        Assert.Equal(("editor", PreviewLinkMiddleware.AuthenticationType), (editor.Identity!.Name, editor.Identity.AuthenticationType));
        Assert.True(editor.IsInRole("WebEditors"));
        // Once rendered, the request is nobody's again.
        Assert.False(preview.Outer.AuthenticatedAfter);
    }

    [Fact]
    public async Task Without_another_login_the_preview_is_the_editors_and_never_an_mcp_editors()
    {
        await using var preview = await PreviewSite.StartAsync();
        var link = await preview.LinkAsync();

        using var response = await preview.Site.Client().GetAsync(link);
        using var head = await preview.Site.Client().SendAsync(new HttpRequestMessage(HttpMethod.Head, link));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var got = preview.Downstream.Got!;
        Assert.Equal(("editor", PreviewLinkMiddleware.AuthenticationType, true), (got.User, got.AuthenticationType, got.Roles.Contains("WebEditors")));
        Assert.False(got.IsMcpEditor);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal("<h1>The draft</h1>".Length, head.Content.Headers.ContentLength);
        Assert.False(head.Headers.Contains("Set-Cookie"));
        // Audited with the version, the editor and the client, never the link.
        var audit = string.Join("\n", preview.Site.Audit.Audit);
        Assert.Contains("MCP preview 123_456 en: user editor, client mcp_test, ok 200", audit);
        Assert.DoesNotContain(link.Split('/').Last(), audit);
    }

    [Fact]
    public async Task A_body_written_to_the_pipe_is_sent_whole()
    {
        await using var preview = await PreviewSite.StartAsync();
        preview.Downstream.Respond = async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes(new string('x', 100_000)));
        };

        using var response = await preview.Site.Client().GetAsync(await preview.LinkAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100_000, (await response.Content.ReadAsStringAsync()).Length);
    }

    [Theory]
    [InlineData(302, 404)]
    [InlineData(301, 404)]
    [InlineData(401, 404)]
    [InlineData(403, 404)]
    [InlineData(404, 404)]
    [InlineData(500, 500)]
    public async Task A_redirect_challenge_refusal_or_error_from_the_site_is_never_passed_on(int status, int expected)
    {
        await using var preview = await PreviewSite.StartAsync();
        preview.Downstream.Respond = async context =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Location = "https://elsewhere.example.com/login?ReturnUrl=%2Fdraft";
            context.Response.Headers.WWWAuthenticate = "preview_token";
            context.Response.Cookies.Append("session", "secret");
            await context.Response.WriteAsync("the site's own page, with secret details");
        };

        using var response = await preview.Site.Client().GetAsync(await preview.LinkAsync());

        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret", body);
        Assert.Equal(expected == 500 ? PreviewLinkMiddleware.Failed : PreviewLinkMiddleware.NotValid, body);
        Assert.Contains($"refused: the site answered {status}", string.Join("\n", preview.Site.Audit.Audit));
    }

    [Fact]
    public async Task The_sites_own_error_page_for_a_refused_preview_is_not_passed_on_either()
    {
        await using var preview = await PreviewSite.StartAsync(statusCodePages: true);
        preview.Downstream.Respond = context =>
        {
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        };

        using var response = await preview.Site.Client().GetAsync(await preview.LinkAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(PreviewLinkMiddleware.NotValid, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_exception_in_the_preview_is_answered_by_the_module_not_by_error_handling_outside_it()
    {
        // The CMS UI adds its error handling (the developer exception page, or its own error pages) through a startup
        // filter registered before the module's: outside the module's middleware.
        await using var preview = await PreviewSite.StartAsync(outerErrorPage: true);
        preview.Downstream.Respond = _ => throw new InvalidOperationException("secret details of the draft's view");

        using var response = await preview.Site.Client().GetAsync(await preview.LinkAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(PreviewLinkMiddleware.Failed, await response.Content.ReadAsStringAsync());
        Assert.False(preview.Outer.AuthenticatedAfter);
        Assert.Contains("failed: InvalidOperationException", string.Join("\n", preview.Site.Audit.Audit));
        // Logged for the site's developers, as an error.
        Assert.Contains("Rendering the preview of a draft for an assistant's preview link failed.", preview.Site.Audit.All);
    }

    [Fact]
    public async Task A_preview_larger_than_a_page_is_not_sent()
    {
        await using var preview = await PreviewSite.StartAsync();
        preview.Downstream.Respond = async context =>
        {
            for (var written = 0; written <= PreviewLinkMiddleware.MaxBytes; written += 1024 * 1024)
            {
                await context.Response.Body.WriteAsync(new byte[1024 * 1024]);
            }
        };

        using var response = await preview.Site.Client().GetAsync(await preview.LinkAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(PreviewLinkMiddleware.Failed, await response.Content.ReadAsStringAsync());
        Assert.Contains("failed: larger than 10 MB", string.Join("\n", preview.Site.Audit.Audit));
    }

    [Theory]
    [InlineData("/EPiServer/CMS/Content/en/about-us,,123_456/?epieditmode=false", true)]
    [InlineData("/EPiServer/CMS/Content/contentassets/en/1eb214c8fe7c4987bf9021c7472fc081/,,123_456/?epieditmode=false", true)]
    [InlineData("https://other.example.com/EPiServer/CMS/Content/en/a,,123_456", true)]
    // A shortcut's preview URL is its target's, without a version: the target's latest draft.
    [InlineData("/EPiServer/CMS/Content/en/target,,789/?epieditmode=false", false)]
    [InlineData("/EPiServer/CMS/Content/en/about-us,,123_457/", false)]
    [InlineData("/EPiServer/CMS/Content/en/about-us,,123_456_Other/", false)]
    [InlineData("/en/about-us/", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_preview_url_must_name_the_version_itself(string? url, bool names) =>
        Assert.Equal(names, CmsPreviewVersions.Names(url, ContentReference.Parse(Version)));

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    public async Task Only_get_and_head_are_answered(string method)
    {
        await using var preview = await PreviewSite.StartAsync();

        using var response = await preview.Site.Client().SendAsync(new HttpRequestMessage(new HttpMethod(method), await preview.LinkAsync()));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["GET", "HEAD"], response.Content.Headers.Allow.Order());
        Assert.Null(preview.Downstream.Got);
        Assert.Empty(preview.Versions.Calls);
    }

    [Fact]
    public async Task Only_the_link_as_it_was_given_is_answered()
    {
        await using var preview = await PreviewSite.StartAsync();
        var link = await preview.LinkAsync();

        foreach (var url in new[]
        {
            link + "?preview_token=abc", link + "?epieditmode=true", link + "?x", link + "/extra", link + "x",
            "/episerver/opticli/preview", "/episerver/opticli/preview/", "/EPISERVER/opticli/preview/abc",
        })
        {
            using var response = await preview.Site.Client().GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(PreviewLinkMiddleware.NotValid, await response.Content.ReadAsStringAsync());
        }
        Assert.Null(preview.Downstream.Got);
        Assert.Empty(preview.Versions.Calls);
    }

    [Fact]
    public async Task A_revoked_or_ended_connection_or_another_editors_name_ends_the_link()
    {
        await using var preview = await PreviewSite.StartAsync();
        var site = preview.Site;

        var revoked = await preview.LinkAsync();
        var grant = (await site.Store.ListGrantsAsync("editor", default)).Single();
        await site.Store.DeleteGrantAsync(grant.GrantId, default);
        site.Services.GetRequiredService<GrantCache>().Evict(grant.GrantId);
        Assert.Equal(HttpStatusCode.NotFound, (await site.Client().GetAsync(revoked)).StatusCode);

        // A link that names another editor than its connection's.
        var forged = await preview.LinkAsync(user: "admin", linkUser: "editor");
        Assert.Equal(HttpStatusCode.NotFound, (await site.Client().GetAsync(forged)).StatusCode);

        // The connection's end (ConnectionLifetime), while the link itself would still work.
        var ended = await preview.LinkAsync();
        site.Time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(HttpStatusCode.NotFound, (await site.Client().GetAsync(ended)).StatusCode);

        Assert.Null(preview.Downstream.Got);
        Assert.Contains("refused: connection revoked or ended", string.Join("\n", site.Audit.Audit));
    }

    [Fact]
    public async Task An_expired_link_or_a_version_the_editor_cant_read_shows_nothing()
    {
        await using var preview = await PreviewSite.StartAsync();
        var site = preview.Site;

        // Issued eleven minutes ago, with the default ten.
        site.Time.Advance(TimeSpan.FromMinutes(-11));
        var expired = await preview.LinkAsync();
        site.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.NotFound, (await site.Client().GetAsync(expired)).StatusCode);
        Assert.Empty(preview.Versions.Calls);

        preview.Versions.Readable = false;
        using var response = await site.Client().GetAsync(await preview.LinkAsync());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(PreviewLinkMiddleware.NotValid, await response.Content.ReadAsStringAsync());
        Assert.Null(preview.Downstream.Got);
        Assert.Contains("refused: version not readable", string.Join("\n", site.Audit.Audit));
    }

    [Fact]
    public async Task Turned_off_the_path_is_the_sites_like_any_other()
    {
        // On by default; false turns it off.
        Assert.True(new OptiCliMcpOptions().AllowPreviewLinks);
        await using var preview = await PreviewSite.StartAsync(o => o.AllowPreviewLinks = false);

        // Even a link the module would have made is just a path the site has nothing on.
        using var response = await preview.Site.Client().GetAsync(await preview.LinkAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("X-Robots-Tag"));
        Assert.Empty(preview.Versions.Calls);
    }

    [Fact]
    public async Task On_a_site_that_requires_a_host_only_that_host_answers()
    {
        await using var preview = await PreviewSite.StartAsync(o => o.RequireHost = "editors.example.com");
        var link = new Uri(await preview.LinkAsync(host: "editors.example.com")).PathAndQuery;

        using var other = await preview.Site.Client().GetAsync(link);
        using var editors = await preview.Site.Client("editors.example.com").GetAsync(link);

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.False(other.Headers.Contains("X-Robots-Tag"));
        Assert.Equal(HttpStatusCode.OK, editors.StatusCode);
    }

    [Theory]
    [InlineData("editors.example.com", "editors.example.com", true)]
    [InlineData("EDITORS.example.com:8443", "editors.example.com", true)]
    [InlineData("editors.example.com:8443", "editors.example.com:8443", true)]
    [InlineData("editors.example.com", "editors.example.com:8443", false)]
    [InlineData("editors.example.com:443", "editors.example.com:8443", false)]
    [InlineData("www.example.com", "editors.example.com", false)]
    public void The_host_matches_as_endpoint_routing_matches_it(string actual, string required, bool matches) =>
        Assert.Equal(matches, PreviewLinkMiddleware.HostMatches(new HostString(actual), required));

    [Fact]
    public async Task Opened_links_are_rate_limited_per_connection_and_what_isnt_a_link_per_address_apart_from_them()
    {
        await using var preview = await PreviewSite.StartAsync(limits: new OptiCliMcpRateLimits { PreviewPerMinute = 1, InvalidPreviewPerAddressPerMinute = 2 });
        var first = await preview.LinkAsync();
        var second = await preview.LinkAsync();
        var client = preview.Site.Client();

        // Garbage from the same address (an assistant provider's shared one) uses up its own window, not the links'.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/episerver/opticli/preview/garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(first + "x")).StatusCode);
        using var garbage = await client.GetAsync("/episerver/opticli/preview/garbage");
        Assert.Equal(HttpStatusCode.TooManyRequests, garbage.StatusCode);
        Assert.True(garbage.Headers.Contains("Retry-After"));
        Assert.True(garbage.Headers.CacheControl!.NoStore);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(first)).StatusCode);
        using var limited = await client.GetAsync(first);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("for this connection", await limited.Content.ReadAsStringAsync());
        // Another connection, from the same address, has its own window.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(second)).StatusCode);
    }

    [Fact]
    public async Task The_sites_own_forwarded_headers_decide_the_address_and_host_before_anything_else()
    {
        await using var preview = await PreviewSite.StartAsync(
            o => o.RequireHost = "editors.example.com",
            limits: new OptiCliMcpRateLimits { InvalidPreviewPerAddressPerMinute = 1 },
            forwardedHeaders: f =>
            {
                f.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
                // The test server has no client address; a site lists its proxies here.
                f.KnownNetworks.Clear();
                f.KnownProxies.Clear();
            });
        var link = new Uri(await preview.LinkAsync(host: "editors.example.com")).PathAndQuery;
        HttpRequestMessage Forwarded(string url, string address) => new(HttpMethod.Get, url)
        {
            Headers = { { "X-Forwarded-Host", "editors.example.com" }, { "X-Forwarded-For", address }, { "X-Forwarded-Proto", "https" } },
        };

        // The proxy's own host is localhost: the forwarded one is what RequireHost and the link's host are checked against.
        using var response = await preview.Site.Client().SendAsync(Forwarded(link, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var got = preview.Downstream.Got!;
        Assert.Equal(("editors.example.com", "https"), (got.Host, got.Scheme));
        Assert.DoesNotContain("X-Forwarded-Host", got.Headers);

        // Each client behind the proxy has its own window for what isn't a link.
        Assert.Equal(HttpStatusCode.NotFound, (await preview.Site.Client().SendAsync(Forwarded("/episerver/opticli/preview/x", "203.0.113.1"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await preview.Site.Client().SendAsync(Forwarded("/episerver/opticli/preview/x", "203.0.113.1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await preview.Site.Client().SendAsync(Forwarded("/episerver/opticli/preview/x", "203.0.113.2"))).StatusCode);
    }

    [Fact]
    public async Task Forwarded_headers_applied_before_the_module_are_not_applied_again()
    {
        // As on DXP (AddCmsCloudPlatformSupport) or with ASPNETCORE_FORWARDEDHEADERS_ENABLED: a pass before the module,
        // trusting any proxy. A second pass would take the entry the client wrote itself as its address.
        await using var preview = await PreviewSite.StartAsync(
            limits: new OptiCliMcpRateLimits { InvalidPreviewPerAddressPerMinute = 1 },
            forwardedHeaders: TrustAnyProxy,
            earlierForwardedHeaders: true);
        HttpRequestMessage Garbage(string forwardedFor) => new(HttpMethod.Get, "/episerver/opticli/preview/garbage") { Headers = { { "X-Forwarded-For", forwardedFor } } };

        using var first = await preview.Site.Client().SendAsync(Garbage("10.9.0.1, 9.9.9.9"));
        using var second = await preview.Site.Client().SendAsync(Garbage("10.9.0.2, 9.9.9.9"));
        using var other = await preview.Site.Client().SendAsync(Garbage("10.9.0.3, 9.9.9.8"));

        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        // Both from 9.9.9.9, whatever they claim before it.
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        // Refusals over the window aren't audited one by one.
        Assert.Equal(2, preview.Site.Audit.Audit.Count(m => m.Contains("refused: not a valid link", StringComparison.Ordinal)));
        // A link opens as before.
        var link = await preview.LinkAsync();
        using var opened = await preview.Site.Client().SendAsync(new HttpRequestMessage(HttpMethod.Get, link) { Headers = { { "X-Forwarded-For", "10.9.0.4, 9.9.9.9" } } });
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
    }

    [Fact]
    public async Task A_forged_sign_of_an_earlier_pass_gains_the_client_nothing()
    {
        // Only the module's own pass, and the client claims one ran already (X-Original-*): the module then checks the
        // connection's own address and host, which the client doesn't choose.
        await using var preview = await PreviewSite.StartAsync(
            limits: new OptiCliMcpRateLimits { InvalidPreviewPerAddressPerMinute = 1 },
            forwardedHeaders: TrustAnyProxy);
        var link = new Uri(await preview.LinkAsync(host: "editors.example.com")).PathAndQuery;
        HttpRequestMessage Forged(string url, string forwardedFor) => new(HttpMethod.Get, url)
        {
            Headers = { { "X-Forwarded-For", forwardedFor }, { "X-Forwarded-Host", "editors.example.com" }, { "X-Original-For", "198.51.100.1" } },
        };

        // The garbage all counts against the connection's own address, however the forwarded address varies.
        Assert.Equal(HttpStatusCode.NotFound, (await preview.Site.Client().SendAsync(Forged("/episerver/opticli/preview/x", "203.0.113.1"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await preview.Site.Client().SendAsync(Forged("/episerver/opticli/preview/x", "203.0.113.2"))).StatusCode);

        // And the link's host is checked against the request's own (localhost), not the forwarded one it was issued on.
        await using var fresh = await PreviewSite.StartAsync(forwardedHeaders: TrustAnyProxy);
        var issued = new Uri(await fresh.LinkAsync(host: "editors.example.com")).PathAndQuery;
        using var refused = await fresh.Site.Client().SendAsync(Forged(issued, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Null(fresh.Downstream.Got);
        Assert.Contains("refused: issued for another host", string.Join("\n", fresh.Site.Audit.Audit));
        // Without the forged header, the same request is the forwarded one, and opens.
        using var forwarded = await fresh.Site.Client().SendAsync(new HttpRequestMessage(HttpMethod.Get, issued) { Headers = { { "X-Forwarded-Host", "editors.example.com" } } });
        Assert.Equal(HttpStatusCode.OK, forwarded.StatusCode);
    }

    /// <summary>Forwarded headers trusted from any proxy, as DXP's are (and the test server has no client address).</summary>
    private static void TrustAnyProxy(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto;
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    }

    [Fact]
    public void The_sites_own_forwarded_headers_after_the_module_find_nothing_more_to_do()
    {
        var options = Options.Create(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.All });
        options.Value.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
        var forwarded = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, options);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        context.Request.Host = new HostString("internal:8080");
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        context.Request.Headers["X-Forwarded-Host"] = "editors.example.com";
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        forwarded.ApplyForwarders(context);
        var once = (context.Connection.RemoteIpAddress.ToString(), context.Request.Host.Value, context.Request.Scheme);
        forwarded.ApplyForwarders(context);

        Assert.Equal(("203.0.113.9", "editors.example.com", "https"), once);
        Assert.Equal(once, (context.Connection.RemoteIpAddress.ToString(), context.Request.Host.Value, context.Request.Scheme));
        Assert.False(context.Request.Headers.ContainsKey("X-Forwarded-For"));
        Assert.False(context.Request.Headers.ContainsKey("X-Forwarded-Host"));
    }

    [Fact]
    public async Task A_link_opens_only_on_the_host_it_was_issued_on()
    {
        await using var preview = await PreviewSite.StartAsync();
        var link = new Uri(await preview.LinkAsync()).PathAndQuery;

        using var other = await preview.Site.Client("evil.example.com").GetAsync(link);
        using var otherPort = await preview.Site.Client("localhost:8080").GetAsync(link);
        using var same = await preview.Site.Client("LOCALHOST").GetAsync(link);

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(PreviewLinkMiddleware.NotValid, await other.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, otherPort.StatusCode);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Contains("refused: issued for another host", string.Join("\n", preview.Site.Audit.Audit));
    }

    [Theory]
    [InlineData("/episerver/opticli/preview/abc-_09XYZ", "abc-_09XYZ")]
    // Other spellings the Data Protection would decode to the same bytes, and other paths.
    [InlineData("/episerver/opticli/preview/abc+_09XYZ", null)]
    [InlineData("/episerver/opticli/preview/abc-/09XYZ", null)]
    [InlineData("/episerver/opticli/preview/abc-_09XYZ==", null)]
    [InlineData("/episerver/opticli/PREVIEW/abc-_09XYZ", null)]
    [InlineData("/EPiServer/opticli/preview/abc-_09XYZ", null)]
    // Percent-encoding is undone by the server, for this path as for any.
    [InlineData("/episerver/opticli/preview/abc%2D_09XYZ", "abc-_09XYZ")]
    [InlineData("/episerver/opticli/preview/", null)]
    [InlineData("/episerver/opticli/preview", null)]
    public void A_payload_is_one_segment_in_the_modules_own_spelling(string path, string? token)
    {
        var request = new DefaultHttpContext().Request;
        request.Path = PathString.FromUriComponent(path);
        Assert.Equal(token, PreviewLinkMiddleware.Token(request, new OptiCliMcpOptions()));
        request.QueryString = new QueryString("?x=1");
        Assert.Null(PreviewLinkMiddleware.Token(request, new OptiCliMcpOptions()));
    }

    [Fact]
    public async Task Another_spelling_of_a_real_link_is_refused()
    {
        await using var preview = await PreviewSite.StartAsync();
        var link = await preview.LinkAsync();
        var token = Token(link);
        var at = link.Length - token.Length;
        var spellings = new List<string> { link + "==", link[..at].Replace("/preview/", "/PREVIEW/", StringComparison.Ordinal) + token };
        if (token.IndexOfAny(['-', '_']) is var i and >= 0)
        {
            spellings.Add(link[..(at + i)] + (token[i] == '-' ? "+" : "/") + token[(i + 1)..]);
        }

        foreach (var url in spellings)
        {
            using var response = await preview.Site.Client().GetAsync(url);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.Null(preview.Downstream.Got);
    }

    [Fact]
    public async Task A_failure_before_the_render_is_the_modules_own_500_never_the_sites_error_page()
    {
        await using var preview = await PreviewSite.StartAsync(outerErrorPage: true);
        preview.Versions.Throw = true;

        using var response = await preview.Site.Client().GetAsync(await preview.LinkAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(PreviewLinkMiddleware.Failed, await response.Content.ReadAsStringAsync());
        Assert.False(preview.Outer.AuthenticatedAfter);
        Assert.Contains("MCP preview 123_456 en: user editor, client mcp_test, failed: InvalidOperationException", string.Join("\n", preview.Site.Audit.Audit));
        Assert.Contains("Answering an assistant's preview link failed.", preview.Site.Audit.All);
    }

    [Fact]
    public async Task The_sites_callbacks_as_the_response_starts_dont_run_as_the_editor()
    {
        await using var preview = await PreviewSite.StartAsync(outerErrorPage: true);
        bool? signedIn = null;
        preview.Downstream.Respond = async context =>
        {
            context.Response.OnStarting(() =>
            {
                signedIn = context.User.Identity?.IsAuthenticated;
                throw new InvalidOperationException("a site's callback failed");
            });
            await context.Response.WriteAsync("<h1>The draft</h1>");
        };

        try
        {
            using var response = await preview.Site.Client().GetAsync(await preview.LinkAsync());
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(PreviewLinkMiddleware.Failed, await response.Content.ReadAsStringAsync());
        }
        catch (Exception e) when (e is not Xunit.Sdk.XunitException)
        {
            // Or, once the response had started, the connection is cut (the test server hands the client the
            // exception): never the site's error page.
        }
        // A cut connection reaches the client before the server is done with the request.
        for (var wait = 0; wait < 100 && preview.Outer.AuthenticatedAfter is null; wait++)
        {
            await Task.Delay(20);
        }
        Assert.False(signedIn);
        Assert.Null(preview.Outer.Escaped);
        Assert.False(preview.Outer.AuthenticatedAfter);
    }

    [Theory]
    [InlineData("/EPiServer/CMS/Content/en/a,,5_6/?epieditmode=false", "", "/EPiServer/CMS/Content/en/a,,5_6/", "?epieditmode=false")]
    // An absolute URL (content on another site's host) keeps the request's host: only its path and query are used.
    [InlineData("https://other.example.com/EPiServer/CMS/Content/en/a,,5_6/?epieditmode=false", "", "/EPiServer/CMS/Content/en/a,,5_6/", "?epieditmode=false")]
    // Under a base path, the CMS's URL has it, and the request has it already.
    [InlineData("/app/EPiServer/CMS/Content/en/a,,5_6/", "/app", "/EPiServer/CMS/Content/en/a,,5_6/", "")]
    [InlineData("/EPiServer/CMS/Content/sv/%C3%A5%C3%A4,,5_6/", "", "/EPiServer/CMS/Content/sv/åä,,5_6/", "")]
    public void The_request_becomes_the_preview_url(string url, string pathBase, string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = pathBase;
        context.Request.Path = "/episerver/opticli/preview/abc";
        context.Request.QueryString = new QueryString("?x=1");
        context.Request.Headers.Cookie = "a=b";
        context.Request.Headers.Authorization = "Bearer x";

        Assert.True(PreviewLinkMiddleware.Rewrite(context.Request, url));

        Assert.Equal((path, query), (context.Request.Path.Value, context.Request.QueryString.Value ?? ""));
        Assert.False(context.Request.Headers.ContainsKey("Cookie"));
        Assert.False(context.Request.Headers.ContainsKey("Authorization"));
        Assert.Equal(context.Request.PathBase.Add(context.Request.Path).ToUriComponent() + query, context.Features.Get<IHttpRequestFeature>()!.RawTarget);
        Assert.False(PreviewLinkMiddleware.Rewrite(context.Request, ""));
    }

    [Fact]
    public async Task The_lifetime_is_from_a_minute_to_an_hour_and_ten_minutes_by_default()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), new OptiCliMcpOptions().PreviewLinkLifetime);
        Assert.Null(new OptiCliMcpOptions { PreviewLinkLifetime = TimeSpan.FromMinutes(1) }.Problem());
        Assert.Null(new OptiCliMcpOptions { PreviewLinkLifetime = TimeSpan.FromHours(1) }.Problem());
        Assert.Contains("PreviewLinkLifetime", new OptiCliMcpOptions { PreviewLinkLifetime = TimeSpan.FromSeconds(59) }.Problem());
        Assert.Contains("PreviewLinkLifetime", new OptiCliMcpOptions { PreviewLinkLifetime = TimeSpan.FromMinutes(61) }.Problem());
        Assert.Contains("every limit must be positive", new OptiCliMcpOptions { RateLimits = new() { PreviewPerMinute = 0 } }.Problem());

        await using var site = await TestSite.StartAsync(configuration: new()
        {
            ["OptiCli:Mcp:AllowPreviewLinks"] = "false",
            ["OptiCli:Mcp:PreviewLinkLifetime"] = "00:05:00",
        });
        Assert.Equal((false, TimeSpan.FromMinutes(5)), (site.Options.AllowPreviewLinks, site.Options.PreviewLinkLifetime));
    }

    private static PreviewLinks Links(TimeProvider time, IDataProtectionProvider? protection = null) =>
        new(protection ?? new EphemeralDataProtectionProvider(), Options.Create(new OptiCliMcpOptions()), time);

    private static string Token(string url) => url[(url.LastIndexOf('/') + 1)..];

    /// <summary>The CMS's preview, faked: the version is readable unless the test says otherwise.</summary>
    private sealed class FakeVersions : IPreviewVersions
    {
        public bool Readable { get; set; } = true;

        /// <summary>When set, the CMS fails (its database, say).</summary>
        public bool Throw { get; set; }

        public List<(ContentReference Version, string? Language, ClaimsPrincipal Editor)> Calls { get; } = [];

        public string? PreviewUrl(ContentReference version, string? language, ClaimsPrincipal editor)
        {
            Calls.Add((version, language, editor));
            if (Throw)
            {
                throw new InvalidOperationException("The database is gone.");
            }
            return Readable ? PreviewPath + "?epieditmode=false" : null;
        }
    }

    /// <summary>What reached the site's preview endpoint.</summary>
    private sealed record Received(string Host, string Scheme, string Path, string Query, string? RawTarget, HashSet<string> Headers, int Cookies, bool HasForm, long BodyLength, string? User, string? AuthenticationType, string[] Roles, bool IsMcpEditor);

    /// <summary>The CMS's preview endpoint, faked: it records the request and answers as the test says, setting a cookie.</summary>
    private sealed class Downstream
    {
        public Received? Got { get; private set; }

        public Func<HttpContext, Task> Respond { get; set; } = async context =>
        {
            context.Response.Cookies.Append("EPiStateMarker", "true");
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.Append("Set-Cookie", "late=1; path=/");
                return Task.CompletedTask;
            });
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("<h1>The draft</h1>");
        };

        public async Task HandleAsync(HttpContext context)
        {
            var user = context.User;
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            Got = new Received(
                context.Request.Host.Value!,
                context.Request.Scheme,
                context.Request.Path.Value!,
                context.Request.QueryString.Value ?? "",
                context.Features.Get<IHttpRequestFeature>()?.RawTarget,
                context.Request.Headers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                context.Request.Cookies.Count,
                context.Request.HasFormContentType,
                body.Length,
                user.Identity?.Name,
                user.Identity?.AuthenticationType,
                user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
                McpEditor.Of(context) is not null);
            await Respond(context);
        }
    }

    /// <summary>
    /// Middleware outside the module's, as a startup filter registered before it adds it (the CMS UI's error handling):
    /// it records whether the request was still signed in once the module was done, and may show an error page or apply
    /// the forwarded headers before the module.
    /// </summary>
    private sealed class Outer(bool errorPage, bool forwardedHeaders) : IStartupFilter
    {
        public bool? AuthenticatedAfter { get; private set; }

        /// <summary>An exception that came out of the module's middleware.</summary>
        public Exception? Escaped { get; private set; }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                try
                {
                    await inner(context);
                }
                catch (Exception e)
                {
                    Escaped = e;
                    throw;
                }
                finally
                {
                    AuthenticatedAfter = context.User.Identity?.IsAuthenticated;
                }
            });
            if (forwardedHeaders)
            {
                // The proxy's connection, which a real server always has and the test server doesn't.
                app.Use((context, inner) =>
                {
                    context.Connection.RemoteIpAddress ??= IPAddress.Parse("10.0.0.1");
                    return inner(context);
                });
                app.UseForwardedHeaders();
            }
            if (errorPage)
            {
                app.UseExceptionHandler(new ExceptionHandlerOptions
                {
                    ExceptionHandler = context => context.Response.WriteAsync($"The site's error page, for {context.User.Identity?.Name}"),
                });
            }
            next(app);
        };
    }

    /// <summary>A test site with preview links on, the fake CMS preview mapped where the CMS has it, and the clock at now.</summary>
    private sealed class PreviewSite : IAsyncDisposable
    {
        public required TestSite Site { get; init; }

        public required FakeVersions Versions { get; init; }

        public required Downstream Downstream { get; init; }

        public required Outer Outer { get; init; }

        public static async Task<PreviewSite> StartAsync(Action<OptiCliMcpOptions>? configure = null, OptiCliMcpRateLimits? limits = null, bool statusCodePages = false, bool outerErrorPage = false,
            Action<ForwardedHeadersOptions>? forwardedHeaders = null, bool earlierForwardedHeaders = false)
        {
            var versions = new FakeVersions();
            var downstream = new Downstream();
            var outer = new Outer(outerErrorPage, earlierForwardedHeaders);
            var site = await TestSite.StartAsync(
                // Preview links are on by default.
                configure,
                limits: limits,
                services: s =>
                {
                    s.AddSingleton<IPreviewVersions>(versions);
                    s.AddSingleton<IStartupFilter>(outer);
                    if (forwardedHeaders is not null)
                    {
                        s.Configure(forwardedHeaders);
                    }
                },
                statusCodePages: statusCodePages,
                endpoints: app => app.Map("/EPiServer/CMS/Content/{**rest}", downstream.HandleAsync));
            // The link's own expiry is the Data Protection's, on the real clock.
            site.Time.Advance(DateTimeOffset.UtcNow - site.Time.GetUtcNow());
            return new PreviewSite { Site = site, Versions = versions, Downstream = downstream, Outer = outer };
        }

        /// <summary>A connection of <paramref name="user"/>'s, and a link for it that names <paramref name="linkUser"/>, issued on <paramref name="host"/>.</summary>
        public async Task<string> LinkAsync(string user = "editor", string? linkUser = null, string host = "localhost")
        {
            var now = Site.Time.GetUtcNow();
            var grant = new Grant
            {
                GrantId = Secrets.New(), ClientId = "mcp_test", ClientName = "Test client", UserName = user,
                Roles = user == "admin" ? ["WebAdmins"] : ["WebEditors"], Scope = "content:read content:write",
                Resource = "http://localhost/episerver/opticli/mcp", RefreshHash = Secrets.Hash(Secrets.New()),
                Created = now, RefreshIssued = now, Expires = now.AddDays(30),
            };
            await Site.Store.AddGrantAsync(grant, default);
            var request = new DefaultHttpContext { Request = { Scheme = "http", Host = new HostString(host) } }.Request;
            return Site.Services.GetRequiredService<PreviewLinks>().Issue(request, grant.GrantId, linkUser ?? user, Version, "en").Url;
        }

        public ValueTask DisposeAsync() => Site.DisposeAsync();
    }
}
