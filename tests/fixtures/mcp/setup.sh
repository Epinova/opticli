#!/usr/bin/env bash
# Builds the MCP test site for the MCP module's end-to-end tests (tests/OptiCli.Mcp.Integration): a copy of the
# edge-case site (tests/fixtures/edge-cases/setup.sh) and of its database, with this repository's src/OptiCli.Mcp
# added, McpFixture.cs (test users and access rights), and a client ID metadata document. Then starts it with serve.sh.
# The edge-case site and its database are not changed.
#
#   setup.sh [<edge-site-dir> [<target-dir> [<source-db> [<target-db>]]]]
#
# Defaults: /demo/AlloyEdge -> /demo/AlloyMcp, database alloy-edge -> alloy-mcp. Needs dotnet, sqlcmd, rsync, curl,
# jq and python3, and SQL Server credentials in SQLCMDSERVER, SQLCMDUSER and SQLCMDPASSWORD (default: localhost,1433
# and sa). The database copy is kept on later runs unless FRESH=1. MCP_PORT is the site's port (default 5180).
set -euo pipefail

if [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
  sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
  exit 1
fi
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../.." && pwd)
MODULE="$REPO/src/OptiCli.Mcp/OptiCli.Mcp.csproj"
SOURCE=$(cd "${1:-/demo/AlloyEdge}" && pwd)
TARGET=${2:-/demo/AlloyMcp}
SOURCE_DB=${3:-alloy-edge}
TARGET_DB=${4:-alloy-mcp}
PORT=${MCP_PORT:-5180}
export SQLCMDSERVER=${SQLCMDSERVER:-localhost,1433} SQLCMDUSER=${SQLCMDUSER:-sa}
: "${SQLCMDPASSWORD:?set SQLCMDPASSWORD to the SQL Server password}"
export SQLCMDPASSWORD

if [ "$SOURCE_DB" = "$TARGET_DB" ]; then
  echo "The target database must not be the source database ($SOURCE_DB)." >&2
  exit 1
fi
case "$TARGET_DB" in *[!A-Za-z0-9_-]*) echo "Database names may only have letters, digits, '_' and '-'." >&2; exit 1 ;; esac
case "$SOURCE_DB" in *[!A-Za-z0-9_-]*) echo "Database names may only have letters, digits, '_' and '-'." >&2; exit 1 ;; esac
case "$PORT" in ''|*[!0-9]*) echo "MCP_PORT must be a number." >&2; exit 1 ;; esac
if [ ! -f "$MODULE" ]; then
  echo "$MODULE doesn't exist: run this script from an opticli checkout." >&2
  exit 1
fi

echo "== site: $SOURCE -> $TARGET"
mkdir -p "$TARGET"
TARGET=$(cd "$TARGET" && pwd)
if [ "$SOURCE" = "$TARGET" ]; then
  echo "The target directory must not be the source site." >&2
  exit 1
fi
# Every instance serve.sh started here, before its files and database are replaced.
for pidfile in "$TARGET"/App_Data/mcp-site-*.pid; do
  [ -e "$pidfile" ] || continue
  port=${pidfile##*/mcp-site-}
  "$HERE/serve.sh" "$TARGET" --port "${port%.pid}" --stop
done
# App_Data/mcp-* (the test users' passwords, the logs) belongs to the copy and survives; so do its media blobs.
rsync -a --delete --exclude bin/ --exclude obj/ --exclude App_Data/blobs/ --exclude 'App_Data/mcp-*' --exclude McpFixture/ \
  "$SOURCE/" "$TARGET/"
if [ -d "$SOURCE/App_Data/blobs" ]; then
  rsync -a "$SOURCE/App_Data/blobs/" "$TARGET/App_Data/blobs/"
fi
mkdir -p "$TARGET/McpFixture"
cp "$HERE/McpFixture.cs" "$TARGET/McpFixture/"
for settings in "$TARGET"/appsettings*.json; do
  sed -i "s/Database=$SOURCE_DB;/Database=$TARGET_DB;/g; s/Initial Catalog=$SOURCE_DB;/Initial Catalog=$TARGET_DB;/g" "$settings"
done
if ! grep -q "$TARGET_DB" "$TARGET"/appsettings*.json; then
  echo "No connection string to $SOURCE_DB found in $TARGET/appsettings*.json." >&2
  exit 1
fi

echo "== module: $MODULE"
# The site and Startup.cs come fresh from the source on every run; each change is still made only once.
python3 - "$TARGET" "$MODULE" <<'PY'
import glob, sys
target, module = sys.argv[1], sys.argv[2]

def patch(path, changes):
    with open(path, encoding="utf-8") as f:
        text = f.read()
    for done, anchor, replacement in changes:
        if done in text:
            continue
        if anchor not in text:
            sys.exit(f"{path}: can't find {anchor!r} to patch")
        text = text.replace(anchor, replacement, 1)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)

project = sorted(glob.glob(f"{target}/*.csproj"))[0]
patch(project, [(
    "OptiCli.Mcp.csproj",
    "</Project>",
    f"""  <ItemGroup>
    <!-- opticli MCP test site (tests/fixtures/mcp/setup.sh): a site would reference the OptiCli.Mcp package instead. -->
    <ProjectReference Include="{module}" />
  </ItemGroup>
</Project>""")])

patch(f"{target}/Startup.cs", [
    ("using OptiCli.Mcp;", "using Alloy.Extensions;", "using Alloy.Extensions;\nusing OptiCli.Mcp;"),
    ("AddOptiCliMcp(", "        // Required by Wangkanai.Detection", """        // opticli MCP test site (tests/fixtures/mcp/setup.sh): publishing and deleting on, product editors may connect,
        // high rate limits.
        services.AddOptiCliMcp(o =>
        {
            o.AllowPublish = true;
            o.AllowDelete = true;
            o.AllowedRoles = [.. o.AllowedRoles, "ProductEditors"];
            // The end-to-end tests sign in many times a minute from one address: well above what a site needs.
            o.RateLimits = new() { RegisterPerMinute = 1000, TokenPerMinute = 1000, TokenPerAddressPerMinute = 5000, AuthorizePerMinute = 1000 };
        });
        // ...unless the OptiCli:Mcp settings say otherwise (OptiCli__Mcp__AllowPublish=false), for the tests' second configuration.
        services.AddOptions<OptiCliMcpOptions>().PostConfigure<IConfiguration>(OptiCliMcpFixture.McpFixture.ApplyConfiguration);

        // Required by Wangkanai.Detection"""),
    ("MapOptiCliMcp()", "endpoints.MapContent();", "endpoints.MapOptiCliMcp();\n            endpoints.MapContent();"),
])
PY

# A client ID metadata document on the site itself, for the CIMD test: the module fetches http loopback documents
# only in Development. Its client_id must be its own URL, so it names the port.
cat > "$TARGET/wwwroot/mcp-test-client.json" <<EOF
{
  "client_id": "http://127.0.0.1:$PORT/mcp-test-client.json",
  "client_name": "opticli E2E tests (CIMD)",
  "redirect_uris": ["http://127.0.0.1:53682/callback"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "token_endpoint_auth_method": "none"
}
EOF

exists=$(sqlcmd -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'$TARGET_DB'")
if [ "${FRESH:-0}" = 1 ] || [ "$exists" = 0 ]; then
  echo "== database: $SOURCE_DB -> $TARGET_DB"
  sqlcmd -b -Q "
    DECLARE @bak nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000)) + N'/$TARGET_DB.bak';
    DECLARE @dir nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000));
    DECLARE @data sysname = (SELECT name FROM sys.master_files WHERE database_id = DB_ID(N'$SOURCE_DB') AND type = 0);
    DECLARE @log sysname = (SELECT name FROM sys.master_files WHERE database_id = DB_ID(N'$SOURCE_DB') AND type = 1);
    DECLARE @dataFile nvarchar(4000) = @dir + N'$TARGET_DB.mdf', @logFile nvarchar(4000) = @dir + N'${TARGET_DB}_log.ldf';
    BACKUP DATABASE [$SOURCE_DB] TO DISK = @bak WITH COPY_ONLY, INIT;
    IF DB_ID(N'$TARGET_DB') IS NOT NULL ALTER DATABASE [$TARGET_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    RESTORE DATABASE [$TARGET_DB] FROM DISK = @bak WITH REPLACE, MOVE @data TO @dataFile, MOVE @log TO @logFile;
    ALTER DATABASE [$TARGET_DB] SET MULTI_USER;"
fi

echo "== build"
(cd "$TARGET" && dotnet build -nologo -v quiet -clp:ErrorsOnly)

echo "== start"
"$HERE/serve.sh" "$TARGET" --port "$PORT"
users="$TARGET/App_Data/mcp-test-users.json"
for _ in $(seq 30); do
  [ -s "$users" ] && break
  sleep 1
done
if [ ! -s "$users" ]; then
  echo "The fixture didn't write $users; see $TARGET/App_Data/mcp-site-$PORT.log." >&2
  exit 1
fi
cat <<EOF

Done: $TARGET runs on http://127.0.0.1:$PORT (stop it with: $HERE/serve.sh $TARGET --port $PORT --stop).
Run the end-to-end tests against it, from $REPO:
  OPTICLI_MCP_IT_URL=http://127.0.0.1:$PORT OPTICLI_MCP_IT_USERS=$users dotnet test tests/OptiCli.Mcp.Integration
With the second configuration (publishing off) as well, on another port:
  $HERE/serve.sh $TARGET --port $((PORT + 1)) --no-publish
  OPTICLI_MCP_IT_URL=http://127.0.0.1:$PORT OPTICLI_MCP_IT_NO_PUBLISH_URL=http://127.0.0.1:$((PORT + 1)) OPTICLI_MCP_IT_USERS=$users dotnet test tests/OptiCli.Mcp.Integration
  $HERE/serve.sh $TARGET --port $((PORT + 1)) --stop
EOF
