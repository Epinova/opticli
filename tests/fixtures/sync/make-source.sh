#!/usr/bin/env bash
# Makes the source database for the content-sync tests: a fresh copy of the edge-case site's database (alloy-edge ->
# alloy-edge-source), changed through opticli the way editors change another environment, with
# source-changes.plan.json. The edge-case site, its database and its blobs are not changed.
#
#   make-source.sh [<site-dir> [<base-db> [<copy-db>]]]   a fresh copy, then the changes (default: /demo/AlloyEdge,
#                                                         alloy-edge, alloy-edge-source)
#   make-source.sh --copy-only [<site-dir> [<base-db> [<copy-db>]]]   only a fresh copy (e.g. a target), no changes
#   make-source.sh --serve <copy-db> [<site-dir> [<base-db>]]   run the site against a copy (to write to it)
#   make-source.sh --stop <copy-db> [<site-dir> [<base-db>]]    stop that site
#   make-source.sh --env <copy-db> [<site-dir> [<base-db>]]     print the shell variables that point opticli at the
#                                                               copy (eval "$(make-source.sh --env ...)")
#   make-source.sh --drop <copy-db> [<site-dir> [<base-db>]]    stop its site, drop the copy and its folder
#
# Every run of the first form starts from a fresh copy, so it repeats. A copy's name must start with "alloy-edge-"
# (no base database's does), so the script can't overwrite a database it didn't make. It works on the CMS 13 edge site
# too (/demo/AlloyEdge13 alloy-edge13 alloy-edge-13-source): the plan names existing content by the Alloy sample's
# GUIDs, which both have.
#
# A copy's site runs from the edge-case site's build, with opticli state of its own (so it runs beside a `serve` of the
# edge-case site itself) and its own blob folder: media bytes are in <copy home>/blobs, not the site's App_Data/blobs.
# The copy home is ~/.local/state/opticli-sync/<copy-db> (SYNC_ROOT overrides ~/.local/state/opticli-sync); the
# changes' apply result (every step's ref and GUID) goes to changes.json there. The first form stops the site at the
# end unless KEEP_RUNNING=1.
#
# The changes (GUIDs of new content: uuid5(the plan's guidNamespace, the step's id)):
#   new pages        "Sync section" under Start, with "Sync page A" (and its child "Sync page A1") and "Sync page B";
#                    all published
#   cycle            A's MainBody links to B and its MainContentArea holds B; B's MainBody links to A
#   new media        folder "Sync media" in the global assets, sync-image.png in it (published); the section's PageImage
#   new block        "Sync teaser" (TeaserBlock) in the section's "For this page" folder, Image = the image, Link = B,
#                    in the section's MainContentArea; published
#   edited, published  About us: TeaserText
#   edited, draft    Contact us: TeaserText, not published
#   moved            Become a reseller: About us -> How to buy
#   trashed          Management (the start page's link collection still links to it)
#   new language     Whitepaper: sv branch "Vitbok", published
#
# Needs sqlcmd, jq, rsync and opticli (OPTICLI overrides the command), and SQL Server credentials in SQLCMDSERVER,
# SQLCMDUSER and SQLCMDPASSWORD (default: localhost,1433 and sa).
set -euo pipefail

usage() { sed -n '2,42p' "$0" | sed 's/^# \{0,1\}//'; exit 1; }
HERE=$(cd "$(dirname "$0")" && pwd)
OPTICLI=${OPTICLI:-opticli}
export SQLCMDSERVER=${SQLCMDSERVER:-localhost,1433} SQLCMDUSER=${SQLCMDUSER:-sa}
: "${SQLCMDPASSWORD:?set SQLCMDPASSWORD to the SQL Server password}"
export SQLCMDPASSWORD

MODE=changes
case "${1:-}" in
  -h|--help) usage ;;
  --copy-only) MODE=copy; shift ;;
  --serve|--stop|--env|--drop)
    MODE=${1#--}
    [ $# -ge 2 ] || usage
    COPY_DB=$2
    shift 2 ;;
  -*) usage ;;
esac
SITE=$(cd "${1:-/demo/AlloyEdge}" && pwd)
BASE_DB=${2:-alloy-edge}
if [ "$MODE" = changes ] || [ "$MODE" = copy ]; then COPY_DB=${3:-alloy-edge-source}; fi

for db in "$BASE_DB" "$COPY_DB"; do
  case "$db" in *[!A-Za-z0-9_-]*) echo "Database names may only have letters, digits, '_' and '-'." >&2; exit 1 ;; esac
done
case "$COPY_DB" in alloy-edge-?*) ;; *) echo "A copy's name must start with alloy-edge- ($COPY_DB)." >&2; exit 1 ;; esac
[ "$COPY_DB" != "$BASE_DB" ] || { echo "The copy must not be the base database." >&2; exit 1; }

COPY_HOME=${SYNC_ROOT:-$HOME/.local/state/opticli-sync}/$COPY_DB
BLOBS=$COPY_HOME/blobs
base_connection=$(jq -r '.ConnectionStrings.EPiServerDB // empty' "$SITE/appsettings.Development.json")
case "$base_connection" in
  *"Database=$BASE_DB;"*) ;;
  *) echo "$SITE/appsettings.Development.json doesn't connect to $BASE_DB." >&2; exit 1 ;;
esac
COPY_CONNECTION=${base_connection/Database=$BASE_DB;/Database=$COPY_DB;}

# The copy's site has opticli state of its own, so a serve of the edge-case site (same project directory) keeps running.
export XDG_STATE_HOME=$COPY_HOME/state
cd "$SITE"

stop() { $OPTICLI serve --stop --connection "$COPY_CONNECTION" >/dev/null 2>&1 || true; }

serve() {
  echo "== serve on $COPY_DB"
  mkdir -p "$BLOBS"
  # EPiServer:Cms:FileBlobProvider:Path is where the CMS's file blob provider keeps media bytes (default
  # [appDataPath]\blobs, the site's App_Data/blobs).
  local started
  if ! started=$(EPiServer__Cms__FileBlobProvider__Path="$BLOBS" $OPTICLI serve --connection "$COPY_CONNECTION"); then
    echo "$started" | jq '.error // .' >&2
    exit 1
  fi
  echo "$started" | jq -c '.data | {state, url, database: .database.name}'
}

copy() {
  stop
  echo "== database: $BASE_DB -> $COPY_DB (fresh)"
  sqlcmd -b -Q "
    DECLARE @bak nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000)) + N'/$COPY_DB.bak';
    DECLARE @dir nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000));
    DECLARE @data sysname = (SELECT name FROM sys.master_files WHERE database_id = DB_ID(N'$BASE_DB') AND type = 0);
    DECLARE @log sysname = (SELECT name FROM sys.master_files WHERE database_id = DB_ID(N'$BASE_DB') AND type = 1);
    DECLARE @dataFile nvarchar(4000) = @dir + N'$COPY_DB.mdf', @logFile nvarchar(4000) = @dir + N'${COPY_DB}_log.ldf';
    IF @data IS NULL THROW 50000, 'The base database $BASE_DB does not exist.', 1;
    BACKUP DATABASE [$BASE_DB] TO DISK = @bak WITH COPY_ONLY, INIT;
    IF DB_ID(N'$COPY_DB') IS NOT NULL ALTER DATABASE [$COPY_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    RESTORE DATABASE [$COPY_DB] FROM DISK = @bak WITH REPLACE, MOVE @data TO @dataFile, MOVE @log TO @logFile;
    ALTER DATABASE [$COPY_DB] SET MULTI_USER;" >/dev/null
  # Media bytes are files, not database rows: the copy gets its own copy of the base site's blobs.
  echo "== blobs: $SITE/App_Data/blobs -> $BLOBS"
  mkdir -p "$BLOBS" "$XDG_STATE_HOME"
  rsync -a --delete "$SITE/App_Data/blobs/" "$BLOBS/"
}

case "$MODE" in
  serve) serve; exit 0 ;;
  stop) stop; echo "== stopped the site on $COPY_DB"; exit 0 ;;
  env)
    printf 'export XDG_STATE_HOME=%q OPTICLI_DB=%q\n' "$XDG_STATE_HOME" "$COPY_CONNECTION"
    exit 0 ;;
  drop)
    stop
    sqlcmd -b -Q "IF DB_ID(N'$COPY_DB') IS NOT NULL BEGIN ALTER DATABASE [$COPY_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$COPY_DB]; END"
    rm -rf "$COPY_HOME"
    echo "== dropped $COPY_DB and $COPY_HOME"
    exit 0 ;;
  copy)
    copy
    echo "Done: $COPY_DB is a fresh copy of $BASE_DB, blobs in $BLOBS."
    exit 0 ;;
esac

copy
[ "${KEEP_RUNNING:-0}" = 1 ] || trap stop EXIT
serve

echo "== changes (source-changes.plan.json)"
if ! result=$($OPTICLI apply "$HERE/source-changes.plan.json" --connection "$COPY_CONNECTION"); then
  echo "$result" | jq '.error | {code, message, hint}' >&2
  echo "$result" | jq -c '.error.details.operations[]? | {index, op, id, status, error: .error.message}' >&2
  exit 1
fi
echo "$result" > "$COPY_HOME/changes.json"
echo "$result" | jq -c '.data.operations[] | {index, op, id, status, ref: (.result.ref // null), guid: (.guid // .result.guid // null)}'

# The image must be in the copy's blob folder, not the edge-case site's. `opticli blob` maps the URI with the site's
# appsettings, which don't know $BLOBS: only its container and file name count.
image=$(echo "$result" | jq -r '.data.operations[] | select(.id == "image") | .result.ref')
blob=$($OPTICLI blob "$image" --connection "$COPY_CONNECTION" | jq -r '.data.blob | "\(.container)/\(.file)"')
if [ ! -f "$BLOBS/$blob" ] || [ -e "$SITE/App_Data/blobs/${blob%%/*}" ]; then
  echo "The new image's blob $blob isn't (only) in $BLOBS." >&2
  exit 1
fi
echo "== image blob: $BLOBS/$blob"

cat <<EOF

Done: $COPY_DB has the changes above. Media bytes: $BLOBS. Apply result: $COPY_HOME/changes.json.
Point opticli at it: eval "\$($0 --env $COPY_DB $SITE $BASE_DB)"
EOF
