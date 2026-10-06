#!/usr/bin/env bash
# Builds opticli's two CMS 13 test sites from scratch:
# - fresh: a new Alloy site (`dotnet new epi-alloy-mvc`, EPiServer.Templates 2.0.1) in /demo/Alloy13, on an empty
#   database (alloy13) that the CMS fills on its first start and first request.
# - upgraded: the same code in /demo/Alloy13Up, on a copy of a CMS 12 Alloy database (alloy-demo -> alloy13-upgraded)
#   with that site's media blobs, which CMS 13 upgrades on its first start.
# Both get VisualBuilderFixture.cs and its sample content. The CMS 12 site and its database are only read.
#
#   setup.sh [fresh|upgraded|both]        (default: both; upgraded copies the fresh site's code, so it needs fresh)
#
# Needs dotnet with the EPiServer.Templates 2.0.1 template package, sqlcmd, curl, jq and rsync, and SQL Server
# credentials in SQLCMDSERVER, SQLCMDUSER and SQLCMDPASSWORD (default: localhost,1433 and sa). Run it again to update the
# fixture and its content: sites and databases are kept unless FRESH=1, which makes both again from nothing.
# Other settings: CMS_VERSION (13.3.0), FRESH_DIR, FRESH_DB, FRESH_PORT (5130), UPGRADED_DIR, UPGRADED_DB,
# UPGRADED_PORT (5131), SOURCE_SITE (/demo/Alloy, for its blobs) and SOURCE_DB (alloy-demo).
set -euo pipefail

WHICH=${1:-both}
case "$WHICH" in fresh|upgraded|both) ;; *) sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;; esac
HERE=$(cd "$(dirname "$0")" && pwd)
CMS_VERSION=${CMS_VERSION:-13.3.0}
FRESH_DIR=${FRESH_DIR:-/demo/Alloy13}
FRESH_DB=${FRESH_DB:-alloy13}
FRESH_PORT=${FRESH_PORT:-5130}
UPGRADED_DIR=${UPGRADED_DIR:-/demo/Alloy13Up}
UPGRADED_DB=${UPGRADED_DB:-alloy13-upgraded}
UPGRADED_PORT=${UPGRADED_PORT:-5131}
SOURCE_SITE=${SOURCE_SITE:-/demo/Alloy}
SOURCE_DB=${SOURCE_DB:-alloy-demo}
PROJECT=Alloy13
export SQLCMDSERVER=${SQLCMDSERVER:-localhost,1433} SQLCMDUSER=${SQLCMDUSER:-sa}
: "${SQLCMDPASSWORD:?set SQLCMDPASSWORD to the SQL Server password}"
export SQLCMDPASSWORD

for db in "$FRESH_DB" "$UPGRADED_DB" "$SOURCE_DB"; do
  case "$db" in *[!A-Za-z0-9_-]*|'') echo "Database names may only have letters, digits, '_' and '-'." >&2; exit 1 ;; esac
done
if [ "$FRESH_DB" = "$SOURCE_DB" ] || [ "$UPGRADED_DB" = "$SOURCE_DB" ] || [ "$FRESH_DB" = "$UPGRADED_DB" ]; then
  echo "The fresh, upgraded and source databases must all differ." >&2
  exit 1
fi
source_site=$(realpath -m "$SOURCE_SITE")
# FRESH=1 deletes both target directories: neither may be (or contain) the source site, or be the other one.
for dir in "$FRESH_DIR" "$UPGRADED_DIR"; do
  target=$(realpath -m "$dir")
  case "$source_site/" in
    "$target"/*) echo "$dir is or contains the source site ($SOURCE_SITE); pick another directory." >&2; exit 1 ;;
  esac
  if [ "$target" = / ]; then
    echo "$dir is the root directory; pick another directory." >&2
    exit 1
  fi
done
case "$(realpath -m "$UPGRADED_DIR")/" in
  "$(realpath -m "$FRESH_DIR")"/*) echo "UPGRADED_DIR ($UPGRADED_DIR) is or is inside FRESH_DIR ($FRESH_DIR); pick another directory." >&2; exit 1 ;;
esac
case "$(realpath -m "$FRESH_DIR")/" in
  "$(realpath -m "$UPGRADED_DIR")"/*) echo "FRESH_DIR ($FRESH_DIR) is or is inside UPGRADED_DIR ($UPGRADED_DIR); pick another directory." >&2; exit 1 ;;
esac
if ! dotnet new uninstall 2>/dev/null | grep -A1 '^ *EPiServer.Templates$' | grep -q 'Version: 2\.0\.1$'; then
  echo "The EPiServer.Templates 2.0.1 template package isn't installed: dotnet new install EPiServer.Templates::2.0.1" >&2
  exit 1
fi

sql() { sqlcmd -C -b -h -1 -W -Q "SET NOCOUNT ON; $1"; }
db_exists() { [ "$(sql "SELECT COUNT(*) FROM sys.databases WHERE name = N'$1'")" != 0 ]; }

# Points the site's connection string (appsettings.Development.json, where the template puts it) at a database.
connect() {
  local dir=$1 db=$2 cs
  cs="Server=$SQLCMDSERVER;Database=$db;User Id=$SQLCMDUSER;Password=$SQLCMDPASSWORD;TrustServerCertificate=True;MultipleActiveResultSets=True"
  cs=$(printf '%s' "$cs" | sed 's/[&|\\]/\\&/g')
  sed -i -E "s|(\"EPiServerDB\": *\")[^\"]*(\")|\1$cs\2|" "$dir/appsettings.Development.json"
  if ! grep -q "Database=$db;" "$dir/appsettings.Development.json"; then
    echo "No EPiServerDB connection string found in $dir/appsettings.Development.json." >&2
    exit 1
  fi
}

# The CMS packages at CMS_VERSION.
pin() {
  local dir=$1
  sed -i -E "s|(Include=\"EPiServer\.(CMS\|Cms\.UI\.AspNetIdentity)\" Version=\")[^\"]*\"|\1$CMS_VERSION\"|" "$dir/$PROJECT.csproj"
  if [ "$(grep -cE "Include=\"EPiServer\.(CMS|Cms\.UI\.AspNetIdentity)\" Version=\"$CMS_VERSION\"" "$dir/$PROJECT.csproj")" != 2 ]; then
    echo "Couldn't set EPiServer.CMS and EPiServer.Cms.UI.AspNetIdentity to $CMS_VERSION in $dir/$PROJECT.csproj." >&2
    exit 1
  fi
}

add_fixture() {
  mkdir -p "$1/VisualBuilder"
  cp "$HERE/VisualBuilderFixture.cs" "$1/VisualBuilder/"
}

SITE_PID=
stop_site() {
  if [ -n "$SITE_PID" ] && kill -0 "$SITE_PID" 2>/dev/null; then
    kill "$SITE_PID" 2>/dev/null || true
    wait "$SITE_PID" 2>/dev/null || true
  fi
  SITE_PID=
}
trap stop_site EXIT

# Builds and starts the site (Development, without opticli's agent) and waits for its start page. The first request to
# a fresh site imports Alloy's content and makes the site's application, with the request's host.
start_site() {
  local dir=$1 port=$2 url="http://localhost:$2" log="$1/App_Data/setup-site.log" code=000
  echo "== build $dir"
  (cd "$dir" && dotnet build -nologo -v q -clp:ErrorsOnly)
  if curl -s -o /dev/null "$url/"; then
    echo "Something already listens on port $port; stop it or set another port." >&2
    exit 1
  fi
  echo "== start $url"
  mkdir -p "$dir/App_Data"
  (cd "$dir" && ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=$url exec dotnet "bin/Debug/net10.0/$PROJECT.dll") >"$log" 2>&1 &
  SITE_PID=$!
  for _ in $(seq 1 180); do
    if ! kill -0 "$SITE_PID" 2>/dev/null; then
      tail -n 40 "$log" >&2
      echo "The site stopped; its log: $log" >&2
      exit 1
    fi
    code=$(curl -s -L -o /dev/null -w '%{http_code}' --max-time 120 "$url/" || true)
    [ "$code" = 200 ] && break
    sleep 2
  done
  if [ "$code" != 200 ]; then
    echo "The start page answered $code; the site's log: $log" >&2
    exit 1
  fi
  echo "start page: $code"
}

# Makes the fixture content and shows the experience.
fill_site() {
  local url="http://localhost:$1" result page
  result=$(curl -s -X POST --max-time 300 "$url/opticli-fixture/visual-builder")
  if ! echo "$result" | jq -e .ok >/dev/null 2>&1; then
    echo "$result" | jq -r '.error // .' >&2 2>/dev/null || echo "$result" >&2
    exit 1
  fi
  echo "$result" | jq -r '.done[] | "fixture: " + .'
  result=$(curl -s --max-time 120 "$url/opticli-fixture/visual-builder")
  echo "$result" | jq -c '.report | {experience: .experience.link, url, versions: [.versions[] | {link, status: .Status, variation: .Variation}], blueprints: [.blueprints[].DisplayName]}'
  page=$(echo "$result" | jq -r '.report.url // empty')
  if [ -n "$page" ]; then
    echo "== $url$page"
    curl -s -L --max-time 120 "$url$page" | head -n 20
    echo
  fi
}

prepare_fresh() {
  if [ "${FRESH:-0}" = 1 ] || [ ! -f "$FRESH_DIR/$PROJECT.csproj" ]; then
    echo "== new site: $FRESH_DIR"
    rm -rf "$FRESH_DIR"
    dotnet new epi-alloy-mvc -n "$PROJECT" -o "$FRESH_DIR" --no-update-check >/dev/null
  fi
  pin "$FRESH_DIR"
  connect "$FRESH_DIR" "$FRESH_DB"
  if [ "${FRESH:-0}" = 1 ] || ! db_exists "$FRESH_DB"; then
    echo "== empty database: $FRESH_DB"
    sql "IF DB_ID(N'$FRESH_DB') IS NOT NULL BEGIN ALTER DATABASE [$FRESH_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$FRESH_DB]; END; CREATE DATABASE [$FRESH_DB];"
    # Alloy's content first, without the fixture: the import replaces the start page type's settings, which would drop
    # the experience type from the types allowed under it.
    rm -rf "$FRESH_DIR/VisualBuilder"
    start_site "$FRESH_DIR" "$FRESH_PORT"
    stop_site
  fi
  add_fixture "$FRESH_DIR"
}

prepare_upgraded() {
  if [ ! -f "$FRESH_DIR/$PROJECT.csproj" ]; then
    echo "The upgraded site copies the fresh site's code; make it first (setup.sh fresh)." >&2
    exit 1
  fi
  echo "== site: $FRESH_DIR -> $UPGRADED_DIR"
  if [ "${FRESH:-0}" = 1 ]; then
    rm -rf "$UPGRADED_DIR"
  fi
  mkdir -p "$UPGRADED_DIR"
  rsync -a --delete --exclude bin/ --exclude obj/ --exclude App_Data/ "$FRESH_DIR/" "$UPGRADED_DIR/"
  mkdir -p "$UPGRADED_DIR/App_Data"
  # Media files are blobs on disk, not in the database: the copy needs the CMS 12 site's.
  if [ -d "$SOURCE_SITE/App_Data/blobs" ]; then
    rsync -a "$SOURCE_SITE/App_Data/blobs/" "$UPGRADED_DIR/App_Data/blobs/"
  fi
  pin "$UPGRADED_DIR"
  add_fixture "$UPGRADED_DIR"
  connect "$UPGRADED_DIR" "$UPGRADED_DB"
  if [ "${FRESH:-0}" = 1 ] || ! db_exists "$UPGRADED_DB"; then
    echo "== database: $SOURCE_DB -> $UPGRADED_DB"
    sqlcmd -C -b -Q "
      DECLARE @bak nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000)) + N'/$UPGRADED_DB.bak';
      DECLARE @dir nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000));
      DECLARE @data sysname = (SELECT name FROM sys.master_files WHERE database_id = DB_ID(N'$SOURCE_DB') AND type = 0);
      DECLARE @log sysname = (SELECT name FROM sys.master_files WHERE database_id = DB_ID(N'$SOURCE_DB') AND type = 1);
      DECLARE @dataFile nvarchar(4000) = @dir + N'$UPGRADED_DB.mdf', @logFile nvarchar(4000) = @dir + N'${UPGRADED_DB}_log.ldf';
      BACKUP DATABASE [$SOURCE_DB] TO DISK = @bak WITH COPY_ONLY, INIT;
      IF DB_ID(N'$UPGRADED_DB') IS NOT NULL ALTER DATABASE [$UPGRADED_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
      RESTORE DATABASE [$UPGRADED_DB] FROM DISK = @bak WITH REPLACE, MOVE @data TO @dataFile, MOVE @log TO @logFile;
      ALTER DATABASE [$UPGRADED_DB] SET MULTI_USER;" >/dev/null
  fi
}

schema() { sqlcmd -C -b -h -1 -W -d "$1" -Q "SET NOCOUNT ON; DECLARE @v int; EXEC @v = dbo.sp_DatabaseVersion; SELECT @v"; }

if [ "$WHICH" != upgraded ]; then
  prepare_fresh
  start_site "$FRESH_DIR" "$FRESH_PORT"
  fill_site "$FRESH_PORT"
  stop_site
  echo "$FRESH_DB schema version: $(schema "$FRESH_DB")"
fi
if [ "$WHICH" != fresh ]; then
  prepare_upgraded
  start_site "$UPGRADED_DIR" "$UPGRADED_PORT"
  fill_site "$UPGRADED_PORT"
  stop_site
  echo "$UPGRADED_DB schema version: $(schema "$UPGRADED_DB")"
fi
echo
echo "Done. Start a site by hand with:"
echo "  cd $FRESH_DIR && ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:$FRESH_PORT dotnet bin/Debug/net10.0/$PROJECT.dll"
