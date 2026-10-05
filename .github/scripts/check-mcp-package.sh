#!/usr/bin/env bash
# Checks a packed OptiCli.Mcp .nupkg: it is the -preview prerelease of the release version, has the module assembly,
# and keeps the CMS dependencies within CMS 12. Usage: check-mcp-package.sh <dir with the .nupkg> <version>
set -euo pipefail

expected="$2-preview"
package="$1/OptiCli.Mcp.$expected.nupkg"
if [ ! -f "$package" ]; then
  echo "::error::No OptiCli.Mcp.$expected.nupkg in $1 (found: $(cd "$1" && ls OptiCli.Mcp.*.nupkg 2>/dev/null | tr '\n' ' '))."
  exit 1
fi

contents="$(mktemp -d)"
trap 'rm -rf "$contents"' EXIT
unzip -q "$package" -d "$contents"

if [ ! -f "$contents/lib/net8.0/OptiCli.Mcp.dll" ]; then
  echo "::error::$(basename "$package") has no lib/net8.0/OptiCli.Mcp.dll."
  exit 1
fi

nuspec="$contents/OptiCli.Mcp.nuspec"
for dependency in 'EPiServer.CMS.Core" version="\[12.12.1, 13.0.0)' 'EPiServer.CMS.UI.Core" version="\[12.16.1, 13.0.0)'; do
  if ! grep -q "id=\"$dependency\"" "$nuspec"; then
    echo "::error::$(basename "$package") doesn't depend on ${dependency//\\/}\"."
    grep -o '<dependency [^>]*>' "$nuspec" >&2
    exit 1
  fi
done
echo "$(basename "$package"): OptiCli.Mcp $expected, CMS 12 dependencies as expected"
