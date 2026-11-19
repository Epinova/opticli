# Connecting an assistant

Any MCP client that speaks Streamable HTTP and signs in with OAuth (authorization code with PKCE, identified by a client
ID metadata document or registered dynamically) can connect: add a server with the MCP endpoint's URL. The first time it
connects, the editor's browser opens the site's login page, then the module's consent page.

- **On the editor's own machine** (desktop apps, code editors, command-line agents): the client returns to a loopback
  address, which is always allowed, and calls the site from that machine. It connects as it is.
- **Web-based assistants** return to their own host, which must be in `AllowedRedirectHosts` (claude.ai and claude.com
  by default), and call the site from their own servers, so the site must be reachable from there (below).

## Connecting Claude

- **Claude Desktop extension (recommended for editors):**
  [opticli-cms.mcpb](https://github.com/epinova/opticli/releases/latest/download/opticli-cms.mcpb), attached to every
  [GitHub release](https://github.com/epinova/opticli/releases). Send it to the editors. They open the file (or drag it
  into Claude Desktop's Settings → Extensions) and fill in the site's address: `https://www.example.com` gets the
  default path, and a full endpoint URL works for another `BasePath` or a dedicated host. The first time Claude uses it,
  the editor's browser opens the site's login page, then the consent page. The extension connects from the editor's
  own machine (through [mcp-remote](https://www.npmjs.com/package/mcp-remote), which keeps the sign-in in
  `~/.mcp-auth`), and nobody has to add a connector for the organization. Claude Desktop runs it with its own Node.js,
  so editors install nothing else.
- **Custom connector (claude.ai in the browser, mobile, or Claude Desktop without the extension):** add a custom
  connector with the MCP endpoint's URL (on Team and Enterprise plans an owner may have to add it for the
  organization). Claude opens the site's login page, then the module's consent page. Connectors call the site from
  Anthropic's cloud, so the site must be publicly reachable (below).
- **Claude Code:** `claude mcp add --transport http cms https://www.example.com/episerver/opticli/mcp`, then `/mcp` in
  Claude Code to sign in.

**A Claude Desktop extension for one site** has the address built in, so editors aren't asked for it, and a name of
its own, so an editor can install several side by side (test and production); the generic one installs once. Build it
from [mcp-extension/](../../../mcp-extension) with Node.js 20 or newer:

```sh
cd mcp-extension
npm ci
node build.mjs https://www.example.com "Example CMS"   # dist/opticli-cms-www-example-com.mcpb
```

`node build.mjs` without arguments builds the generic `dist/opticli-cms.mcpb`. Both have opticli's version, from
`Directory.Build.props`.

**Trying it locally with Claude Code:** plain http on a loopback address (`http://127.0.0.1:5000/...`) works where
the site allows it. Over https, Claude Code (a native executable) honours `NODE_EXTRA_CA_CERTS` but doesn't accept the
ASP.NET Core development certificate as a trust root, since that certificate is a self-signed leaf (`CA:FALSE`): it
fails with `UNABLE_TO_VERIFY_LEAF_SIGNATURE`, though curl and Node accept it. A localhost certificate signed by a test
CA of your own works:

```sh
openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=Local MCP test CA" \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" \
  -keyout ca.key -out ca.pem
printf 'subjectAltName=DNS:localhost,IP:127.0.0.1\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n' > localhost.ext
openssl req -newkey rsa:2048 -nodes -subj "/CN=localhost" -keyout localhost.key -out localhost.csr
openssl x509 -req -in localhost.csr -CA ca.pem -CAkey ca.key -CAcreateserial -days 30 -extfile localhost.ext -out localhost.pem

# the site, with that certificate
ASPNETCORE_URLS=https://localhost:5443 \
ASPNETCORE_Kestrel__Certificates__Default__Path=$PWD/localhost.pem \
ASPNETCORE_Kestrel__Certificates__Default__KeyPath=$PWD/localhost.key \
dotnet run

# Claude Code, trusting the test CA
NODE_EXTRA_CA_CERTS=$PWD/ca.pem claude
```

Keep `ca.key` to yourself and delete it when you are done: whoever has it can make certificates your Claude Code trusts.

Nothing needs registering on the site first. A client with a client ID metadata document (CIMD), as Claude has, is identified by it: the
site fetches the document from the client's own HTTPS URL, which needs outbound HTTPS from the site. Clients without
CIMD register themselves (dynamic client registration, DCR).

**The site must be publicly reachable for web-based assistants.** Claude's custom connectors (claude.ai, Claude Desktop
and mobile), for one, call the site from Anthropic's cloud, not from the editor's machine, so a site that is only on a
VPN, a private network or split DNS can't be used with them. A site that restricts `/episerver` by IP address must allow Anthropic's outbound range,
`160.79.104.0/21`, for the MCP endpoint, the metadata documents and the `register` and `token` endpoints. The login,
`authorize` and the connections page open in the editor's own browser, so they need what the CMS login needs.
Assistants on the editor's own machine, such as Claude Desktop with the extension and Claude Code, connect from there
instead.

**Dedicated host mode:** `BasePath = ""` with `RequireHost = "mcp.example.com"` puts the module at the root of a host
name of its own (which must reach the same site). The issuer then has no path, which suits clients that don't handle
an issuer with one, and the root `/.well-known` documents on that host are the module's. `RequireHost` with the default
`BasePath` only keeps the module off the site's other host names.
