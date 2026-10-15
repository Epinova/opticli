#!/usr/bin/env bash
# The export round trip: exports content of the source copy (make-source.sh) and applies it to a fresh copy of the base
# database whose ids differ from the source's, then checks that every exported item reads the same in both and that
# applying the plan again changes nothing. Nothing here touches the base database or the edge-case site's own blobs.
#
#   round-trip.sh [<site-dir> [<base-db> [<source-db> [<target-db>]]]]
#       defaults: /demo/AlloyEdge alloy-edge alloy-edge-source alloy-edge-target
#       CMS 13:   round-trip.sh /demo/AlloyEdge13 alloy-edge13 alloy-edge-13-source alloy-edge-13-target
#
# 1. make-source.sh makes the source afresh (KEEP_SOURCE=1 uses the one there is).
# 2. make-source.sh --copy-only makes the target, a fresh copy of the base database, and serves it. Two pages are created
#    in it first: copies continue the same identity seed, so without them new content would get the source's ids, and a
#    reference written by id instead of GUID would go unnoticed.
# 3. opticli export reads the source over SQL: the new "Sync section" and "Sync media" folder, About us and the
#    Whitepaper (with what is below them), and on CMS 13 the new "Sync experience"; media files from the source's blobs.
# 4. opticli apply --update-existing writes the plan to the target (a dry run first).
# 5. Every item and language branch the plan writes is read with `get --full --all-properties` from both databases and
#    compared, ignoring what is the database's own: refs and ids, versions, who saved when (saved, changedBy,
#    latestDraft), the parent (compared by GUID; for a block or file in a "For this page" folder, which a create makes
#    anew, by the content it belongs to), the GUID of such a folder in a URL, and a folder's startPublish (folders have no
#    versions to write it to: it is when the folder was made). Publish dates, sort order, URL segment, menu visibility
#    and everything else must match.
# 6. The plan is applied again: every step must be unchanged.
#
# The target's site is stopped at the end and the target dropped (KEEP_TARGET=1 keeps both, to look at it). The export
# and the reports are in the target's copy home (~/.local/state/opticli-sync/<target-db>) while it exists.
#
# Needs what make-source.sh needs (sqlcmd, jq, rsync, opticli; OPTICLI overrides the command, SQLCMDPASSWORD the
# password).
set -euo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
MAKE="$HERE/make-source.sh"
OPTICLI=${OPTICLI:-opticli}
export OPTICLI SQLCMDSERVER=${SQLCMDSERVER:-localhost,1433} SQLCMDUSER=${SQLCMDUSER:-sa}
: "${SQLCMDPASSWORD:?set SQLCMDPASSWORD to the SQL Server password}"
export SQLCMDPASSWORD

case "${1:-}" in -h|--help) awk 'NR == 1 { next } !/^#/ { exit } { sub(/^# ?/, ""); print }' "$0"; exit 1 ;; esac
SITE=$(cd "${1:-/demo/AlloyEdge}" && pwd)
BASE_DB=${2:-alloy-edge}
SOURCE_DB=${3:-alloy-edge-source}
TARGET_DB=${4:-alloy-edge-target}
ROOT=${SYNC_ROOT:-$HOME/.local/state/opticli-sync}
SOURCE_HOME=$ROOT/$SOURCE_DB
TARGET_HOME=$ROOT/$TARGET_DB

connection() {
  local base
  base=$(jq -r '.ConnectionStrings.EPiServerDB' "$SITE/appsettings.Development.json")
  echo "${base/Database=$BASE_DB;/Database=$1;}"
}
SOURCE=$(connection "$SOURCE_DB")
TARGET=$(connection "$TARGET_DB")
cd "$SITE"

if [ "${KEEP_SOURCE:-0}" != 1 ]; then
  "$MAKE" "$SITE" "$BASE_DB" "$SOURCE_DB"
fi
[ -f "$SOURCE_HOME/changes.json" ] || { echo "$SOURCE_DB has no changes.json: run make-source.sh first." >&2; exit 1; }

"$MAKE" --copy-only "$SITE" "$BASE_DB" "$TARGET_DB"
finish() {
  if [ "${KEEP_TARGET:-0}" = 1 ]; then
    echo "Kept $TARGET_DB and its site: $MAKE --drop $TARGET_DB $SITE $BASE_DB removes both."
  else
    "$MAKE" --drop "$TARGET_DB" "$SITE" "$BASE_DB" >/dev/null || true
  fi
}
trap finish EXIT
"$MAKE" --serve "$TARGET_DB" "$SITE" "$BASE_DB"
# The target's site has its own opticli state, as make-source.sh gives it.
target() { XDG_STATE_HOME=$TARGET_HOME/state $OPTICLI "$@" --connection "$TARGET"; }
source_() { $OPTICLI "$@" --connection "$SOURCE"; }

START=7b08c7d5-7585-47e5-add7-5822da68c3ce
echo "== ids: two pages in $TARGET_DB first"
for n in 1 2; do
  target create "$START" --type StandardPage --name "Id shifter $n" >/dev/null
done

guid() { jq -r --arg id "$2" '.data.operations[] | select(.id == $id) | .guid' "$SOURCE_HOME/$1"; }
refs=("$(guid changes.json section)" "$(guid changes.json mediaFolder)" 61104228-962d-4dd2-925c-b5b12e2373ee b28e8711-2526-4696-b02d-8249cf812e84)
if [ -f "$SOURCE_HOME/changes.cms13.json" ]; then
  refs+=("$(guid changes.cms13.json experience)")
fi

OUT=$TARGET_HOME/export
echo "== export from $SOURCE_DB: ${refs[*]}"
source_ export "${refs[@]}" --descendants --blobs "$SOURCE_HOME/blobs" --out "$OUT" > "$TARGET_HOME/export.json"
jq -c '.data | {items, steps, kinds, languages, files, heldBack, skipped: (.skipped | length), drafts: (.drafts | length), externalRefs: (.externalRefs | length), dropped: (.dropped | length)}' "$TARGET_HOME/export.json"
jq -c '.data.skipped[], .data.dropped[]' "$TARGET_HOME/export.json"

apply() {
  local name=$1
  shift
  if ! target apply "$OUT/plan.json" --update-existing "$@" > "$TARGET_HOME/$name.json"; then
    jq '.error | {code, message, hint}' "$TARGET_HOME/$name.json" >&2
    jq -c '.error.details.operations[]? | select(.status == "invalid" or .status == "failed") | {index, op, id, status, error: .error.message}' "$TARGET_HOME/$name.json" >&2
    exit 1
  fi
  jq -c '[.data.operations[].status] | group_by(.) | map({(.[0]): length}) | add' "$TARGET_HOME/$name.json"
}
echo "== apply to $TARGET_DB: dry run, run"
apply dry-run --dry-run
apply apply

echo "== compare every item in both databases"
# What is the database's own, not the content's: ids (ref, everywhere), versions, who saved when, and the GUID of a
# "For this page" folder, which a create in the target makes anew.
normalise='walk(if type == "object" then del(.ref, .version, .saved, .changedBy, .latestDraft) else . end)
  | walk(if type == "string" then gsub("/contentassets/[0-9a-f]{32}/"; "/contentassets/*/") else . end)'
read_item() { # <reader> <guid> <lang>
  local item parent
  item=$($1 get "$2" ${3:+--lang "$3"} --full --all-properties --json)
  parent=$(echo "$item" | jq -r '.data.parent // empty')
  if [ -n "$parent" ]; then
    parent=$($1 get "$parent" --fields name --json | jq -r 'if .data.type == "SysContentAssetFolder" then "for \(.data.guid)" else .data.guid end')
    if [[ $parent == for* ]]; then
      parent="for $($1 sql "SELECT LOWER(CAST(ContentOwnerID AS nvarchar(36))) AS owner FROM tblContent WHERE ContentGUID = '${parent#for }'" --json | jq -r '.data.rows[0].owner')"
    fi
  fi
  echo "$item" | jq -S --arg parent "$parent" ".data | .parent = \$parent | if .kind == \"folder\" then del(.startPublish) else . end | $normalise"
}
differences=0
compared=0
while read -r guid lang; do
  [ "$lang" = "*" ] && lang=""
  a=$(read_item source_ "$guid" "$lang")
  b=$(read_item target "$guid" "$lang")
  compared=$((compared + 1))
  if [ "$a" != "$b" ]; then
    differences=$((differences + 1))
    echo "-- $guid ${lang:-} $(echo "$a" | jq -r '.name'): differs"
    diff <(echo "$a") <(echo "$b") | head -40 || true
  fi
done < <(jq -r '.source.versions | to_entries[] | .key as $guid | .value | keys[] | "\($guid) \(.)"' "$OUT/plan.json")
echo "compared $compared branch(es): $differences differ"

echo "== apply again: every step unchanged"
apply again
changed=$(jq '[.data.operations[] | select(.status != "unchanged")] | length' "$TARGET_HOME/again.json")
jq -c '.data.operations[] | select(.status != "unchanged") | {index, op, id, status, changes: .result.changes}' "$TARGET_HOME/again.json"

if [ "$differences" != 0 ] || [ "$changed" != 0 ]; then
  echo "FAILED: $differences branch(es) differ, $changed step(s) changed on the second run (reports in $TARGET_HOME)." >&2
  KEEP_TARGET=1
  exit 1
fi
echo "OK: $compared branch(es) the same in $SOURCE_DB and $TARGET_DB, and the second run changed nothing."
