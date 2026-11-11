// Where the browser opens content and sites on a solution with several sites. opticli gives a write's URL host-relative
// (`url`, a path on the served address) and with its site's host (`canonicalUrl`). A path is right for the site that
// answers the served address; a page on another site is opened on that site's own host, when the served site answers
// it: a `*.localhost` name (browsers send those to this machine) on a port `opticli serve` listens on.

/** The site's own address for the content, when it is another site the served one answers; else the path, as before. */
export function browseUrl(path: string | undefined, canonical: string | undefined, addresses: readonly string[]): string | undefined {
  if (!path || !canonical) return path;
  const url = parse(canonical);
  return url && answered(url, addresses) && !servedHost(url) ? url.href : path;
}

/**
 * Where to open a site (`opticli sites`' `url`): its own address when the served site answers it, `baseURL` for the site
 * on the served address itself (`localhost`, `127.0.0.1`); undefined for a host that doesn't answer here (a production
 * name, another port).
 */
export function siteUrl(url: string | undefined, baseURL: string, addresses: readonly string[]): string | undefined {
  const parsed = url ? parse(url) : undefined;
  if (!parsed || !answered(parsed, addresses)) return undefined;
  return servedHost(parsed) ? baseURL : `${parsed.origin}/`;
}

/** The served site answers it: a name only this machine has, on a port and scheme `serve` listens on. */
function answered(url: URL, addresses: readonly string[]): boolean {
  if (!loopback(url.hostname)) return false;
  return addresses.some((address) => {
    const served = parse(address);
    return served !== undefined && served.protocol === url.protocol && port(served) === port(url);
  });
}

/** The names of the served address itself: content there keeps its path, so it stays on `baseURL`. */
function servedHost(url: URL): boolean {
  return url.hostname === 'localhost' || url.hostname === '127.0.0.1' || url.hostname === '[::1]';
}

function loopback(hostname: string): boolean {
  return hostname === 'localhost' || hostname.endsWith('.localhost') || /^127\.\d+\.\d+\.\d+$/.test(hostname) || hostname === '[::1]';
}

function port(url: URL): string {
  return url.port || (url.protocol === 'https:' ? '443' : '80');
}

function parse(text: string): URL | undefined {
  try {
    return new URL(text);
  } catch {
    return undefined;
  }
}
