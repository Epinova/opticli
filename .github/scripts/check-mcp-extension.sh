#!/usr/bin/env bash
# Checks the generic Claude Desktop extension that mcp-extension/build.mjs packs: it asks for the CMS address, carries
# its server and mcp-remote, and (when a version is given) has the release's version.
# Usage: check-mcp-extension.sh <file.mcpb> [version]
set -euo pipefail

file="$1"
expected="${2:-}"

fail() { echo "::error::$1"; exit 1; }

[ -f "$file" ] || fail "$file was not built."

manifest="$(unzip -p "$file" manifest.json)"
name="$(jq -r .name <<<"$manifest")"
version="$(jq -r .version <<<"$manifest")"
site="$(jq -r '.server.mcp_config.args[1]' <<<"$manifest")"
echo "$file: $name $version, address $site"

[ "$name" = "opticli-cms" ] || fail "The extension's name is '$name', not opticli-cms."
[ "$site" = '${user_config.site_url}' ] || fail "The generic extension must ask for the CMS address, not use '$site'."
jq -e '.user_config.site_url.required' <<<"$manifest" >/dev/null || fail "user_config.site_url must be required."
if [ -n "$expected" ] && [ "$version" != "$expected" ]; then
  fail "The extension's version is $version, not $expected."
fi

files="$(unzip -Z1 "$file")"
for f in server/index.js node_modules/mcp-remote/dist/proxy.js; do
  grep -qx "$f" <<<"$files" || fail "$file has no $f."
done
if grep -q '^node_modules/@anthropic-ai/mcpb/' <<<"$files"; then
  fail "$file carries the mcpb packer (a dev dependency)."
fi
