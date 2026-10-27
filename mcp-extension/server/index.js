// Bridges Claude Desktop (stdio) to an opticli MCP module on an Optimizely CMS site, through mcp-remote, which also
// runs the OAuth sign-in in the editor's browser. The first argument is the site: its address
// (https://www.example.com, which gets the module's default path) or the full MCP endpoint URL.

const DEFAULT_PATH = "/episerver/opticli/mcp";

function fail(message) {
  console.error(`opticli: ${message}`);
  process.exit(1);
}

function endpoint(input) {
  const raw = (input ?? "").trim();
  if (!raw || raw.startsWith("${")) {
    fail("no CMS address configured. Open the extension's settings in Claude Desktop and fill in the CMS address.");
  }

  let url;
  try {
    url = new URL(/^[a-z][a-z0-9+.-]*:\/\//i.test(raw) ? raw : `https://${raw}`);
  } catch {
    fail(`'${raw}' is not a web address. Use the site's address, like https://www.example.com.`);
  }

  const loopback = ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
  if (url.protocol !== "https:" && !(url.protocol === "http:" && loopback)) {
    fail(`'${raw}' must start with https:// (plain http only works for localhost).`);
  }
  if (url.username || url.password || url.search || url.hash) {
    fail(`'${raw}' must be a plain address, without a user name, '?' or '#'.`);
  }

  const path = url.pathname.replace(/\/+$/, "");
  if (path === "" || /\/episerver$/i.test(path)) {
    url.pathname = DEFAULT_PATH;
  } else if (/\/opticli$/i.test(path)) {
    url.pathname = `${path}/mcp`;
  } else {
    url.pathname = path;
  }
  return url.href;
}

const url = endpoint(process.argv[2]);
console.error(`opticli: connecting to ${url}`);

// mcp-remote reads its arguments from process.argv when it is imported.
process.argv = [process.argv[0], process.argv[1], url, "--transport", "http-only", "--enable-proxy"];
await import("mcp-remote/dist/proxy.js");
