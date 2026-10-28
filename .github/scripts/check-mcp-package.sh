#!/usr/bin/env bash
# Checks the packed MCP module packages, which are released together: OptiCli.Mcp (CMS 12) and OptiCli.Mcp.Cms13
# (CMS 13). Each has the release version, has the module assembly for its one target framework only, and keeps its CMS
# dependencies within its own CMS major. Usage: check-mcp-package.sh <dir with the .nupkg files> <version>
set -euo pipefail

dir="$1"
expected="$2"
contents="$(mktemp -d)"
trap 'rm -rf "$contents"' EXIT

# check <package id> <target framework> <dependency: id and range as in the nuspec, regex-escaped>...
check() {
  local id="$1" framework="$2"
  shift 2
  local package="$dir/$id.$expected.nupkg"
  if [ ! -f "$package" ]; then
    echo "::error::No $id.$expected.nupkg in $dir (found: $(cd "$dir" && ls OptiCli.Mcp.*.nupkg 2>/dev/null | tr '\n' ' '))."
    exit 1
  fi

  local unpacked="$contents/$id"
  unzip -q "$package" -d "$unpacked"

  if [ ! -f "$unpacked/lib/$framework/OptiCli.Mcp.dll" ]; then
    echo "::error::$(basename "$package") has no lib/$framework/OptiCli.Mcp.dll."
    exit 1
  fi
  local frameworks
  frameworks="$(ls "$unpacked/lib")"
  if [ "$frameworks" != "$framework" ]; then
    echo "::error::$(basename "$package") must have lib/$framework only, not: $(tr '\n' ' ' <<<"$frameworks")."
    exit 1
  fi

  local nuspec="$unpacked/$id.nuspec" dependency
  for dependency in "$@"; do
    if ! grep -q "id=\"$dependency\"" "$nuspec"; then
      echo "::error::$(basename "$package") doesn't depend on ${dependency//\\/}\"."
      grep -o '<dependency [^>]*>' "$nuspec" >&2
      exit 1
    fi
  done
  echo "$(basename "$package"): $id $expected, lib/$framework, CMS dependencies as expected"
}

check OptiCli.Mcp net8.0 \
  'EPiServer.CMS.Core" version="\[12.12.1, 13.0.0)' \
  'EPiServer.CMS.UI.Core" version="\[12.16.1, 13.0.0)'
check OptiCli.Mcp.Cms13 net10.0 \
  'EPiServer.CMS.Core" version="\[13.0.0, 14.0.0)' \
  'EPiServer.CMS.UI.Core" version="\[13.0.0, 14.0.0)' \
  'EPiServer.ImageLibrary" version="\[13.0.0, 14.0.0)'
