# MCP server for editors

The MCP module is a separate NuGet package for the site itself, not for developers' machines. Installed in a site, it
lets **editors** connect an AI assistant that supports MCP servers (Claude, or another MCP client with an OAuth
sign-in) to that site, in any environment, production included. The editor signs in with the site's own login, and the
assistant then works as that editor, with their access rights in the CMS. The tools run the same content operations as the site agent: drafts by default, dry runs with a
list of changes, validation, approval sequences, `baseVersion` conflicts, and errors with a hint.

It comes as two packages, one for each CMS version, released together with the same version as the CLI:

| The site runs | Package |
|---|---|
| CMS 12 (`EPiServer.CMS.Core` 12.12.1 and `EPiServer.CMS.UI.Core` 12.16.1 or newer), on .NET 8 or newer, .NET 10 included | `OptiCli.Mcp` |
| CMS 13 (13.0.0 or newer) | `OptiCli.Mcp.Cms13` |

Both are built from the same code and set up the same way: the same `AddOptiCliMcp`, `MapOptiCliMcp` and options, the
same endpoints and tools. What differs on CMS 13 is under [On CMS 13](#on-cms-13).

## Install

```sh
dotnet add package OptiCli.Mcp          # CMS 12
dotnet add package OptiCli.Mcp.Cms13    # CMS 13
```

In `Startup.cs`, the same for both:

```csharp
using OptiCli.Mcp;

public void ConfigureServices(IServiceCollection services)
{
    // ... AddCms() and the rest
    services.AddOptiCliMcp(o =>
    {
        o.AllowPublish = true;   // optional: off by default
    });
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

Map the module **after** `MapContent()` and the site's own endpoints. Its paths are literal, so they win over content
routing's catch-all wherever they are mapped, and mapped last it changes nothing for the rest. Mapped before
`MapContent()` it does: the CMS's documentation asks for other endpoints before or after `MapContent()`, not both,
because with anything mapped before it, the CMS freezes the routes add-ons register, and a `MapControllers()` after it
then maps the site's controllers a second time. A site with a named route then fails every request with "Duplicate
endpoint name". The module logs a warning when it is the site's first endpoint mapping.

The module adds a bearer scheme of its own for the MCP endpoint, and leaves the site's default authentication scheme
as it is: editors sign in with whatever the site uses (ASP.NET Identity, Microsoft Entra ID, Opti ID). Connections
(and the record of each [action tool](tools.md#action-tools) run) are kept in the Dynamic Data Store, and access tokens (and the assistant's preview links, if the site allows them) are
protected with the site's Data Protection keys, which every instance of a load-balanced site already shares for its
login cookies.

`AddOptiCliMcp` returns a builder, on which a site can add tools of its own to the module's server: see
[Custom tools](tools.md#custom-tools). Call it once, with all the options: a second call throws. Where the site has no
`UIRoleProvider`, the module logs a warning and goes by the roles in the editor's login.

Requirements: `OptiCli.Mcp` needs .NET 8 or newer, `EPiServer.CMS.Core` 12.12.1 or newer and `EPiServer.CMS.UI.Core`
12.16.1 or newer (the first CMS 12 versions whose dependencies allow the MCP SDK's `Microsoft.Extensions` 10.x
packages). A CMS 12 site on .NET 10 uses it too: the package goes by the CMS version, not by .NET's.
`OptiCli.Mcp.Cms13` needs .NET 10, `EPiServer.CMS.Core` and `EPiServer.CMS.UI.Core` 13.0.0 or newer, and brings
`EPiServer.ImageLibrary` (the image service interface `view_media` scales images through). Both bring the official MCP
C# SDK (`ModelContextProtocol.AspNetCore` 2.2.0).

**The wrong package for the site's CMS** is usually refused by NuGet already, as the CMS packages' versions conflict or
there is no build for the site's .NET version. Where it isn't, the site stops at startup, in `AddOptiCliMcp`, with an
error that names the right package:

```
System.InvalidOperationException: This site runs CMS 13: use the OptiCli.Mcp.Cms13 package instead of OptiCli.Mcp.
```

(or "This site runs CMS 12: use the OptiCli.Mcp package instead of OptiCli.Mcp.Cms13.").

**Moving a site from CMS 12 to 13** is one package swap: replace `OptiCli.Mcp` with `OptiCli.Mcp.Cms13` at the same
version, and leave `Startup.cs` and the `OptiCli:Mcp` configuration as they are. The module's default path moves with
it, from `/episerver/opticli` to `/optimizely/opticli` (see [On CMS 13](#on-cms-13)), so editors connect their
assistant again at the new address, or the site sets `BasePath` to `/episerver/opticli` to keep the old one.
Connections are kept in Dynamic Data Store stores with the same names on both, so they carry over when the database's
store data does and the path stays the same; otherwise editors connect their assistant once more after the upgrade.

## On CMS 13

Everything else under [MCP server for editors](#mcp-server-for-editors) holds on CMS 13 too. What is different there:

- **The module's path:** `/optimizely/opticli` by default (the MCP endpoint is `/optimizely/opticli/mcp`, the
  connections page `/optimizely/opticli/connections`), where CMS 12 has `/episerver/opticli`. On each major that is under
  the CMS's own protected path (`/Optimizely` on CMS 13, `/EPiServer` on CMS 12), where the CMS's login signs editors in.
  Opti ID only signs editors in there (and in its `AdditionalPaths`), so at `/episerver/opticli` on CMS 13 the consent
  and connections pages got no CMS sign-in. A site that set the module up on CMS 13 with opticli 0.37 or earlier, at
  `/episerver/opticli`, either has editors connect their assistant again at the new address (the old connections stop
  working, and stay on the connections page until they expire or are revoked there), or keeps the old path, where its
  own login signs editors in:

  ```csharp
  services.AddOptiCliMcp(o => o.BasePath = "/episerver/opticli");
  ```

  The module logs a warning at startup when `BasePath` isn't under the CMS's protected path. The Claude Desktop extension
  tries both paths for a site address without one (see [Connecting Claude](connecting.md#connecting-claude)).
- **Visual Builder.** `get_content` of an experience gives its composition: sections, rows, columns and elements, with
  their display templates and settings and the elements' properties. It follows the same rules as every other read:
  properties the edit UI hides or locks are left out (an element's tabs are shown as the edit UI shows them, as a local
  block's are), and a shared block or section the editor can't read shows only its place in the composition (`nodeType`,
  `key` and a `ref` they can't open), never its name, type or values. Dry runs and pending drafts list a composition's
  changes by the same rules, and leave out a change made only inside content the editor can't read. **Editors can't
  change compositions through the MCP server yet**: an experience's other properties can be changed, and `Layout` is
  refused. Developers change compositions with `opticli composition` (see [Visual Builder](../cli/cms13.md#visual-builder)).
- **Edit links:** `editUrl` and `previewUrl` come from the CMS, so they open CMS 13's edit UI, under `/Optimizely/CMS/`
  instead of `/EPiServer/CMS/`.
- **Uploads** follow the CMS's `MediaFileOptions` (`EPiServer:Cms:MediaFile`), which CMS 13's edit UI uses in place of
  the CMS UI's upload settings: its `FileSizeLimit` (30,000,000 bytes by default; the lower of it and `MaxUploadBytes`
  applies) and its `AllowedFileExtensions`, matched as the edit UI matches them, whole and with the dot (`.jpg`; `jpg`
  allows nothing). After the module's own checks, a file goes through the CMS's script parser, as an upload in the edit
  UI does (for the extensions in its `MediaExtensionsToParse`: `.svg`, `.svgz`, `.html` and `.htm` by default): where
  the parser refuses the file, so does the module, with the parser's message; otherwise the module stores the file as
  the parser wrote it back, as the edit UI does, once that is checked for script again.
- **Script in rich text:** CMS 13's script parser refuses event handler attributes and `javascript:` links in rich
  text, URL and link properties on save by default, and removes them when content is loaded. The module's own checks
  still answer first, with their own message, and cover what the parser doesn't (`<iframe>`, `srcdoc`, forms, `data:`
  links and the rest).
- Optimizely's own MCP server (`Optimizely.Cms.Opal.Tools`) can run in the same site: see
  [Beside another MCP server](configuration.md#beside-another-mcp-server).

## Uploads on IIS

The MCP endpoint takes requests up to `MaxUploadBytes` as base64 plus 1 MB, and refuses larger ones with a 413; a file
larger than the CMS UI's own upload limit (`EPiServer:CmsUI:Upload:FileSizeLimit`: 4 MB by default up to CMS UI 12.31,
30,000,000 bytes from 12.33; on CMS 13 `EPiServer:Cms:MediaFile:FileSizeLimit`, 30,000,000 bytes) is refused too, as
the edit UI refuses it, so raise that as well for larger uploads. IIS has a limit of its own, `maxAllowedContentLength`,
30,000,000 bytes by default, so a site that raises `MaxUploadBytes` past about 20 MB must raise that too, in
`web.config`:

```xml
<system.webServer>
  <security>
    <requestFiltering>
      <requestLimits maxAllowedContentLength="73400320" />
    </requestFiltering>
  </security>
</system.webServer>
```
