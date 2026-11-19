# Security model

- **The assistant is the editor.** Every read is checked against the editor's Read access; content they can't read gives
  exactly the same `not_found` as content that doesn't exist, and lists leave it out. Every save, publish, move and
  delete goes through the CMS's own access checks for the editor. The CMS records the editor as the one who saved.
- **What the CMS leaves to the edit UI is checked too**, since the CMS's repository doesn't check it on save:
  - a content type's own access rights (who may create it), and the access its group of types requires on the parent;
  - the access rights per language (admin mode, Languages): an editor who may not edit a language gets no draft,
    publish, unpublish or discard in it, and can't add it to content;
  - properties the edit UI hides or locks for the editor: not displayed in edit mode (`[ScaffoldColumn(false)]`),
    read-only (`[Editable(false)]`), on a tab whose required access they lack on the content (in an inline block or a
    block list's item too, by the content it is in; not in a local block, which the edit UI shows whatever its tab), or
    hidden or locked for them by an editor descriptor or metadata extender. The module asks the CMS UI's own metadata,
    as the edit UI's form builds it (local blocks nested in their content's), and when that can't be built for an item,
    the model's settings decide what is shown and nothing of the item may be changed. `get_content` leaves these
    properties out, and so do diffs, also inside local blocks and block lists; writing them is refused, and a block list
    rewritten keeps what its items had of them. `get_content_type` leaves out what the type's settings hide or lock;
    what depends on the content and the editor only `get_content` knows. Its `allowedTypes` and `restrictedTypes` are
    the edit UI's too: from `[AllowedTypes]`, or for a property with a `UIHint` and without the attribute, what an
    editor descriptor sets for the hint (as sites do for an area many page types share), from the metadata of a new
    item of the type;
  - the types such an editor descriptor sets: the CMS checks only `[AllowedTypes]` on save, so content that a write
    adds to one of these ContentAreas or reference properties, and that the edit UI wouldn't offer there, is a
    validation error (`valid: false` in a dry run), as for `[AllowedTypes]`. What the property already holds may stay.
    The content's own properties are checked, not those of blocks inside it;
  - content references: every one is looked up as the editor, also in rich text (embedded blocks), so an assistant
    can't point to content the edit UI's pickers wouldn't offer, or learn from a dry run that it exists. The CMS's own
    text forms of a ContentArea or a reference list, which skip that, are refused: give an array of refs;
  - new blocks in a ContentArea: where the edit UI makes them shared blocks in the content's "For this page" folder, so
    does the module, with the rights the edit UI's "Create a new block" needs: Edit and Create on the content (Create on
    its parent for new content) and a block type it offers there (on new content, one they may create in its folder;
    see [New blocks in a ContentArea](../cli/content-areas.md#new-blocks-in-a-contentarea));
  - restoring from the recycle bin, which the module doesn't do at all (a move out of the recycle bin is refused, with
    a hint to restore it in the CMS, where the editor sees what comes back live).
- **No script in rich text or links.** The CMS saves rich text as it is given, and the edit UI's TinyMCE only cleans it
  in the browser. A draft with script would run it in the edit UI of whoever opens it, with their session, and could
  publish through the CMS's own API, past `AllowPublish`. So rich text with `<script>`, event handler attributes
  (`onerror=...`), `javascript:`, `vbscript:` or non-image `data:` URLs, `<iframe>`, `<object>`, `<embed>`, `<form>`,
  `<base>`, `<meta>`, `<link>`, `<svg>` and the like is refused, however it is written (character references, tabs or
  odd case in a scheme, attributes without quotes), and so are links (link properties, link collections, URL
  properties, URLs in a list property's items, a shortcut's external link) other than http, https, mailto, tel and
  relative ones, and links with attributes other than href, title and target (the CMS renders every attribute a link
  has), or with a control character in them. Links are given as `{href, text, title, target}`, never as the CMS's
  stored markup. Ordinary rich text passes: tables, images, links, embedded blocks, `style` and `class`. What a property
  already has may be written back as it is, so an editor's assistant can still fix a typo in rich text with a video's
  `<iframe>` or a link collection with an sms: link; anything new is refused, also such a construct copied once more or
  into another property. Only what a browser surely reads as live counts as already there: script inside an element it
  reads as text or keeps inert (`<textarea>`, `<style>`, `<template>`, `<svg>`, ...) is never written back live, nor a
  live construct moved into one. Set the CMS's own script parser to remove script as well, for every save, the edit
  UI's included (CMS 12.15 or newer): `"EPiServer": {"Cms": {"ScriptParser": {"SavingMode": "Remove"}}}`. CMS 13
  already refuses such a save by default.
- **No files that run script.** A file uploaded to the CMS is served from the site's own origin, so one a browser runs
  script in would run it with the session of whoever opens it. An editor's assistant can't upload a file the site
  serves as HTML, XML (in any of its types but SVG), XSLT or JavaScript, going by the site's own content types (its
  static file content types, which serve media, and the CMS's MIME registry), whatever the extension: `.html`, `.htm`,
  `.xhtml`, `.xml`, `.xsl`, `.js` and the like, but also the legacy `.hxt`, `.stm`, `.xsd`, `.dtd` or `.wsdl` a default
  site serves as HTML or XML. Nor compressed SVG (`.svgz`). The CMS serves media by a URL made from its name, so a
  media name (on upload, a rename, a replaced file) that ends in another extension than the file's is checked as that
  extension. The hint says to upload such a file in the CMS. An SVG file (or a file the site serves as SVG) is read: one with
  `<script>`, `<foreignObject>`, event handler attributes, `javascript:` or non-image `data:` links (also set by an
  animation), entity or attribute list declarations or an `xml-stylesheet` instruction is refused, and an ordinary
  drawing uploads. A file named as a raster image (`.jpg`, `.png`, ...) that starts like markup is refused too. Have the
  site send `X-Content-Type-Options: nosniff` with its media, so a browser never takes a file for anything but what its
  content type says.
- **The role gate** (`AllowedRoles`) is checked against the editor's roles as they are now (the CMS UI's role
  provider), not the ones in their login cookie: at consent and at every token refresh. So is their account, where the
  site manages it: an ASP.NET Identity user who is disabled, locked out or deleted gets no consent and no new tokens.
  An editor who fails either at a refresh gets no new tokens, and the connection is deleted; the access token they
  have works until it expires (`AccessTokenLifetime`).
- **External logins** (Microsoft Entra ID, Opti ID, any OpenID Connect provider): the account is managed by the
  identity provider, not the CMS, so for a user the CMS synchronized from one the module doesn't judge the account at
  all, whatever a user provider for such users reports, and the roles decide. Those roles are only as current as the
  CMS's copy, which it updates when the editor signs in; and the CMS never hears that an account was disabled in the
  identity provider. So such an editor, disabled there, could keep refreshing for as long as the connection is used.
  `ConnectionLifetime` bounds that: counted from when the editor allowed the connection, checked at every refresh and
  on every access token, after which they have to connect again through the site's login, where the identity provider
  has its say. The default is 30 days; for a site with external logins, 1 to 7 days is a better choice. The site's
  login cookie counts as signed in while it lasts, so its lifetime matters too.
- **Approval sequences are enforced.** The CMS doesn't apply them to API saves, so the module refuses a publish of
  content under one and offers `requestApproval`, which sends it for review as the edit UI's Ready for Review does.
  `requestApproval` never publishes, so it needs neither `AllowPublish` nor `content:publish`: where no sequence
  applies, it is refused (`noApprovalSequence`) and nothing is saved.
- **Publishing and deleting are opt-in** (`AllowPublish`, `AllowDelete`). Without `AllowPublish` the consent page
  doesn't offer the `content:publish` scope, and every publish is refused with a hint to save a draft instead; with it,
  the editor still chooses on the consent page, and their Publish rights still apply. Turning `AllowPublish` off later
  refuses publishing at once, and existing connections lose `content:publish` at their next refresh. Besides each tool's own check,
  every save that would publish or schedule is checked against the same gate, and so is every other change that puts
  something live at once: moving content that has a published version or anything below it (its URLs change, it takes
  the access rights of its new parent; the CMS itself only asks for Publish on the destination when the moved item is
  published), discarding a version scheduled for publishing, and saving content without versions. Content that may be
  live isn't moved from one approval sequence to another at all (move it in the CMS). Changing access rights and
  removing language branches aren't offered at all.
- **Deleting** only moves content to the recycle bin, but note that this takes published content offline, with
  `AllowDelete` alone (and the editor's Delete rights): `delete_content` needs no publishing rights. Discarding a draft
  someone else saved deletes their work for good, so it needs `AllowDelete` too; an editor's own drafts can always be
  discarded.
- **`includeDraft` is a confirmation, not a boundary.** It stops the assistant from putting live, deleting, or (where
  the site makes shared area blocks) saving a block's new version over someone else's changes without asking the user,
  but the assistant sets it itself; what the site allows is decided by
  `AllowPublish`, `AllowDelete` and the editor's access rights. It is needed also when the assistant publishes a
  version it names (`version`, or a `123_456` reference): unlike the CLI's `publish --version`, where naming the version
  is the developer's choice, the assistant picks it. A review request (`requestApproval`) is never refused for someone
  else's changes, as the reviewer sees the whole version, but its result (and an `update_content` dry run's) names them
  in `pendingDraft`, so the assistant can tell the editor. It never puts a colleague's draft of a block of the content's
  "For this page" folder live along with a publish: such a block stays a draft unless the assistant names it in
  `publish_content`'s (or `publish_project`'s) `blocks`, which the instructions tell it to do only after asking the
  editor, and a dry run's warnings say whose draft it is.
- **In a project, marking an item ready is consent to publish it.** `publish_project` puts live without `includeDraft`
  the changes of whoever marked an item ready (`readyBy`): a colleague who marked their own draft ready in the CMS, the
  reviewer who approved it under an approval sequence (the CMS records the reviewer), or the developer's opticli
  (`readyBy: "opticli"`). Changes of anyone else that a ready version carries still need `includeDraft`, and the result
  names whose changes went live either way. Because the CMS records whoever marks a version ready as the one who saved
  it, `mark_ready_to_publish` of a draft someone else saved needs `includeDraft` too: after it, the publish couldn't
  tell. The Projects gadget's own Publish (in the CMS) puts every unpublished item of a project live, ready or not,
  without asking about anyone's changes.
- **The consent page** lists what the app may do, with a checkbox each: reading is required, writing and (where the
  site allows it) publishing can be unticked, and the connection gets only what was left ticked. For an app that
  registered itself, a warning says plainly that it chose its own name and the site can't confirm who made it; an app
  with a metadata document is named with the domain that publishes it. The connections page shows each connection's
  scopes.
- **OAuth:** PKCE (S256) is required; codes work once, for 5 minutes. Refresh tokens rotate, as a compare-and-swap, so
  of two refreshes with the same token only one succeeds and a connection revoked meanwhile stays revoked. The refresh
  token before the current one, used again, revokes the connection (RFC 9700), since the site can't tell a leaked
  token from a confused client; but not from the same client within `RefreshTokenReuseGrace` of the refresh that
  replaced it, which is a client refreshing twice at once or retrying after a lost answer: that is only refused, and
  the connection keeps the tokens the other refresh got. Older refresh tokens are just refused. A refresh may ask for fewer scopes: the access token
  gets those, and the connection keeps what the editor approved. Tokens are bound to the MCP endpoint's URL as the editor connected to it (scheme, host and port) and checked
  against their connection on every use, so revoking one cuts the client off at once (another instance of a
  load-balanced site notices within 30 seconds). Codes, secrets and refresh tokens are stored as hashes only. Redirect
  URIs must match exactly (any port for http on a loopback address), and be on a loopback address or one of `AllowedRedirectHosts`. The
  consent page can't be framed and checks an antiforgery token. Client metadata documents are only fetched from public
  addresses (in Development also over http from a loopback address), checked on the connected socket, without redirects. Registration, authorization and token requests are
  rate-limited (`RateLimits`). Registered clients without a connection are deleted a day after they registered, and
  registration stops at 5,000 of them; the cleanup runs in the background, a bounded amount at a time.
- **Load-balanced sites:** the Dynamic Data Store has no conditional update, so a refresh's compare-and-swap is
  atomic within an instance only. Two instances given the same refresh token within milliseconds could both rotate
  it (the later one wins, and the other new refresh token never works), and a revocation on one instance in the same
  milliseconds as a refresh on another could be undone by it. Taking a code has the same window, which PKCE covers.
- **What an editor can still tell about content they can't read:** that a page they can read has a parent (its
  `parent` id, which they can't open), and that a ContentArea or content reference they can read points to something
  (its id, which they can't open). Both are by design: a bare id, never the content's name, type or values.
  `list_children`'s cursor counts only what the editor can read, so paging tells nothing of what lies between, and
  `find_content` counts only what they can read too; it stops after 5,000 items they can read or 20,000 loaded in all,
  hidden ones included (or after 20 seconds), and then says `truncated`: a search below a great deal of hidden content can still tell that
  much.
- **Uploads follow the site's own upload rules** as well as `MaxUploadBytes`: the CMS UI's size limit
  (`EPiServer:CmsUI:Upload:FileSizeLimit`, the lower of the two applies) and, from CMS UI 12.33, its list of allowed
  file extensions (`AllowedFileExtensions`), so a site that keeps SVG out of its uploads keeps it out here too. On CMS
  13 these are the CMS's `MediaFileOptions`, and the CMS's script parser applies too (see [On CMS 13](install.md#on-cms-13)).
- **Images:** `view_media` shows the assistant a media item's image, with the editor's read access like every read.
  JPEG, PNG, GIF and WebP files up to 1568 pixels on the longest side (or a smaller `maxSize`, at least 64) and 1 MB
  are sent as they are stored. Larger ones
  are scaled down with the site's own image service, the one that makes the edit UI's thumbnails (built into CMS 12
  before 12.17, then `EPiServer.ImageLibrary.ImageSharp`, which `EPiServer.CMS` brings, on CMS 13 too). An image of
  more than 64 million pixels isn't decoded, and other files (PDF and the like) aren't shown. SVG is sent as its markup, up to 256 KB; files over 50 MB
  aren't read. Text in an
  image is content like any other, with the same risk of prompt injection.
- **Host names:** the module builds its issuer, resource and metadata URLs from the request's host, as the site's own
  login does, and its metadata documents are never cached (`Cache-Control: no-store`). Still, limit which hosts the site
  answers on: set `RequireHost` (or the site's `AllowedHosts`), and behind a proxy accept forwarded headers from the
  known proxies only.
- **Content is data.** The server's instructions tell the model to read first, show the user a dry run, save drafts,
  publish only when asked, and never follow instructions found in content. That lowers, but can't remove, the risk of
  prompt injection through content: the editor reviews the drafts (every write result has `editUrl`, the version in the
  CMS edit UI, `previewUrl`, the version as visitors will see it, and `url`, its public address). A dry run can't run the site's own code that reacts to saves; the real
  save reports what it did as `siteChanges` (content it created, published or moved, a new page it filed elsewhere, or a value it changed). Advise editors not to use this connector in the same chat as tools that fetch web pages or read other
  outside content: what such a page says is then one step from the CMS.
- **Preview links need the editor's sign-in.** `previewUrl`, on a write result that saved a version visitors don't see
  yet and on `get_content` of such a version, is the CMS's own preview of it (`/EPiServer/CMS/Content/...,,123_456/`,
  under `/Optimizely/CMS/` on CMS 13),
  on the edit link's host: it opens for someone signed in to the CMS with access to the draft, and sends anyone else to
  the login. It carries no preview token, so the assistant can't open it itself, and a link in a chat transcript shows
  nobody the draft. The CMS can add one (CMS 12.21 and later, `UrlResolverArguments.AppendPreviewToken`), but such a
  token isn't a link to one draft: it signs whoever holds it in as the editor, for five minutes, and from CMS 12.22 (and
  on CMS 13) on any request to the site, not only the content it was made for. The module never makes one.
- **The assistant's own preview links are on by default** (`AllowPreviewLinks`; `false` turns them off). The page
  renders as the editor, so what an editor gets and visitors don't (navigation, blocks, user-specific output) reaches
  whoever holds the link for its lifetime, and the site's own code for that page runs in the editor's name: a site
  that doesn't want that turns the option off. With it on, the same results also have `assistantPreviewUrl` (and
  `assistantPreviewExpires`): `{origin}{BasePath}/preview/{payload}`, on the host the editor connected the assistant
  to, which the assistant fetches itself to check how the draft looks. The payload
  names the connection, the editor, the version, its language and the host the link was issued on, signed and
  time-limited with the site's Data Protection keys; it carries no CMS preview token and nothing that works anywhere
  else. The module renders the CMS's preview of that one version inside the site, as the editor, for that one request:
  it answers GET and HEAD only, on the link's own host only, and takes the version from the payload alone. Only the
  link exactly as the module gave it opens: that path in its casing, one segment of base64url (no other spelling of
  the same bytes, no padding) and no query; anything else below `{BasePath}/preview` is refused. It drops the request's
  body and every header but `Host`, `Accept`, `Accept-Encoding`, `Accept-Language`, `User-Agent` and the forwarded
  headers (so its cookies and `Authorization` too), and sends no cookie, no redirect, no sign-in challenge and no error
  page back (anything but a success is a plain 404, or 500 for an error, a failure or a page larger than 10 MB; 405
  for other methods, 429 over a rate limit), with `Cache-Control:
  no-store`, `X-Robots-Tag: noindex, nofollow` and `Referrer-Policy: no-referrer`. The module itself changes nothing,
  but the site's own code for that page runs as the editor, as when the editor previews the draft in the CMS: code that
  writes something when a page is viewed does so in the editor's name.
  The link stops working after `PreviewLinkLifetime` (10 minutes), when the connection is revoked or ends, and when the
  editor can no longer read the version (with the roles of the connection's last refresh, as for every tool call).
  Until then, whoever has it sees that draft: the assistant, and anything that keeps the chat or the fetch, such as a
  transcript or a proxy's log. They see it as the editor sees it: a page type whose view shows something for the
  signed-in user (their account, content filtered by their access, a visitor group by role) shows the editor's. That
  includes the editor-only parts a site's layout adds for a signed-in editor, such as the CMS's quick navigator (with
  links to the dashboard and the edit UI), the edit UI's communication script, a "Log out" link, or an antiforgery
  token bound to the editor in a form. None of these works for anyone else (no cookie comes with the link, and none is
  set), but they are in the HTML. So the server's instructions tell the assistant not to hand the link to the user, who
  reviews the draft through `editUrl` and `previewUrl`. Each opened link is audited (the version, the editor, the
  client and the outcome; never the link), and rate limited per connection (`RateLimits.PreviewPerMinute`); requests
  that aren't a valid link have a window of their own per address (`RateLimits.InvalidPreviewPerAddressPerMinute`), so
  that a flood of them from an assistant provider's shared addresses can't use up anyone's.
  The module adds a middleware at the start of the site's pipeline for it (only with the option on), since a preview
  needs routing and the site's own views after it. The rate limit, `RequireHost` and the link's host need the client's
  address and host behind a proxy, so the forwarded headers must be applied before the module checks them. Where they
  already are (a startup filter before the module: `AddCmsCloudPlatformSupport()` on DXP, or
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED`), the module leaves them as they are. Where the site applies them only in its
  own `Configure`, after the module, the module applies the site's settings (`ForwardedHeadersOptions`, with their
  `KnownProxies` and `KnownNetworks`) to these requests itself, and the site's own `UseForwardedHeaders()` then finds
  nothing more to do. That needs the settings in the services (`services.Configure<ForwardedHeadersOptions>(...)`), not
  only passed to `UseForwardedHeaders(options)` in `Configure`, which the module can't see: there, behind a proxy that
  changes the host, links answer 404. The module also leaves them alone when a proxy sends an `X-Original-Host` (or
  `-For`, `-Proto`) header of its own, as if they had been applied already: behind such a proxy, apply them before the
  module (`ASPNETCORE_FORWARDEDHEADERS_ENABLED` or a startup filter). Call `AddOptiCliMcp()` after `AddCms()` (as in
  [Install](install.md#install)), so the module comes after the CMS's first-request initialization, and after `AddCmsCloudPlatformSupport()` on
  DXP, so it comes after its forwarded headers. A base path the site sets with `UsePathBase()` isn't supported for these
  links (an IIS application's is).

## Error pages and security headers

- **Custom error pages** (`UseStatusCodePages`, `UseStatusCodePagesWithReExecute`) are switched off for the module's
  own requests, and its errors all have a body, so the site's error page never replaces them. That matters most for
  the MCP endpoint's 401: re-executed as a POST to a GET-only error page, it would reach the client as a 405 without the
  `WWW-Authenticate` header that tells it where to sign in.
- **Security headers:** the module's pages (consent, connections, its error pages) send `X-Frame-Options: DENY`, a
  `Content-Security-Policy` with `default-src 'none'`, `frame-ancestors 'none'` and a `form-action` limited to the
  module and the client's redirect, `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff` and
  `Cache-Control: no-store`. Neither page takes a CMS preview token (`preview_token`) as a sign-in: such a request
  gets a 400. Middleware that sets a site's own security headers as the response starts replaces
  them, and the module can't set its own later than that. The page also carries its policy and referrer policy in
  `meta` elements, which the browser applies besides the site's header, but `frame-ancestors` and `X-Frame-Options`
  only work as headers. So have such middleware leave `{BasePath}` (`/episerver/opticli`) alone, or only set headers
  the response doesn't already have; or check that the site's own values forbid framing.
- **Whatever the headers:** a page the browser asks for to show in a frame (`Sec-Fetch-Dest: iframe`, `frame`,
  `fencedframe`, `object` or `embed`) gets a 403 page instead, and the consent and revoke posts are refused when the
  browser says they came from another site or origin (`Sec-Fetch-Site` other than `same-origin`). A browser too old to
  send these headers gets the page as before. The metadata documents are sent with `Cache-Control: no-store`.
