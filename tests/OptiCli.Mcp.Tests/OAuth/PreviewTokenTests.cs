using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tests.Support;

namespace OptiCli.Mcp.Tests.OAuth;

/// <summary>
/// A CMS preview token is never the sign-in for the consent and connections pages. The site here has a stand-in for the
/// CMS's preview token handler as CMS 12.22 and later have it: a request with a token is signed in as the editor it
/// names, whatever it asks for, because the CMS makes its handler the default scheme for such a request.
/// </summary>
public sealed class PreviewTokenTests : IAsyncLifetime
{
    private TestSite _site = null!;

    public async Task InitializeAsync() => _site = await TestSite.StartAsync(services: PreviewTokenLogin.Add);

    public async Task DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task The_consent_page_refuses_a_preview_token_that_would_sign_the_editor_in()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var url = TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()));

        // The editor signed in with the site's own login gets the page...
        Assert.Equal(HttpStatusCode.OK, (await _site.Browser("editor").GetAsync(url)).StatusCode);
        // ...but a link with a preview token, which the CMS would take as the editor's sign-in, doesn't.
        var response = await _site.Browser(null).GetAsync(url + "&preview_token=editor");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Not with a preview link", html);
        Assert.DoesNotContain("name=\"decision\"", html);
    }

    [Fact]
    public async Task Allowing_a_connection_with_a_preview_token_is_refused_and_issues_no_code()
    {
        var (clientId, _) = await _site.RegisterAsync();
        var url = TestSite.AuthorizeUrl(clientId, Pkce.Challenge(TestSite.Verifier()));
        // The form as the editor's own page has it, posted with a token in the header instead of the site's login.
        var browser = _site.Browser("editor");
        var html = await (await browser.GetAsync(url)).Content.ReadAsStringAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, Browser.FormAction(html)) { Content = new FormUrlEncodedContent(Browser.Inputs(html, "allow")) };
        request.Headers.Authorization = new AuthenticationHeaderValue("preview_token", "editor");

        var response = await browser.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Not with a preview link", await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.Location);
        Assert.DoesNotContain(_site.Audit.Audit, line => line.Contains("allowed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_connections_page_refuses_a_preview_token_for_showing_and_revoking()
    {
        var tokens = await _site.ConnectAsync();
        var grant = Assert.Single(await _site.Store.ListGrantsAsync("editor", default));

        var shown = await _site.Browser(null).GetAsync("/episerver/opticli/connections?preview_token=editor");
        Assert.Equal(HttpStatusCode.BadRequest, shown.StatusCode);
        Assert.DoesNotContain(tokens.ClientId, await shown.Content.ReadAsStringAsync());

        var request = new HttpRequestMessage(HttpMethod.Post, "/episerver/opticli/connections")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant"] = grant.GrantId }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Preview_Token", "editor");
        Assert.Equal(HttpStatusCode.BadRequest, (await _site.Browser(null).SendAsync(request)).StatusCode);
        Assert.NotNull(await _site.Store.FindGrantAsync(grant.GrantId, default));
    }

    [Theory]
    [InlineData("/x?preview_token=abc", null, true)]
    [InlineData("/x?a=1&preview_token=abc", null, true)]
    [InlineData("/x", "preview_token abc", true)]
    [InlineData("/x", "PREVIEW_TOKEN abc", true)]
    [InlineData("/x?preview_token=", null, false)]
    [InlineData("/x?preview_token=", "preview_token abc", true)]
    [InlineData("/x?previewtoken=abc", null, false)]
    [InlineData("/x", "Bearer abc", false)]
    [InlineData("/x", null, false)]
    public void A_preview_token_is_found_where_the_cms_looks_for_one(string url, string? authorization, bool found)
    {
        var context = new DefaultHttpContext();
        var uri = new Uri("http://localhost" + url);
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        Assert.Equal(found, PreviewTokens.InRequest(context.Request));
    }
}

/// <summary>
/// A stand-in for the CMS's preview token handler (CMS 12.22 and later): the token is the editor's name, any request
/// with one is signed in as them, and the scheme is the default for such a request, as the CMS's scheme provider makes it.
/// </summary>
internal sealed class PreviewTokenLogin(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public new const string Scheme = "Preview-token";

    public static void Add(IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PreviewTokenLogin>(Scheme, null);
        services.AddHttpContextAccessor();
        services.AddSingleton<IAuthenticationSchemeProvider, Schemes>();
    }

    private static string? Token(HttpRequest? request)
    {
        if (request is null)
        {
            return null;
        }
        if (request.Query["preview_token"].ToString() is { Length: > 0 } query)
        {
            return query;
        }
        return AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header)
            && header.Scheme.Equals("preview_token", StringComparison.OrdinalIgnoreCase) ? header.Parameter : null;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(Token(Request) is { } user
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "Identity.Application")), Scheme))
            : AuthenticateResult.NoResult());

    private sealed class Schemes(IOptions<AuthenticationOptions> options, IHttpContextAccessor http) : AuthenticationSchemeProvider(options)
    {
        public override Task<AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() =>
            Token(http.HttpContext?.Request) is null ? base.GetDefaultAuthenticateSchemeAsync() : GetSchemeAsync(Scheme);

        public override Task<AuthenticationScheme?> GetDefaultChallengeSchemeAsync() =>
            Token(http.HttpContext?.Request) is null ? base.GetDefaultChallengeSchemeAsync() : GetSchemeAsync(Scheme);
    }
}
