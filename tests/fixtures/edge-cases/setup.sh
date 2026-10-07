#!/usr/bin/env bash
# Builds the edge-case site for opticli's integration tests: a copy of an Alloy site and of its database, with
# EdgeCasesFixture.cs, JobsFixture.cs, UsersFixture.cs, OrphansFixture.cs and Cms12Fixture.cs added (on CMS 13,
# Cms13Fixture.cs instead of Cms12Fixture.cs), and the content of edge-cases.plan.json. The
# original site and database are not changed. The source can be CMS 12's Alloy or CMS 13's (tests/fixtures/cms13/
# setup.sh's /demo/Alloy13, database alloy13).
#
#   setup.sh <alloy-project-dir> <target-dir> [<source-db> [<target-db>]]
#
# Needs sqlcmd, jq and opticli on PATH (OPTICLI overrides the command, e.g. a checkout's build), and SQL Server
# credentials in SQLCMDSERVER, SQLCMDUSER and SQLCMDPASSWORD (default: localhost,1433 and sa). Run it again to update
# the content: the database copy is kept unless FRESH=1.
set -euo pipefail

if [ $# -lt 2 ]; then
  sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
  exit 1
fi
HERE=$(cd "$(dirname "$0")" && pwd)
SOURCE=$(cd "$1" && pwd)
TARGET=$2
SOURCE_DB=${3:-alloy-demo}
TARGET_DB=${4:-alloy-edge}
OPTICLI=${OPTICLI:-opticli}
export SQLCMDSERVER=${SQLCMDSERVER:-localhost,1433} SQLCMDUSER=${SQLCMDUSER:-sa}
: "${SQLCMDPASSWORD:?set SQLCMDPASSWORD to the SQL Server password}"
export SQLCMDPASSWORD

if [ "$SOURCE_DB" = "$TARGET_DB" ]; then
  echo "The target database must not be the source database ($SOURCE_DB)." >&2
  exit 1
fi
case "$TARGET_DB" in *[!A-Za-z0-9_-]*) echo "Database names may only have letters, digits, '_' and '-'." >&2; exit 1 ;; esac
case "$SOURCE_DB" in *[!A-Za-z0-9_-]*) echo "Database names may only have letters, digits, '_' and '-'." >&2; exit 1 ;; esac

echo "== site: $SOURCE -> $TARGET"
mkdir -p "$TARGET"
TARGET=$(cd "$TARGET" && pwd)
if [ "$SOURCE" = "$TARGET" ]; then
  echo "The target directory must not be the source site." >&2
  exit 1
fi
rsync -a --delete --exclude bin/ --exclude obj/ --exclude App_Data/blobs/ --exclude EdgeCases/ "$SOURCE/" "$TARGET/"
# Media files are blobs on disk, not in the database: the copy needs them too.
if [ -d "$SOURCE/App_Data/blobs" ]; then
  rsync -a "$SOURCE/App_Data/blobs/" "$TARGET/App_Data/blobs/"
fi
# Only this run's fixtures: one left from an earlier run (a renamed file) would clash with them.
rm -rf "$TARGET/EdgeCases"
mkdir -p "$TARGET/EdgeCases"
project=$(find "$TARGET" -maxdepth 1 -name '*.csproj' | head -n 1)
cms_major=$(sed -n 's:.*Include="EPiServer\.CMS" Version="\([0-9]*\)\..*:\1:p' "$project" | head -n 1)
fixtures=("$HERE/EdgeCasesFixture.cs" "$HERE/JobsFixture.cs" "$HERE/UsersFixture.cs" "$HERE/OrphansFixture.cs")
if [ "${cms_major:-12}" -ge 13 ]; then
  # Cms13Fixture.cs turns on visitor groups, which CMS 13 only registers when the site asks, makes the hosts sites as
  # applications with start pages of their own (no nested site: CMS 13 refuses applications whose start pages
  # overlap), and saves the orphans fixture's properties through the content type repository.
  echo "== CMS $cms_major: with Cms13Fixture.cs"
  fixtures+=("$HERE/Cms13Fixture.cs")
else
  fixtures+=("$HERE/Cms12Fixture.cs")
fi
cp "${fixtures[@]}" "$TARGET/EdgeCases/"
# The fixture names Alloy's StandardPage; the CMS 13 template's Alloy lives in the project's own namespace (Alloy13).
namespace=$(sed -n 's:.*<RootNamespace>\(.*\)</RootNamespace>.*:\1:p' "$project" | head -n 1)
namespace=${namespace:-$(basename "$project" .csproj)}
sed -i "s/typeof(Alloy\.Models\./typeof($namespace.Models./g" "$TARGET/EdgeCases/EdgeCasesFixture.cs"
for settings in "$TARGET"/appsettings*.json; do
  sed -i "s/Database=$SOURCE_DB;/Database=$TARGET_DB;/g; s/Initial Catalog=$SOURCE_DB;/Initial Catalog=$TARGET_DB;/g" "$settings"
done
if ! grep -q "$TARGET_DB" "$TARGET"/appsettings*.json; then
  echo "No connection string to $SOURCE_DB found in $TARGET/appsettings*.json." >&2
  exit 1
fi

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

cd "$TARGET"
restart() {
  $OPTICLI serve --stop >/dev/null 2>&1 || true
  $OPTICLI serve "$@" | jq -c 'if .ok then .data | {state, url} else .error | {code, message, hint} end'
}
echo "== build and start (creates the visitor group the plan uses)"
restart --build
echo "== plan"
if ! result=$($OPTICLI apply "$HERE/edge-cases.plan.json" --update-existing); then
  echo "$result" | jq '.error | {code, message, hint}'
  echo "$result" | jq -c '.error.details.operations[]? | {index, op, id, status, error: .error.message}'
  exit 1
fi
echo "$result" | jq -c '.data.operations[] | {index, op, id, status}'
echo "== restart (creates the nested site (CMS 12), the hosts sites, the approval sequence, the language settings and the orphans)"
restart
if $OPTICLI serve --logs --tail 400 | jq -r '.data.lines[]' | grep -A3 '\[edge-cases\] setup failed'; then
  exit 1
fi
$OPTICLI sites | jq -c '.data[] | {name, startPage}'
cat <<EOF

Done. Run the integration tests against it:
  OPTICLI_IT_PROJECT=$TARGET OPTICLI_IT_PLAN=$HERE/edge-cases.plan.json dotnet test tests/OptiCli.Integration
EOF
