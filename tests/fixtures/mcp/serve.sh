#!/usr/bin/env bash
# Starts (or stops) the MCP test site that setup.sh built, on a loopback port, in Development, and waits until the
# MCP module answers. The site runs in the background; its output goes to App_Data/mcp-site-<port>.log.
#
#   serve.sh <site-dir> [--port <port>] [--no-publish] [--no-preview-links] [--inline-blocks on|off] [--stop]
#
# --port              default 5180 (MCP_PORT)
# --no-publish        start with OptiCli__Mcp__AllowPublish=false and OptiCli__Mcp__AllowDelete=false (the module's
#                     defaults for publishing and deleting): the second configuration the E2E tests check, usually on
#                     another port beside the default one (all use the same database)
# --no-preview-links  start with OptiCli__Mcp__AllowPreviewLinks=false (the assistant's preview links, on by default,
#                     turned off): the third configuration, usually on a third port
# --inline-blocks     on or off: start with EPiServer__CmsUI__UI__InlineBlocksInContentAreaEnabled=true|false, so the
#                     edit UI (and the module's writes) make a new block in a ContentArea inline or as a shared block in
#                     the page's "For this page" folder. Without it, the CMS's default: off on CMS 12, on on CMS 13
# --stop              stop the instance on that port
# Other OptiCli__Mcp__* settings in the environment (e.g. OptiCli__Mcp__AccessTokenLifetime=00:00:30) are passed on.
set -euo pipefail

if [ $# -lt 1 ]; then
  sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'
  exit 1
fi
SITE=$(cd "$1" && pwd)
shift
PORT=${MCP_PORT:-5180}
STOP=0
NO_PUBLISH=0
NO_PREVIEW_LINKS=0
INLINE_BLOCKS=
while [ $# -gt 0 ]; do
  case "$1" in
    --port) PORT=$2; shift 2 ;;
    --no-publish) NO_PUBLISH=1; shift ;;
    --no-preview-links) NO_PREVIEW_LINKS=1; shift ;;
    --inline-blocks)
      case "${2:-}" in
        on) INLINE_BLOCKS=true ;;
        off) INLINE_BLOCKS=false ;;
        *) echo "--inline-blocks takes on or off." >&2; exit 1 ;;
      esac
      shift 2 ;;
    --stop) STOP=1; shift ;;
    *) echo "Unknown option $1" >&2; exit 1 ;;
  esac
done
case "$PORT" in ''|*[!0-9]*) echo "--port must be a number." >&2; exit 1 ;; esac

PROJECT=$(ls "$SITE"/*.csproj | head -n 1)
NAME=$(basename "$PROJECT" .csproj)
TFM=$(sed -n 's:.*<TargetFramework>\(.*\)</TargetFramework>.*:\1:p' "$PROJECT" | head -n 1)
DLL="$SITE/bin/Debug/$TFM/$NAME.dll"
PIDFILE="$SITE/App_Data/mcp-site-$PORT.pid"
LOG="$SITE/App_Data/mcp-site-$PORT.log"
URL="http://127.0.0.1:$PORT"

# Only a process this script started, by the pid it wrote: never a pattern that could match something else.
stop() {
  [ -f "$PIDFILE" ] || return 0
  local pid
  pid=$(cat "$PIDFILE")
  if [ -n "$pid" ] && tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null | grep -qF "$DLL"; then
    kill "$pid"
    for _ in $(seq 60); do
      kill -0 "$pid" 2>/dev/null || break
      sleep 0.5
    done
    kill -9 "$pid" 2>/dev/null || true
    echo "stopped $URL (pid $pid)"
  fi
  rm -f "$PIDFILE"
}

stop
if [ "$STOP" = 1 ]; then
  exit 0
fi
if [ ! -f "$DLL" ]; then
  echo "$DLL doesn't exist: run setup.sh (or dotnet build) first." >&2
  exit 1
fi
if curl -s -o /dev/null --max-time 2 "$URL/"; then
  echo "Something else already answers on $URL; stop it or pick another --port." >&2
  exit 1
fi

mkdir -p "$SITE/App_Data"
settings=()
if [ "$NO_PUBLISH" = 1 ]; then
  settings+=("OptiCli__Mcp__AllowPublish=false" "OptiCli__Mcp__AllowDelete=false")
fi
if [ "$NO_PREVIEW_LINKS" = 1 ]; then
  settings+=("OptiCli__Mcp__AllowPreviewLinks=false")
fi
if [ -n "$INLINE_BLOCKS" ]; then
  settings+=("EPiServer__CmsUI__UI__InlineBlocksInContentAreaEnabled=$INLINE_BLOCKS")
fi
cd "$SITE"
# setsid: the site outlives this shell (and the container session that ran it).
env ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="$URL" "${settings[@]}" \
  setsid dotnet "$DLL" > "$LOG" 2>&1 < /dev/null &
echo $! > "$PIDFILE"

# The module's default path: /episerver/opticli on CMS 12, /optimizely/opticli on CMS 13.
module=
answers() {
  for path in episerver/opticli optimizely/opticli; do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "$URL/.well-known/oauth-protected-resource/$path/mcp")" = 200 ]; then
      module=$path
      return 0
    fi
  done
  return 1
}
for _ in $(seq 300); do
  if ! kill -0 "$(cat "$PIDFILE")" 2>/dev/null; then
    echo "The site exited while starting; the end of $LOG:" >&2
    tail -n 40 "$LOG" >&2
    rm -f "$PIDFILE"
    exit 1
  fi
  if answers; then
    break
  fi
  sleep 1
done
if ! answers; then
  echo "The MCP module didn't answer on $URL/.well-known/oauth-protected-resource/{episerver,optimizely}/opticli/mcp in 5 minutes; see $LOG." >&2
  exit 1
fi
if grep -E '\[(mcp-fixture|edge-cases)\] setup failed' -A3 "$LOG" >&2; then
  exit 1
fi
publish=$(curl -s "$URL/.well-known/oauth-authorization-server/$module" | jq -r '.scopes_supported | index("content:publish") != null')
blocks=$(curl -s --max-time 5 "$URL/opticli-fixture/inline-blocks" || true)
echo "running: $URL (pid $(cat "$PIDFILE"), publishing allowed: $publish, new area blocks: ${blocks:-unknown}, log $LOG)"
