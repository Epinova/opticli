// Bridges Claude Desktop (stdio) to an opticli MCP module on an Optimizely CMS site, through mcp-remote, which also
// runs the OAuth sign-in in the editor's browser. The first argument is the site: its address
// (https://www.example.com, which gets the module's default path) or the full MCP endpoint URL.

import { execFileSync } from "node:child_process";
import { readdirSync, statSync } from "node:fs";
import { join } from "node:path";

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

// Claude Desktop on Linux starts extensions with D-Bus turned off (DBUS_SESSION_BUS_ADDRESS=disabled:, which it sets
// when it reads the environment from the user's login shell), and can start them without the desktop session's
// variables (DISPLAY, WAYLAND_DISPLAY, XDG_SESSION_TYPE...). The sign-in page then never opens, or a browser starts
// that can't find the one already running ("Firefox is already running": on Wayland, Firefox looks for it over D-Bus),
// while mcp-remote waits for the sign-in. Put back the user's session bus, whether or not the display came through, and
// fill in a missing display from the systemd user session, which desktops (KDE, GNOME) keep up to date, or else from
// the user's own Wayland and X sockets. Not the desktop's name (XDG_CURRENT_DESKTOP, KDE_FULL_SESSION...): with it,
// xdg-open on KDE hands the link to KDE's opener, which can download the sign-in page and open that file instead of the
// link.
const SESSION_VARIABLES = [
  "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "XDG_RUNTIME_DIR", "XDG_SESSION_TYPE", "XDG_DATA_DIRS", "XDG_CONFIG_DIRS",
];

function fromUserSession() {
  const session = execFileSync("systemctl", ["--user", "show-environment"], {
    encoding: "utf8",
    timeout: 5000,
    stdio: ["ignore", "pipe", "pipe"],
  });
  const found = {};
  for (const line of session.split("\n")) {
    const at = line.indexOf("=");
    if (at > 0 && SESSION_VARIABLES.includes(line.slice(0, at))) found[line.slice(0, at)] = line.slice(at + 1);
  }
  if (!found.DISPLAY && !found.WAYLAND_DISPLAY) throw new Error("it has no display");
  return found;
}

function fromSockets() {
  const uid = process.getuid();
  const ours = (path) => {
    try {
      return statSync(path).uid === uid;
    } catch {
      return false;
    }
  };
  const list = (dir) => {
    try {
      return readdirSync(dir).sort();
    } catch {
      return [];
    }
  };

  const found = {};
  const runtime = process.env.XDG_RUNTIME_DIR || `/run/user/${uid}`;
  const wayland = list(runtime).find((f) => /^wayland-\d+$/.test(f) && ours(join(runtime, f)));
  if (wayland) Object.assign(found, { XDG_RUNTIME_DIR: runtime, WAYLAND_DISPLAY: wayland });
  const x = list("/tmp/.X11-unix").find((f) => /^X\d+$/.test(f) && ours(join("/tmp/.X11-unix", f)));
  if (x) {
    found.DISPLAY = `:${x.slice(1)}`;
    const auth = list(runtime).find((f) => /^(xauth_|\.mutter-Xwaylandauth\.)/.test(f));
    if (auth) found.XAUTHORITY = join(runtime, auth);
  }
  if (wayland || x) found.XDG_SESSION_TYPE = wayland ? "wayland" : "x11";
  return found;
}

function restoreSessionBus() {
  const address = process.env.DBUS_SESSION_BUS_ADDRESS;
  if (address && !address.startsWith("disabled:")) return;
  const bus = `${process.env.XDG_RUNTIME_DIR || `/run/user/${process.getuid()}`}/bus`;
  if (statSync(bus, { throwIfNoEntry: false })?.uid !== process.getuid()) return;
  process.env.DBUS_SESSION_BUS_ADDRESS = `unix:path=${bus}`;
  console.error(`opticli: D-Bus was ${address ?? "not set"} for the extension, using the session bus ${bus}`);
}

function importDesktopSession() {
  if (process.platform !== "linux") return;
  restoreSessionBus();
  if (process.env.DISPLAY || process.env.WAYLAND_DISPLAY) return;
  let found;
  try {
    found = fromUserSession();
  } catch (error) {
    console.error(`opticli: no display from Claude Desktop or the systemd user session (${error.message.split("\n")[0]})`);
    found = fromSockets();
  }
  const imported = Object.keys(found).filter((name) => !process.env[name]);
  for (const name of imported) process.env[name] = found[name];
  console.error(`opticli: no display from Claude Desktop, using ${imported.map((n) => `${n}=${found[n]}`).join(" ") || "none"}`);
}

const url = endpoint(process.argv[2]);
console.error(`opticli: connecting to ${url}`);
importDesktopSession();

// mcp-remote reads its arguments from process.argv when it is imported.
process.argv = [process.argv[0], process.argv[1], url, "--transport", "http-only", "--enable-proxy"];
await import("mcp-remote/dist/proxy.js");
