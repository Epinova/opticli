#!/usr/bin/env bash
# Runs the shared-database drift scenario against a copy of the edge-case site (CMS 12's or CMS 13's): a copy of its
# database reached by a host name (so opticli treats it as remote), and code changes in the copy of the site. The
# edge-case site and its database are not changed.
#
#   drift.sh <edge-site-dir> <target-dir> [<source-db> [<target-db>]]
#
# Needs sqlcmd, jq and opticli (OPTICLI overrides the command), and SQL Server credentials in SQLCMDSERVER, SQLCMDUSER
# and SQLCMDPASSWORD (default: localhost,1433 and sa). DRIFT_HOST is the remote-looking name of the same server
# (default: <hostname>.localhost, which resolves to the loopback address). opticli's config and state for the copy are
# kept in <target-dir>/.opticli, so the user's own aren't touched. It stops at the first unexpected result.
set -euo pipefail

if [ $# -lt 2 ]; then
  sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
  exit 1
fi
SOURCE=$(cd "$1" && pwd)
TARGET=$2
SOURCE_DB=${3:-alloy-edge}
TARGET_DB=${4:-alloy-edge-shared}
OPTICLI=${OPTICLI:-opticli}
HOST=${DRIFT_HOST:-$(hostname).localhost}
export SQLCMDSERVER=${SQLCMDSERVER:-localhost,1433} SQLCMDUSER=${SQLCMDUSER:-sa}
: "${SQLCMDPASSWORD:?set SQLCMDPASSWORD to the SQL Server password}"
export SQLCMDPASSWORD

if [ "$SOURCE_DB" = "$TARGET_DB" ]; then
  echo "The target database must not be the source database ($SOURCE_DB)." >&2
  exit 1
fi
case "$TARGET_DB$SOURCE_DB" in *[!A-Za-z0-9_-]*) echo "Database names may only have letters, digits, '_' and '-'." >&2; exit 1 ;; esac

mkdir -p "$TARGET"
TARGET=$(cd "$TARGET" && pwd)
if [ "$SOURCE" = "$TARGET" ]; then
  echo "The target directory must not be the edge-case site." >&2
  exit 1
fi
export XDG_CONFIG_HOME=$TARGET/.opticli/config XDG_STATE_HOME=$TARGET/.opticli/state
cd "$TARGET"
$OPTICLI serve --stop >/dev/null 2>&1 || true

echo "== site: $SOURCE -> $TARGET, database: $SOURCE_DB -> $TARGET_DB on $HOST"
rsync -a --delete --exclude bin/ --exclude obj/ --exclude .opticli/ "$SOURCE/" "$TARGET/"
sed -i "s/Server=[^;]*;Database=$SOURCE_DB;/Server=$HOST,1433;Database=$TARGET_DB;/g" "$TARGET"/appsettings.Development.json
grep -q "Server=$HOST,1433;Database=$TARGET_DB;" "$TARGET"/appsettings.Development.json
sqlcmd -b -Q "
  DECLARE @bak nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000)) + N'/$TARGET_DB.bak';
  DECLARE @dir nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000));
  DECLARE @data sysname = (SELECT name FROM sys.master_files WHERE database_id = DB_ID(N'$SOURCE_DB') AND type = 0);
  DECLARE @log sysname = (SELECT name FROM sys.master_files WHERE database_id = DB_ID(N'$SOURCE_DB') AND type = 1);
  DECLARE @dataFile nvarchar(4000) = @dir + N'$TARGET_DB.mdf', @logFile nvarchar(4000) = @dir + N'${TARGET_DB}_log.ldf';
  BACKUP DATABASE [$SOURCE_DB] TO DISK = @bak WITH COPY_ONLY, INIT;
  IF DB_ID(N'$TARGET_DB') IS NOT NULL ALTER DATABASE [$TARGET_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
  RESTORE DATABASE [$TARGET_DB] FROM DISK = @bak WITH REPLACE, MOVE @data TO @dataFile, MOVE @log TO @logFile;
  ALTER DATABASE [$TARGET_DB] SET MULTI_USER;" >/dev/null
# Running this script is the choice of the copy as the development database.
$OPTICLI db use "$($OPTICLI db list | jq -r --arg db "$TARGET_DB" '.data.choices[] | select(.database == $db) | .id')" >/dev/null
LOCAL=$(jq -r '.ConnectionStrings.EPiServerDB' "$TARGET"/appsettings.Development.json | sed "s/Server=$HOST,1433;/Server=localhost,1433;/")

build() { dotnet build 2>&1 | grep -q " 0 Error(s)" || { dotnet build; exit 1; }; }
# The edge-case site's OrphansFixture.cs keeps content types whose class no build has (`opticli types --orphaned`):
# drift lists them as the database's (ahead "database") on every run, and on CMS 13 its admin-mode type EdgeAdminPage as
# of unknown origin (ahead "unknown"). `own` leaves them out of a list of differences.
ORPHANS='["EdgeAdminPage", "EdgeRemovedBlock", "EdgeRemovedEmptyPage", "EdgeRemovedPage", "EdgeTrashedPage"]'
expect() { # <jq filter that must be true> <json>
  if ! jq -e --argjson orphans "$ORPHANS" 'def own: map(select(.name as $n | $orphans | index($n) | not)); '"$1" >/dev/null <<<"$2"; then
    echo "unexpected (wanted $1):" >&2
    jq . <<<"$2" >&2
    exit 1
  fi
}
edit() { python3 - "$@"; }
article=Models/Pages/ArticlePage.cs
standard=Models/Pages/StandardPage.cs
# The site's namespace: Alloy on CMS 12, the CMS 13 template's project name (Alloy13).
project=$(find . -maxdepth 1 -name '*.csproj' | head -n 1)
namespace=$(sed -n 's:.*<RootNamespace>\(.*\)</RootNamespace>.*:\1:p' "$project" | head -n 1)
namespace=${namespace:-$(basename "$project" .csproj)}

echo "== 1. nothing differs (apart from the fixture's orphaned types)"
build
out=$($OPTICLI serve)
expect '.ok and (.data.drift.differences == 0 or .data.drift.ahead == "database")' "$out"
out=$($OPTICLI drift)
expect '(.data.contentTypes | own) == [] and all(.data.contentTypes[]; .ahead == "database" or (.name == "EdgeAdminPage" and .ahead == "unknown")) and .data.differences == (.data.contentTypes | length)' "$out"
$OPTICLI serve --stop >/dev/null

echo "== 2. local ahead: a new property and a new type"
edit "$article" <<'PY'
import sys
p = sys.argv[1]; s = open(p).read()
s = "using System.ComponentModel.DataAnnotations;\n" + s.replace("public class ArticlePage : StandardPage\n{",
    "public class ArticlePage : StandardPage\n{\n    [Display(GroupName = SystemTabNames.Content, Order = 305)]\n    public virtual string Subtitle { get; set; }\n", 1)
open(p, "w").write(s)
PY
cat > Models/Pages/DriftPage.cs <<CS
namespace $namespace.Models.Pages;

[SiteContentType(GUID = "7d1f3c2a-9b8e-4f6d-a5c4-0e1b2c3d4e5f")]
public class DriftPage : StandardPage
{
}
CS
build
out=$($OPTICLI serve)
expect '.ok and .data.drift.differences > 0' "$out"
out=$($OPTICLI drift)
expect '[.data.contentTypes | own | .[].name] == ["DriftPage"] and [.data.properties[].name] == ["ArticlePage.Subtitle"] and all((.data.contentTypes | own)[], .data.properties[]; .ahead == "local")' "$out"
fingerprint=$(jq -r '.data.fingerprint' <<<"$out")
# Shared mode didn't commit the local models: the database still has neither.
added=$(sqlcmd -d "$TARGET_DB" -b -h -1 -W -Q "SET NOCOUNT ON; SELECT (SELECT COUNT(*) FROM tblContentType WHERE Name = N'DriftPage') + (SELECT COUNT(*) FROM tblPropertyDefinition WHERE Name = N'Subtitle')")
if [ "$added" != 0 ]; then
  echo "unexpected: the site added DriftPage or ArticlePage.Subtitle to the shared database" >&2
  exit 1
fi
ref=$($OPTICLI find --type ArticlePage --limit 1 | jq -r '.data[0].ref')
out=$($OPTICLI set "$ref" MetaTitle="opticli drift test" --dry-run)
expect '.ok and any(.meta.warnings[]; startswith("drift: "))' "$out"
out=$($OPTICLI set "$ref" MetaTitle="opticli drift test" || true)
expect '.ok == false and .error.code == "drift" and .error.details.fingerprint == "'"$fingerprint"'"' "$out"
out=$($OPTICLI set "$ref" MetaTitle="opticli drift test" --accept-drift 0123456789ab || true)
expect '.error.code == "drift"' "$out"
out=$($OPTICLI set "$ref" MetaTitle="opticli drift test" --accept-drift "$fingerprint")
expect '.ok and .data.saved' "$out"
echo "{\"operations\": [{\"op\": \"set\", \"ref\": \"$ref\", \"properties\": {\"MetaTitle\": \"opticli drift plan\"}}]}" > .opticli/plan.json
out=$($OPTICLI apply .opticli/plan.json || true)
expect '.error.code == "drift"' "$out"
out=$($OPTICLI apply .opticli/plan.json --accept-drift "$fingerprint")
expect '.ok and .data.operations[0].status == "saved"' "$out"
$OPTICLI serve --stop >/dev/null

echo "== 3. the database ahead: the environment synced those models, this checkout doesn't have them"
$OPTICLI serve --connection "$LOCAL" >/dev/null
$OPTICLI serve --stop --connection "$LOCAL" >/dev/null
cp "$SOURCE/$article" "$article"
rm Models/Pages/DriftPage.cs
build
out=$($OPTICLI serve)
expect '.ok and .data.drift.ahead == "database"' "$out"
out=$($OPTICLI drift)
expect '[.data.contentTypes | own | .[] | select(.ahead == "database") | .name] == ["DriftPage"] and [.data.properties[].name] == ["ArticlePage.Subtitle"]' "$out"
out=$($OPTICLI set "$ref" MetaTitle="opticli drift test" || true)
expect '.error.code == "drift" and .error.details.ahead == "database"' "$out"
$OPTICLI serve --stop >/dev/null

echo "== 4. an EF Core migration the database lacks"
mkdir -p Drift
cat > Drift/DriftDbContext.cs <<CS
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace $namespace.Drift;

// For opticli's drift scenario: nothing registers or migrates it.
public class DriftDbContext(DbContextOptions<DriftDbContext> options) : DbContext(options);

[DbContext(typeof(DriftDbContext))]
[Migration("20261002000000_AddDriftTable")]
public class AddDriftTable : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable("DriftTest", t => new { Id = t.Column<int>() });
}
CS
build
# Without a history table the migrations aren't compared, only noted.
out=$($OPTICLI serve)
expect '.ok and any(.meta.warnings[]; contains("__EFMigrationsHistory"))' "$out"
$OPTICLI serve --stop >/dev/null
sqlcmd -d "$TARGET_DB" -b -Q "CREATE TABLE dbo.__EFMigrationsHistory (MigrationId nvarchar(150) NOT NULL PRIMARY KEY, ProductVersion nvarchar(32) NOT NULL); INSERT dbo.__EFMigrationsHistory VALUES (N'20250101000000_Init', N'8.0.0');" >/dev/null
out=$($OPTICLI serve || true)
expect '.error.code == "refused" and .error.details.reason == "pendingMigrations"' "$out"
out=$($OPTICLI serve --allow-pending-migrations)
expect '.ok and .data.drift.ahead == "both"' "$out"
out=$($OPTICLI drift)
expect '[.data.migrations[] | "\(.ahead) \(.name)"] == ["database 20250101000000_Init", "local 20261002000000_AddDriftTable"]' "$out"
$OPTICLI serve --stop >/dev/null
# Rebuild without it, or the next serve finds the migration in bin and refuses.
rm -r Drift
build

cat <<EOF

Done: every step stopped or went ahead as it should. The copy keeps its database ($TARGET_DB); run the integration
tests' drift check against it with:
  XDG_CONFIG_HOME=$XDG_CONFIG_HOME XDG_STATE_HOME=$XDG_STATE_HOME OPTICLI_IT_PROJECT=$TARGET \\
    dotnet test tests/OptiCli.Integration --filter DriftTests
(start it first: cd $TARGET && XDG_CONFIG_HOME=... XDG_STATE_HOME=... opticli serve --allow-pending-migrations)
EOF
