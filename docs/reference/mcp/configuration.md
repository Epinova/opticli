# Configuring the MCP module

## Options

The options are read from the `OptiCli:Mcp` configuration section, then from the delegate passed to `AddOptiCliMcp`,
which wins. In configuration, an `AllowedRoles` or `AllowedRedirectHosts` list replaces the default list.

| Option | Default | Meaning |
|---|---|---|
| `BasePath` | `/episerver/opticli` | where the module lives: the MCP endpoint is `{BasePath}/mcp`, the OAuth issuer `{origin}{BasePath}`; `""` for a host of its own (with `RequireHost`) |
| `RequireHost` | none | only answer on this host name (`host` or `host:port`), e.g. an editors' host |
| `AllowedRoles` | WebEditors, WebAdmins, CmsEditors, CmsAdmins, Administrators | who may connect an assistant at all; the CMS's access rights then decide per item |
| `AllowedRedirectHosts` | claude.ai, claude.com | the hosts an app's return address (OAuth redirect URI) may be on, https only, matched exactly (no subdomains or wildcards); a loopback address on the editor's own machine is always allowed. `["*"]` allows any https host, and logs a warning at startup (see below) |
| `AccessTokenLifetime` | 1 hour | how long an access token works; also how long a removed role can keep working |
| `RefreshTokenLifetime` | 30 days | how long a connection survives unused; each refresh extends it |
| `ConnectionLifetime` | 30 days | the longest a connection lasts, however often it is used; then the editor connects again, through the site's login (see [Security model](security.md#security-model)) |
| `RefreshTokenReuseGrace` | 1 minute | how long after a refresh the refresh token it replaced, sent again by the same client, is only refused; later (or from another client) it revokes the connection |
| `AllowPublish` | `false` | let assistants publish, unpublish and schedule publishing, and publish, schedule and reactivate [projects](tools.md#projects-for-editors-assistants) (the `content:publish` scope) |
| `AllowDelete` | `false` | let assistants delete content, always to the recycle bin |
| `AllowPreviewLinks` | `true` | give the assistant a short-lived link of its own to a draft's preview, which it can open without a sign-in (`assistantPreviewUrl`, see [Security model](security.md#security-model)); `false` turns it off |
| `PreviewLinkLifetime` | 10 minutes | how long such a link works (1 minute to 1 hour) |
| `MaxUploadBytes` | 10 MB | the largest media file an assistant may upload (at most 50 MB); the CMS UI's own upload limit applies too, if it is lower (see [Security model](security.md#security-model) and [Uploads on IIS](install.md#uploads-on-iis)) |
| `MaxPlanSteps` | 100 | the most steps a [custom write tool](tools.md#custom-tools)'s plan may have (1 to 1000) |
| `RateLimits` | see below | requests a minute the OAuth endpoints take |

```json
{"OptiCli": {"Mcp": {"AllowDelete": true, "AllowedRoles": ["WebEditors", "WebAdmins", "ProductEditors"]}}}
```

`AllowedRedirectHosts` is what stops a stranger from connecting an app to an editor's account. Registration is open to
anyone and an app names itself, so without it someone could register an app called "Claude" with a return address of
their own, send a signed-in editor the link to connect it, and receive the code that the editor's Allow sends there.
The module checks the return address when an app registers and again at every sign-in, also for apps with a metadata
document and for apps registered before the list changed. Add a host only for a web app the site knowingly accepts.

`RateLimits` are fixed windows of a minute, counted per instance; over a limit, a request gets 429 with `Retry-After`.
claude.ai calls `register` and `token` from Anthropic's addresses (see [Connecting Claude](connecting.md#connecting-claude)), which every editor of the site who uses
claude.ai shares, so the token endpoint counts per client as well as per address. An IPv6 address counts by its /64,
which one machine usually has whole. Behind a proxy or load balancer, the site needs ASP.NET Core's forwarded headers
set up (as its login usually already does), limited to its known proxies: otherwise every request seems to come from
the proxy, and everyone shares one window.

| `RateLimits.` | Default | Counts |
|---|---|---|
| `TokenPerMinute` | 60 | token requests (code exchanges, refreshes) per client and address |
| `TokenPerAddressPerMinute` | 600 | token requests per address, whatever the client |
| `RegisterPerMinute` | 10 | client registrations per address (clients with a metadata document, as Claude has, don't register) |
| `AuthorizePerMinute` | 60 | the consent page and its post, per address (the editor's browser) |
| `PreviewPerMinute` | 60 | opened preview links (`AllowPreviewLinks`), per connection |
| `InvalidPreviewPerAddressPerMinute` | 120 | requests for preview links that aren't valid (malformed, expired, for another host), per address |

Besides the rate limit, registration stops at 5,000 registered apps that have no connection and no sign-in under way:
`register` then answers 429 until the cleanup has deleted some. An app that registers and doesn't sign in within a day
is deleted. A client that keeps its registration then gets "Unknown app" on the sign-in page until it is removed and
added again. The Claude Desktop extension itself drops a registration over an hour old that never signed in.

```json
{"OptiCli": {"Mcp": {"RateLimits": {"TokenPerAddressPerMinute": 1200}}}}
```

The options are checked at startup, and a site with a bad value doesn't start (`OptiCli:Mcp: …`): lifetimes positive,
`AccessTokenLifetime` at most `RefreshTokenLifetime` and at most `ConnectionLifetime`, `RefreshTokenReuseGrace` from 0
(every reuse revokes) to `AccessTokenLifetime`, rate limits positive.

## Endpoints

With the default `BasePath`, on the site's own host:

| URL | What it is |
|---|---|
| `https://www.example.com/episerver/opticli/mcp` | the MCP endpoint (Streamable HTTP): the URL to give the assistant |
| `/.well-known/oauth-protected-resource/episerver/opticli/mcp` | protected resource metadata (RFC 9728) |
| `/.well-known/oauth-authorization-server/episerver/opticli` | authorization server metadata (RFC 8414), also as `/.well-known/openid-configuration/episerver/opticli` and `/episerver/opticli/.well-known/openid-configuration` |
| `/episerver/opticli/oauth/register`, `/oauth/authorize`, `/oauth/token` | client registration, sign-in and consent, tokens |
| `/episerver/opticli/connections` | the editor's connections, with Revoke |
| `/episerver/opticli/preview/{payload}` | the assistant's preview links, unless `AllowPreviewLinks` is turned off |

The site's own `/.well-known` documents at the root are left alone. A request to the MCP endpoint without a token
gets a 401 that points to the metadata, never a redirect to the login page; a token without `content:read` gets a 403
(`insufficient_scope`). With `BasePath = ""`, the root `/.well-known/oauth-protected-resource` is the module's too.

## Beside another MCP server

A site can run another MCP server built on the official MCP C# SDK next to the module: its own, or a package's, such as
Optimizely's `Optimizely.Cms.Opal.Tools` on CMS 13. Map it at a path of its own (`endpoints.MapMcp("/mcp")`); its paths
and its sign-in don't clash with the module's. The SDK keeps one set of server settings for the whole site, so the
module sees to it that each endpoint serves only its own server:

- The module's endpoint offers the module's tools, name and instructions, and nothing of the other server's. The other
  endpoint offers none of the module's tools, and keeps its own name and instructions.
- The module's endpoint serves MCP requests as POST only (and a DELETE that ends one of its own sessions): no GET
  stream, and no legacy SSE (`/sse`, `/message`), whatever the other server turns on.
- The module asks the SDK for stateless HTTP. If the other server turns sessions on (`Stateless = false`, registered
  after `AddOptiCliMcp`, so its setting wins), the module's endpoint has sessions too, but takes only a session it made
  itself, from the connection that made it: any other session ID gets 404 "Session not found", and the client starts a
  new session. The module's sessions don't work at the other endpoint either. They are bound to the connection by a
  `sub` claim with the connection's ID on the module's own sign-in, so site code that runs during a tool call (a
  content event handler, say) sees that ID as `sub`. The module forgets sessions the SDK has dropped, and prunes the
  rest to as many as the SDK keeps idle ones (`MaxIdleSessionCount`, at least 1,000), the least recently used first.
- Registered before `AddOptiCliMcp`, the other server's `Stateless = false` loses to the module's setting, and with
  legacy SSE the site fails at startup with the SDK's own message: register it after `AddOptiCliMcp`.
- The module keeps the servers apart with the SDK's `HttpServerTransportOptions.ConfigureSessionOptions` hook, and runs
  the other server's own hook on that server's endpoints, so set yours in `WithHttpTransport(...)` as usual. If code
  registered after `AddOptiCliMcp` replaces the hook (a `PostConfigure`), the module's endpoint turns itself off rather
  than serve without it: it answers every signed-in request with an error ("The site turned this MCP endpoint off…"),
  and the site's log says why.
- The site's own tools for the module ([Custom tools](tools.md#custom-tools)) are served on the module's endpoint only. If the
  other server registers the same class too (its `WithTools<T>()`, or `WithToolsFromAssembly()`), the module removes
  those tools there. A tool of the other server's that has the name of one of the module's tools stays on that server,
  whichever is registered first.

## Connections and audit log

At `/episerver/opticli/connections` an editor sees the assistants they connected (app, the scopes they allowed, when
connected and last used, which is updated at most once a minute) and can revoke them. WebAdmins and CmsAdmins see and
revoke everyone's.

Every authorization decision, token issue, refused token request, refresh, revocation, tool call (and each step of a
site's write tool) and opened preview link is logged in the
`OptiCli.Mcp.Audit` category at Information level, with the user, the client, the tool, the content refs and the
outcome; never tokens, links, secrets or property values. A site that logs Warning and up by default needs
`"Logging": {"LogLevel": {"OptiCli.Mcp.Audit": "Information"}}` to keep them. Errors (a tool that failed, a preview
that couldn't render) are logged in the `OptiCli.Mcp` category.
