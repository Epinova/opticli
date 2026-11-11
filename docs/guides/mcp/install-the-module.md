---
title: Install the MCP module in your site
summary: Add the opticli MCP module to a CMS 12 or CMS 13 site, decide what assistants may do, and connect a first assistant to check that it works.
audience: mcp
order: 1
written-for: 0.30.0
---

The MCP module turns your Optimizely site into an MCP server with its own OAuth sign-in. Editors connect an AI
assistant to it, sign in with the site's normal login, and the assistant then works on content as them, with their
access rights. By the end of this guide the module runs in your site, you have chosen what assistants may do, and an
assistant on your own machine has read and drafted content on a local Alloy site.

## Before you start

- A site on CMS 12 (`EPiServer.CMS.Core` 12.12.1 and `EPiServer.CMS.UI.Core` 12.16.1 or newer, .NET 8 or newer) or
  CMS 13 (13.0.0 or newer, .NET 10).
- A login that works for editors: ASP.NET Identity, Microsoft Entra ID or Opti ID. The module uses whatever the site
  already has.
- An editor account in one of the default roles (WebEditors, WebAdmins, CmsEditors, CmsAdmins or Administrators).
- An MCP client for testing. This guide uses Claude Code, which connects from your own machine, so a local site works.

## Install and connect

### 1. Add the package for your CMS version

There is one package per CMS major. Both are released with the same version as the CLI and set up the same way.

| The site runs | Package |
|---|---|
| CMS 12, on .NET 8 or newer (.NET 10 included) | `OptiCli.Mcp` |
| CMS 13 | `OptiCli.Mcp.Cms13` |

```bash
dotnet add package OptiCli.Mcp          # CMS 12
dotnet add package OptiCli.Mcp.Cms13    # CMS 13
```

The package goes by the CMS version, not by .NET's: a CMS 12 site on .NET 10 still takes `OptiCli.Mcp`. If you pick
the wrong one and NuGet doesn't refuse it already, the site stops at startup with an error that names the right
package.

### 2. Register the module

In `Startup.cs`, add the services after `AddCms()`, and map the module after `MapContent()` and the site's own
endpoints. The code is the same on CMS 12 and CMS 13.

```csharp
using OptiCli.Mcp;

public void ConfigureServices(IServiceCollection services)
{
    // ... AddCms() and the rest
    services.AddOptiCliMcp();
}

public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
{
    // ... UseRouting(), UseAuthentication(), UseAuthorization()
    app.UseEndpoints(endpoints =>
    {
        endpoints.MapContent();
        endpoints.MapControllers();   // the site's own endpoints, if it has them
        endpoints.MapOptiCliMcp();    // after everything else
    });
}
```

Order matters in both places. `AddOptiCliMcp()` after `AddCms()` (and after `AddCmsCloudPlatformSupport()` on DXP)
puts the module's middleware after the CMS's initialization and the platform's forwarded headers. `MapOptiCliMcp()`
last keeps the CMS's route registration intact: mapped before `MapContent()`, it makes a later `MapControllers()` map
the site's controllers twice, and a site with a named route then fails with "Duplicate endpoint name". The module logs
a warning when it is the first mapping.

The module adds a bearer scheme of its own for the MCP endpoint and leaves the site's default authentication scheme
alone. Connections are kept in the Dynamic Data Store, and tokens are protected with the site's Data Protection keys,
so a load-balanced site needs no extra setup beyond the shared keys it already has for its login cookies.

### 3. Decide what assistants may do

Out of the box, an assistant can read what the editor can read and save drafts. Everything that changes what
visitors see is off until you turn it on.

| What | Default | How a site turns it on or off |
|---|---|---|
| Read, and save drafts | on | the editor can untick writing on the consent page |
| Publish, unpublish, schedule | off | `AllowPublish = true` |
| Delete (to the recycle bin), discard others' drafts | off | `AllowDelete = true` |
| The assistant's own preview links to a draft | on | `AllowPreviewLinks = false` |
| Script in rich text and links, files that run script | always refused | no option; see the note below |
| Who may connect at all | WebEditors, WebAdmins, CmsEditors, CmsAdmins, Administrators | `AllowedRoles` |

Options come from the `OptiCli:Mcp` configuration section first, then from the delegate you pass to `AddOptiCliMcp`,
which wins. Configuration is handy per environment:

```json
{
  "OptiCli": {
    "Mcp": {
      "AllowPublish": true,
      "AllowedRoles": ["WebEditors", "WebAdmins"]
    }
  }
}
```

The same in code:

```csharp
services.AddOptiCliMcp(o =>
{
    o.AllowPublish = true;
    o.AllowDelete = false;
});
```

A list in configuration (`AllowedRoles`, `AllowedRedirectHosts`) replaces the default list rather than adding to it.
Invalid options stop the site at startup with a message that starts with `OptiCli:Mcp:` and says what is wrong.

Even with `AllowPublish`, the editor still chooses on the consent page whether to grant publishing, their Publish
rights still apply, and a publish of content under an approval sequence is refused: the assistant sends it for review
instead, which needs no publishing option at all.

> **Note:** The module refuses script in rich text and links whatever you configure, but the CMS saves whatever the
> edit UI's TinyMCE or other code gives it. On CMS 12.15 or newer, have the CMS's script parser remove script on every
> save too: `"EPiServer": {"Cms": {"ScriptParser": {"SavingMode": "Remove"}}}`. CMS 13 already refuses such saves by
> default.

The full list of options, with token lifetimes and rate limits, is in the README under
[Options](https://github.com/Epinova/opticli#options).

### 4. Run the site and check the endpoint

Start the site as you normally do. With the default `BasePath`, the MCP endpoint is `/episerver/opticli/mcp`. A request
without a token must get a 401 that points to the OAuth metadata, never a redirect to the login page:

```bash
curl -i -X POST https://localhost:5001/episerver/opticli/mcp
```

```text
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer resource_metadata="https://localhost:5001/.well-known/oauth-protected-resource/episerver/opticli/mcp", scope="content:read content:write"
```

The `scope` list shows `content:publish` too once you set `AllowPublish`. Fetch the metadata document it names to see
that the site answers it:

```bash
curl https://localhost:5001/.well-known/oauth-protected-resource/episerver/opticli/mcp
```

The other paths (authorization server metadata, `register`, `authorize`, `token`, the connections page) are listed in
the README under [Endpoints](https://github.com/Epinova/opticli#endpoints).

### 5. Connect a first assistant

Add the endpoint to Claude Code, then sign in from Claude Code's `/mcp` menu:

```bash
claude mcp add --transport http cms https://localhost:5001/episerver/opticli/mcp
```

Your browser opens the site's login page and then the module's consent page, which lists what the assistant may do.
Allow it, and Claude Code has a connection. Claude Code returns to a loopback address, which the module always
allows, so nothing needs registering on the site first.

Plain http on a loopback address (`http://127.0.0.1:5000/...`) works if your site allows it. Over https, Claude Code
doesn't trust the ASP.NET Core development certificate: the README's
[Connecting Claude](https://github.com/Epinova/opticli#connecting-claude) section shows how to make a localhost
certificate signed by a test CA of your own.

Editors don't use Claude Code. Send them the editors' guide, [Connect your assistant](../editors/connect-your-assistant.md),
which covers the Claude Desktop extension and custom connectors. Web assistants such as claude.ai call the site from
their own servers, so they need a site they can reach: see [Take the MCP module to production](hardening.md).

### 6. Check that it works

Ask the assistant who it is on the site. It calls `whoami`, which reports the editor, their roles, the scopes they
approved and what the site allows:

```prompt
Who am I on the CMS, and what does the site let you do?
```

Then try a read and a dry run on Alloy content:

```prompt
Find the Alloy Plan product page and show me its teaser text. Then suggest a shorter one, as a dry run only.
```

The assistant should show you the change its dry run reports and save nothing until you agree. Let it save the draft,
open the `editUrl` it gives you, and check the draft in the edit UI: the CMS shows you as the one who saved it.

Finally, open `/episerver/opticli/connections` in your browser. You see the connection you just made, with its scopes,
and can revoke it there.

## If something goes wrong

- **"This site runs CMS 13: use the OptiCli.Mcp.Cms13 package"** (or the CMS 12 variant): swap the package, keep the
  code. Moving a site from CMS 12 to 13 later is the same swap.
- **"Duplicate endpoint name" on every request:** `MapOptiCliMcp()` is mapped before `MapContent()`. Move it last.
- **The 401 is a 405, or has no `WWW-Authenticate`:** something in front of the site rewrites it. The module already
  switches off the site's status code pages for its own requests; check proxies and other middleware.
- **"… isn't an editor on this site, so it can't connect an AI assistant":** the account isn't in `AllowedRoles`.
  Roles are checked as the CMS knows them now, not from the login cookie.
- **`UNABLE_TO_VERIFY_LEAF_SIGNATURE` in Claude Code:** the development certificate. Use plain http on loopback or a
  certificate signed by a test CA.
- **Publishing is refused with a hint to save a draft:** `AllowPublish` is off, or the editor unticked publishing on
  the consent page. Change the option, then have them connect again.

## Related

- [Write your first site-specific tool](custom-tools.md)
- [Take the MCP module to production](hardening.md)
- [Connect your assistant](../editors/connect-your-assistant.md), for editors
- README: [MCP server for editors](https://github.com/Epinova/opticli#mcp-server-for-editors),
  [Install](https://github.com/Epinova/opticli#install-1), [On CMS 13](https://github.com/Epinova/opticli#on-cms-13),
  [Security model](https://github.com/Epinova/opticli#security-model)
