#!/usr/bin/env bash
# Runs the MCP module's project checks (tests/OptiCli.Mcp.Integration/ProjectTests.cs) against MCP test sites that
# tests/fixtures/mcp/setup.sh built from this checkout: for each site, it rebuilds the site with this checkout's module
# and fixture files (the MCP fixture's and the edge-case site's), starts it on <port> (publishing allowed) and <port>+1 (publishing off), and runs the tests against
# both. With --project-mode it also starts a CMS 13 site on <port>+3 with project mode on (CMS 13's default is the
# Projects gadget) and runs the tests there, which then expect projectUrl instead of projectNote.
#
#   check-mcp.sh [--all] [--project-mode] <site-dir>:<port> [<site-dir>:<port> ...]
#
# --all           the whole MCP end-to-end suite, not only ProjectTests
# --project-mode  for a CMS 13 site: the extra run with EPiServer__CmsUI__ProjectUI__ProjectModeEnabled=true
#
# Run it where dotnet runs (inside the dev container). The sites are left running on <port> and <port>+1.
# Example (sites built with MCP_PORT=5250 setup.sh <AlloyEdge> <ProjMcp12> alloy-edge alloy-projmcp12, and the same for
# CMS 13 on 5260):
#   tests/fixtures/projects/check-mcp.sh --project-mode ~/demo/ProjMcp12:5250 ~/demo/ProjMcp13:5260
set -euo pipefail

if [ $# -lt 1 ] || [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
  sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'
  exit 1
fi
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../.." && pwd)
SERVE="$REPO/tests/fixtures/mcp/serve.sh"
FILTER="--filter FullyQualifiedName~ProjectTests"
PROJECT_MODE=0
sites=()
for arg in "$@"; do
  case "$arg" in
    --all) FILTER="" ;;
    --project-mode) PROJECT_MODE=1 ;;
    *:*) sites+=("$arg") ;;
    *) echo "Unknown argument $arg (sites are <dir>:<port>)" >&2; exit 1 ;;
  esac
done

dotnet build "$REPO/tests/OptiCli.Mcp.Integration" -nologo -v quiet -clp:ErrorsOnly
failed=0
for entry in "${sites[@]}"; do
  site=$(cd "${entry%:*}" && pwd)
  port=${entry##*:}
  users="$site/App_Data/mcp-test-users.json"
  echo "== $site: rebuild with this checkout's module, start on $port and $((port + 1)) (publishing off)"
  "$SERVE" "$site" --port "$port" --stop
  "$SERVE" "$site" --port $((port + 1)) --stop
  # The fixture files the site has, as this checkout has them (setup.sh copied them once): the MCP fixture's, and the
  # edge-case site's it was copied from (with the site's namespace for Alloy's, as edge-cases/setup.sh writes them).
  for fixture in "$site"/McpFixture/*.cs; do
    if [ -f "$REPO/tests/fixtures/mcp/${fixture##*/}" ]; then
      cp "$REPO/tests/fixtures/mcp/${fixture##*/}" "$fixture"
    fi
  done
  csproj=$(find "$site" -maxdepth 1 -name '*.csproj' | head -n 1)
  namespace=$(sed -n 's:.*<RootNamespace>\(.*\)</RootNamespace>.*:\1:p' "$csproj" | head -n 1)
  namespace=${namespace:-$(basename "$csproj" .csproj)}
  for fixture in "$site"/EdgeCases/*.cs; do
    if [ -f "$REPO/tests/fixtures/edge-cases/${fixture##*/}" ]; then
      sed "s/typeof(Alloy\.Models\./typeof($namespace.Models./g" "$REPO/tests/fixtures/edge-cases/${fixture##*/}" > "$fixture"
    fi
  done
  (cd "$site" && dotnet build -nologo -v quiet -clp:ErrorsOnly)
  "$SERVE" "$site" --port "$port"
  "$SERVE" "$site" --port $((port + 1)) --no-publish
  # shellcheck disable=SC2086
  OPTICLI_MCP_IT_URL="http://127.0.0.1:$port" OPTICLI_MCP_IT_NO_PUBLISH_URL="http://127.0.0.1:$((port + 1))" OPTICLI_MCP_IT_USERS="$users" \
    dotnet test "$REPO/tests/OptiCli.Mcp.Integration" --no-build -nologo $FILTER || failed=1

  major=$(sed -n 's:.*Include="EPiServer\.CMS" Version="\([0-9]*\)\..*:\1:p' "$site"/*.csproj | head -n 1)
  if [ "$PROJECT_MODE" = 1 ] && [ "${major:-12}" -ge 13 ]; then
    echo "== $site: project mode on, on $((port + 3))"
    EPiServer__CmsUI__ProjectUI__ProjectModeEnabled=true "$SERVE" "$site" --port $((port + 3))
    # shellcheck disable=SC2086
    OPTICLI_MCP_IT_EDITOR_UI=projectMode OPTICLI_MCP_IT_URL="http://127.0.0.1:$((port + 3))" OPTICLI_MCP_IT_USERS="$users" \
      dotnet test "$REPO/tests/OptiCli.Mcp.Integration" --no-build -nologo --filter FullyQualifiedName~ProjectTests || failed=1
    "$SERVE" "$site" --port $((port + 3)) --stop
  fi
done
exit $failed
