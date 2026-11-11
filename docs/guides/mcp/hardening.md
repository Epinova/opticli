---
title: Take the MCP module to production
summary: Configure the MCP module for a shared or production environment, from which assistants may connect and how the site is reached to error pages, uploads, the audit log and preview links.
audience: mcp
order: 3
written-for: 0.30.0
---

On your machine the module works with its defaults. A shared test environment or production adds a proxy or load
balancer, a public host name, web assistants calling from their own cloud, the site's own security middleware and
people who need to know who did what. This guide goes through each of those. At the end you have a site where only the
assistants you accept can connect, the module answers on the right host, its errors and pages are left intact, and
every call is in the log. A checklist sums it up.

## Before you start

- The module installed and working locally: see [Install the MCP module in your site](install-the-module.md).
- You know how the site is hosted: IIS or Kestrel, behind which proxy or load balancer, on DXP or not.
- You know which assistants your editors will use: desktop apps (Claude Desktop with the extension, Claude Code) or
  web assistants (claude.ai, Claude's custom connectors).

## Harden the site

### 1. Choose which assistants may connect

Anyone can register an app with the module, and an app names itself. What stops a stranger from registering an app
called "Claude" with a return address of their own is `AllowedRedirectHosts`: the hosts an app's OAuth redirect URI may
be on. The default is `claude.ai` and `claude.com`. Hosts match exactly, over https only, with no subdomains or
wildcards, and the module checks the list at registration and again at every sign-in.

A loopback address on the editor's own machine is always allowed, on any port. That covers desktop apps, Claude Code
and the Claude Desktop extension, which connect from the editor's machine.

If your editors only use desktop apps, allow nothing else:

```csharp
services.AddOptiCliMcp(o => o.AllowedRedirectHosts = []);
```

To accept another web assistant, list its host. In configuration a list replaces the default one, so keep Claude's
hosts if you still want them:

```json
{"OptiCli": {"Mcp": {"AllowedRedirectHosts": ["claude.ai", "claude.com", "assistant.example.com"]}}}
```

`["*"]` lets any https host through and logs a warning at startup. Use it only if the site knowingly accepts any web
app. While you are at it, narrow `AllowedRoles` to the roles that should connect an assistant at all.

### 2. Make the site answer on the right host

The module builds its OAuth issuer, resource and metadata URLs from the request's host and scheme, as the site's login
does. Behind a proxy that means ASP.NET Core's forwarded headers must be applied, limited to your known proxies.
Without them every request seems to come from the proxy: all editors share one rate-limit window, and the assistant's
preview links answer 404 when the proxy changes the host.

Put the settings in the services, where the module can read them, not only in a call to `UseForwardedHeaders(options)`:

```csharp
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownProxies.Add(IPAddress.Parse("10.0.0.10"));   // your proxy
});
```

`ASPNETCORE_FORWARDEDHEADERS_ENABLED`, or `AddCmsCloudPlatformSupport()` on DXP called before `AddOptiCliMcp()`,
applies them early enough too. Behind a proxy that sends `X-Original-Host` (or `-For`, `-Proto`), apply them before
the module in one of those two ways.

Then limit the host names the module answers on. `RequireHost` keeps it on one host, such as an editors' host; the
site's own `AllowedHosts` limits the whole site. For a host of its own, set `BasePath = ""` together with
`RequireHost = "mcp.example.com"`. The issuer then has no path, which suits clients that don't handle one.

### 3. Open the right doors for web assistants

Web assistants call the site from their provider's cloud, not from the editor's browser. Claude's custom connectors
(claude.ai, Claude Desktop without the extension, mobile) need the site to be publicly reachable. A site that is only
on a VPN or split DNS can't be used with them; its editors use the desktop extension instead.

If you restrict `/episerver` by IP address, allow Anthropic's outbound range, `160.79.104.0/21`, for the MCP endpoint
(`/episerver/opticli/mcp`), the `/.well-known` metadata documents and the `register` and `token` endpoints. The login,
`authorize` and the connections page open in the editor's own browser, so they need what the CMS login needs.

The site also needs outbound HTTPS: for a client with a metadata document, as Claude has, the module fetches that
document from the client's own URL. claude.ai calls `register` and `token` from addresses all its users share; if many
editors hit 429 there, raise `RateLimits.TokenPerAddressPerMinute`. Rate limits count per instance.

### 4. Run it beside another MCP server

A site can run another MCP server built on the MCP C# SDK, its own or Optimizely's `Optimizely.Cms.Opal.Tools` on
CMS 13. Register it after `AddOptiCliMcp()` and map it at a path of its own:

```csharp
services.AddOptiCliMcp();
services.AddMcpServer().WithHttpTransport();   // the site's own server, after the module
// ...
endpoints.MapMcp("/mcp");                     // its own path
endpoints.MapOptiCliMcp();
```

Each endpoint then serves only its own server's tools, name and instructions. Registered before `AddOptiCliMcp()`, the
other server's settings lose to the module's, and with legacy SSE the site fails at startup. Don't replace the SDK's
`ConfigureSessionOptions` hook in a `PostConfigure` after the module: the module's endpoint then turns itself off and
the log says why. The details are under [Beside another MCP server](https://github.com/Epinova/opticli#beside-another-mcp-server).

### 5. Leave error pages and security headers to the module

The module switches off the site's status code pages for its own requests, so its 401 keeps the `WWW-Authenticate`
header that tells the client where to sign in. Check that nothing in front of the site rewrites errors either.

The consent and connections pages send their own `X-Frame-Options: DENY`, `Content-Security-Policy` and
`Referrer-Policy`. Middleware that sets the site's security headers as the response starts replaces them, and
`frame-ancestors` only works as a header. Have such middleware leave `/episerver/opticli` alone, or set only what the
response doesn't have yet:

```csharp
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.TryAdd("X-Frame-Options", "SAMEORIGIN");
        context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
        return Task.CompletedTask;
    });
    await next();
});
```

`X-Content-Type-Options: nosniff` on media is worth having anyway: a browser then never takes an uploaded file for
anything but what its content type says. On CMS 12.15 or newer, also have the CMS's script parser remove script on
every save, the edit UI's included: `"EPiServer": {"Cms": {"ScriptParser": {"SavingMode": "Remove"}}}`.

### 6. Allow larger uploads on IIS

Assistants may upload media up to `MaxUploadBytes`, 10 MB by default and 50 MB at most. The CMS's own upload limit
applies too, whichever is lower (`EPiServer:CmsUI:Upload:FileSizeLimit` on CMS 12, `EPiServer:Cms:MediaFile:FileSizeLimit`
on CMS 13). Uploads travel as base64, so IIS's `maxAllowedContentLength` (30,000,000 bytes by default) stops anything
past about 20 MB. Raise it in `web.config` when you raise `MaxUploadBytes`; the README's
[Uploads on IIS](https://github.com/Epinova/opticli#uploads-on-iis) has the snippet.

### 7. Bound connections and keep the audit log

An access token works for `AccessTokenLifetime` (1 hour), and the editor's roles and account are checked again at
every refresh. For an external login (Entra ID, Opti ID) the CMS never hears that an account was disabled, so set
`ConnectionLifetime` shorter than its 30-day default; 1 to 7 days is a better choice. After it, the editor connects
again through the site's login, where the identity provider has its say.

```json
{"OptiCli": {"Mcp": {"ConnectionLifetime": "7.00:00:00"}}}
```

Every authorization decision, token, revocation, tool call and opened preview link is logged in the
`OptiCli.Mcp.Audit` category at Information level, never with tokens, links, secrets or property values. A site that
logs Warning and up drops them unless you raise that category:

```json
{"Logging": {"LogLevel": {"OptiCli.Mcp.Audit": "Information"}}}
```

At `/episerver/opticli/connections` editors see and revoke their own connections; WebAdmins and CmsAdmins see and
revoke everyone's. Tell your administrators it is there.

### 8. Decide on preview links

Every write result gives the editor `editUrl` and `previewUrl`, the CMS's own links, which open only for someone signed
in to the CMS. They carry no CMS preview token, and the module never makes one: from CMS 12.22 such a token signs
whoever holds it in as the editor, on any request to the site, for its few minutes.

So that the assistant can check how a draft looks, the module gives it a link of its own, `assistantPreviewUrl`. It
shows one version of one item, works for `PreviewLinkLifetime` (10 minutes), answers GET and HEAD on its own host only,
and stops when the connection ends. But the page renders as the editor: editor-only output (navigation, a quick
navigator, user-specific content) is in the HTML for whoever holds the link, and the site's own code for that page
runs in the editor's name. If that is more than you want to give away, turn it off:

```json
{"OptiCli": {"Mcp": {"AllowPreviewLinks": false}}}
```

## Checklist

- `AllowedRedirectHosts` lists only the web assistants you accept, or is empty for desktop-only use; never `["*"]`
  without a reason.
- `AllowedRoles` names only the roles that should connect an assistant.
- `AllowPublish` and `AllowDelete` are on only where you decided they should be.
- Forwarded headers are applied for your known proxies, in the services, before the module needs them.
- `RequireHost` or the site's `AllowedHosts` limits the host names.
- Web assistants can reach the MCP endpoint, metadata, `register` and `token`; IP filters allow `160.79.104.0/21`.
- The site can make outbound HTTPS requests.
- Another MCP server, if any, is registered after `AddOptiCliMcp()` and mapped at its own path.
- Security header middleware leaves `/episerver/opticli` alone or only adds missing headers; media gets `nosniff`.
- The CMS's script parser removes script on save (CMS 12.15 or newer).
- `maxAllowedContentLength` on IIS matches `MaxUploadBytes`.
- `ConnectionLifetime` is 1 to 7 days on a site with external logins.
- `OptiCli.Mcp.Audit` logs at Information, and the logs are kept somewhere you can search.
- `AllowPreviewLinks` is a decision, not a default you didn't look at.
- Every instance of a load-balanced site shares the Data Protection keys.

## If something goes wrong

- **"Return address not allowed" on the consent page:** the app's redirect host isn't in `AllowedRedirectHosts`. Add
  it only if you accept that app.
- **A web assistant can't connect, but Claude Code can:** the site isn't reachable from the provider's cloud, or an IP
  filter blocks `160.79.104.0/21`.
- **Every editor gets 429 at once:** forwarded headers aren't applied, so all requests share the proxy's address.
- **Preview links answer 404 behind a proxy:** the forwarded headers are only passed to `UseForwardedHeaders(options)`.
  Configure them in the services.
- **The client gets a 405 instead of a 401:** something outside the module rewrites the error; the module's own
  requests are already exempt from the site's status code pages.
- **Uploads past about 20 MB fail on IIS before they reach the site:** raise `maxAllowedContentLength`. A 413 from the
  module itself means the file is over `MaxUploadBytes`.

## Related

- [Install the MCP module in your site](install-the-module.md)
- [Write your first site-specific tool](custom-tools.md)
- [Connect your assistant](../editors/connect-your-assistant.md), for editors
- README: [Security model](https://github.com/Epinova/opticli#security-model),
  [Options](https://github.com/Epinova/opticli#options),
  [Connecting an assistant](https://github.com/Epinova/opticli#connecting-an-assistant),
  [Error pages and security headers](https://github.com/Epinova/opticli#error-pages-and-security-headers),
  [Connections and audit log](https://github.com/Epinova/opticli#connections-and-audit-log)
