#!/usr/bin/env bash
# The sync pull test: a source and a target that start as copies of the base database, then change apart, and
# `opticli sync pull` brings the source's changes into the target. Nothing here touches the base database, the
# edge-case site's own blobs, or the sources make-source.sh keeps (alloy-edge-source, alloy-edge-13-source).
#
#   sync-pull.sh [<site-dir> [<base-db> [<source-db> [<target-db>]]]]
#       defaults: /demo/AlloyEdge alloy-edge alloy-edge-sync-source alloy-edge-sync-target
#       CMS 13:   sync-pull.sh /demo/AlloyEdge13 alloy-edge13 alloy-edge-13-sync-source alloy-edge-13-sync-target
#
# 1. make-source.sh --copy-only makes both copies. The target's site runs (make-source.sh --serve); two pages are created
#    in it first (copies continue the same identity seed, and ids that coincide would hide an id written for a GUID), and
#    "Find a reseller" is moved to its recycle bin.
# 2. The source gets the role prod, in a user config of the test's own (XDG_CONFIG_HOME): the pulls only read it.
# 3. The first pull takes the baseline: everything is the same but "Find a reseller", which is restored.
# 4. The source changes as make-source.sh changes it (source-changes.plan.json, and on CMS 13 source-changes.cms13.plan.json),
#    plus sync-changes.plan.json: a folder in the new section's "For this page" folder with an image in it, a page scheduled
#    for 2030, and a page of EdgeRemovedEmptyPage, a type the target lacks once `types remove` removed it there.
# 5. A dry run, then the pull with --delete: every change must have arrived (new pages with a cycle, media, a block in a
#    "For this page" folder, the subfolder, the edited page, the new language branch, the move, the trash, the scheduled
#    page as scheduled), the orphan-type page skipped and the newer draft (Contact us) left out. Every item the changes
#    touched is read with `get --full --all-properties` in both databases and compared, as round-trip.sh does.
# 6. Pulled again: all unchanged, and nothing applied.
# 7. About us edited in both: a conflict, left as the target has it; then --overwrite-local writes the source's.
# 8. Sync page B edited only in the target: kept. Sync page A1 moved to the target's recycle bin: a conflict; then
#    --overwrite-local restores it.
# 9. Sync page A translated to sv in both: a conflict (the target's branch isn't the sync's). About us given the same value
#    in both and a draft in the target, then changed in the source: a conflict, the draft kept.
#
# Both copies are dropped at the end (KEEP=1 keeps them, with their sites running). The pulls' results are in
# $WORK (printed). Needs what make-source.sh needs (sqlcmd, jq, rsync, opticli; OPTICLI overrides the command,
# SQLCMDPASSWORD the password).
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
SOURCE_DB=${3:-alloy-edge-sync-source}
TARGET_DB=${4:-alloy-edge-sync-target}
ROOT=${SYNC_ROOT:-$HOME/.local/state/opticli-sync}
SOURCE_HOME=$ROOT/$SOURCE_DB
TARGET_HOME=$ROOT/$TARGET_DB
WORK=$(mktemp -d "${TMPDIR:-/tmp}/opticli-sync-pull.XXXXXX")
# The pulls' user config (the source is production there) and the one the source's own changes are made with.
PULL_CONFIG=$WORK/pull-config
EDIT_CONFIG=$WORK/edit-config
mkdir -p "$PULL_CONFIG" "$EDIT_CONFIG"

connection() {
  local base
  base=$(jq -r '.ConnectionStrings.EPiServerDB' "$SITE/appsettings.Development.json")
  echo "${base/Database=$BASE_DB;/Database=$1;}"
}
SOURCE=$(connection "$SOURCE_DB")
TARGET=$(connection "$TARGET_DB")
cd "$SITE"

START=7b08c7d5-7585-47e5-add7-5822da68c3ce
ABOUT_US=61104228-962d-4dd2-925c-b5b12e2373ee
WHITEPAPER=b28e8711-2526-4696-b02d-8249cf812e84
CONTACT_US=f5d5ac23-d96d-4c2f-b8e1-56dfe7e05733
RESELLER=ef61f729-8e7a-4c56-aede-98d361f1556d
HOW_TO_BUY=e9cefa25-6a0a-47d0-b12c-74c3f6a51c9a
MANAGEMENT=43772e2d-46fa-412a-92ea-16bc0940fcda
FIND_RESELLER=6a5312a5-f9b7-40bd-a6b3-4f2ae2bdf7c0

finish() {
  local status=$?
  if [ "${KEEP:-0}" = 1 ]; then
    echo "Kept $SOURCE_DB and $TARGET_DB: $MAKE --drop <copy> $SITE $BASE_DB removes each. Results: $WORK"
  else
    XDG_CONFIG_HOME=$EDIT_CONFIG "$MAKE" --drop "$SOURCE_DB" "$SITE" "$BASE_DB" >/dev/null 2>&1 || true
    XDG_CONFIG_HOME=$EDIT_CONFIG "$MAKE" --drop "$TARGET_DB" "$SITE" "$BASE_DB" >/dev/null 2>&1 || true
    [ "$status" = 0 ] && rm -rf "$WORK"
  fi
  [ "$status" = 0 ] || echo "FAILED (results in $WORK)" >&2
}
trap finish EXIT

fail() { echo "FAILED: $*" >&2; exit 1; }

# The target is written through its site; the pulls read the source as production. Its blob folder is the target site's.
target() { XDG_CONFIG_HOME=$PULL_CONFIG XDG_STATE_HOME=$TARGET_HOME/state EPiServer__Cms__FileBlobProvider__Path=$TARGET_HOME/blobs $OPTICLI "$@" --connection "$TARGET"; }
source_() { XDG_CONFIG_HOME=$EDIT_CONFIG XDG_STATE_HOME=$SOURCE_HOME/state $OPTICLI "$@" --connection "$SOURCE"; }
serve_source() { XDG_CONFIG_HOME=$EDIT_CONFIG "$MAKE" --serve "$SOURCE_DB" "$SITE" "$BASE_DB" >/dev/null; }
stop_source() { XDG_CONFIG_HOME=$EDIT_CONFIG "$MAKE" --stop "$SOURCE_DB" "$SITE" "$BASE_DB" >/dev/null; }
apply_source() { # <plan>
  local result
  if ! result=$(source_ apply "$1"); then
    echo "$result" | jq -c '.error | {code, message}, (.details.operations[]? | select(.status == "invalid" or .status == "failed") | {index, op, error: .error.message})' >&2
    fail "the source's changes ($1) didn't apply"
  fi
}

# pull <name> [options]: a pull with the source's blobs; its result is $WORK/<name>.json, its counts are printed.
pull() {
  local name=$1
  shift
  if ! target sync pull --from src --all --blobs "$SOURCE_HOME/blobs" "$@" > "$WORK/$name.json"; then
    jq -c '.error | {code, message, hint}' "$WORK/$name.json" >&2
    jq -c '.error.details.run.operations[]? | select(.status == "invalid" or .status == "failed") | {index, op, id, status, error: .error.message}' "$WORK/$name.json" >&2
    fail "pull $name"
  fi
  echo "== $name: $(jq -c '.data.counts + {steps: .data.steps}' "$WORK/$name.json")"
}
# expect <name> <jq filter> <expected> <what>
expect() {
  local actual
  actual=$(jq -c "$2" "$WORK/$1.json")
  [ "$actual" = "$3" ] || fail "$4: expected $3, got $actual ($1: $2)"
}
value() { target get "$1" --fields "$2" --json | jq -c ".data.properties.$2.value"; }

echo "== copies of $BASE_DB: $SOURCE_DB, $TARGET_DB"
"$MAKE" --copy-only "$SITE" "$BASE_DB" "$SOURCE_DB" >/dev/null
"$MAKE" --copy-only "$SITE" "$BASE_DB" "$TARGET_DB" >/dev/null
XDG_CONFIG_HOME=$EDIT_CONFIG "$MAKE" --serve "$TARGET_DB" "$SITE" "$BASE_DB"
for n in 1 2; do
  target create "$START" --type StandardPage --name "Id shifter $n" >/dev/null
done
target delete "$FIND_RESELLER" --ignore-references >/dev/null
XDG_CONFIG_HOME=$PULL_CONFIG $OPTICLI db add src --role prod --connection "$SOURCE" >/dev/null

echo "== the baseline"
pull baseline --delete
expect baseline '.data.baseline' true "the first pull is the baseline"
expect baseline '[.data.counts.created, .data.counts.updated, .data.counts.moved, .data.counts.trashed, .data.counts.conflicts]' '[0,0,0,0,0]' "nothing but the restore on the baseline"
expect baseline "[.data.restored[].guid]" "[\"$FIND_RESELLER\"]" "the page in the target's recycle bin is restored"
[ "$(target get "$FIND_RESELLER" --json | jq -r '.data.deleted // false')" = false ] || fail "Find a reseller is still in the recycle bin"

echo "== the source's changes"
serve_source
apply_source "$HERE/source-changes.plan.json"
experiences=$(sqlcmd -b -h -1 -W -d "$SOURCE_DB" -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM tblContentType WHERE Name = N'VbExperience'")
if [ "$experiences" != 0 ]; then
  apply_source "$HERE/source-changes.cms13.plan.json"
fi
apply_source "$HERE/sync-changes.plan.json"
stop_source
# The target's build lacks the orphan-type page's type once it is removed (the edge-case site makes it again at startup).
target types remove EdgeRemovedEmptyPage >/dev/null || fail "types remove EdgeRemovedEmptyPage in the target"

# The GUIDs the changes made: uuid5 of each plan's guidNamespace and the step's id.
uuid5() { python3 -c "import sys, uuid; print(uuid.uuid5(uuid.UUID(sys.argv[1]), sys.argv[2]))" "$1" "$2"; }
CHANGES=0d6f3a52-8b1e-4c7a-9e25-5a4c1b7d3f90
SYNC=4f1d9c2e-7a3b-4e8f-b6d1-9c0e2a5f7b31
SECTION=$(uuid5 $CHANGES section); PAGE_A=$(uuid5 $CHANGES pageA); PAGE_B=$(uuid5 $CHANGES pageB); PAGE_A1=$(uuid5 $CHANGES pageA1)
FOLDER=$(uuid5 $CHANGES mediaFolder); IMAGE=$(uuid5 $CHANGES image); TEASER=$(uuid5 $CHANGES teaser)
SUBFOLDER=$(uuid5 $SYNC subfolder); SUB_IMAGE=$(uuid5 $SYNC subImage); SCHEDULED=$(uuid5 $SYNC scheduled); ORPHAN=$(uuid5 $SYNC orphanTypePage)
created=("$SECTION" "$PAGE_A" "$PAGE_B" "$PAGE_A1" "$FOLDER" "$IMAGE" "$TEASER" "$SUBFOLDER" "$SUB_IMAGE" "$SCHEDULED")
if [ "$experiences" != 0 ]; then
  created+=("$(uuid5 $CHANGES experience)")
fi

echo "== the changes, pulled: a dry run first"
pull dry-run --delete --dry-run
expect dry-run '.data.dryRun' true "a dry run"
[ "$(target get "$SECTION" --json | jq -r '.ok')" = false ] || fail "the dry run created the section"
pull changes --delete
expect changes '[.data.created[].guid] | sort' "$(printf '%s\n' "${created[@]}" | jq -R . | jq -sc 'sort')" "created"
expect changes "[.data.updated[].guid] | sort" "$(jq -nc --arg a $ABOUT_US --arg w $WHITEPAPER '[$a, $w] | sort')" "updated: About us (TeaserText) and the Whitepaper (sv)"
expect changes "[.data.moved[].guid]" "[\"$RESELLER\"]" "moved"
expect changes "[.data.trashed[].guid]" "[\"$MANAGEMENT\"]" "trashed with --delete"
expect changes "[.data.skipped[] | select(.guid == \"$ORPHAN\") | .reason | contains(\"EdgeRemovedEmptyPage\")]" '[true]' "the page of a type the target lacks is skipped"
expect changes "[.data.drafts[].guid]" "[\"$CONTACT_US\"]" "the newer draft is left out"
expect changes '.data.conflicts | length' 0 "no conflicts"
[ "$(target get "$MANAGEMENT" --json | jq -r '.data.deleted // false')" = true ] || fail "Management isn't in the target's recycle bin"
[ "$(target get "$SCHEDULED" --json | jq -r '.data.status')" = delayedPublish ] || fail "the scheduled page isn't scheduled in the target"
[ "$(target get "$RESELLER" --json | jq -r '.data.parent')" = "$(target get "$HOW_TO_BUY" --json | jq -r '.data.ref')" ] || fail "Become a reseller isn't under How to buy"

echo "== every item the changes touched, compared in both databases"
# What is each database's own (as round-trip.sh): ids, versions, who saved when, the parent (compared by GUID, or by the
# owner for content in a "For this page" folder), the GUID of such a folder in URLs, and a folder's startPublish.
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
source_read() { XDG_CONFIG_HOME=$PULL_CONFIG $OPTICLI "$@" --db src 2>/dev/null; }
target_read() { target "$@"; }
differences=0
compared=0
for guid in "${created[@]}" "$ABOUT_US" "$RESELLER" "$CONTACT_US" "$WHITEPAPER"; do
  for lang in $(source_read get "$guid" --json | jq -r '.data.languages[]? // "*"'); do
    [ "$lang" = "*" ] && lang=""
    a=$(read_item source_read "$guid" "$lang")
    b=$(read_item target_read "$guid" "$lang")
    compared=$((compared + 1))
    if [ "$a" != "$b" ]; then
      differences=$((differences + 1))
      echo "-- $guid ${lang:-} $(echo "$a" | jq -r '.name'): differs"
      diff <(echo "$a") <(echo "$b") | head -40 || true
    fi
  done
done
echo "compared $compared branch(es): $differences differ"
[ "$differences" = 0 ] || fail "$differences branch(es) differ"

echo "== pulled again: nothing to do"
pull again --delete
expect again '[.data.steps, .data.run, .data.counts.created, .data.counts.updated, .data.counts.moved, .data.counts.trashed, .data.counts.restored, .data.counts.conflicts]' '[0,null,0,0,0,0,0,0]' "the second pull changes nothing"

echo "== About us edited in both: a conflict, then --overwrite-local"
target set "$ABOUT_US" TeaserText="Edited in the target" --publish >/dev/null
serve_source
source_ set "$ABOUT_US" TeaserText="Edited in the source again" --publish >/dev/null
stop_source
pull conflict --delete
expect conflict "[.data.conflicts[].guid]" "[\"$ABOUT_US\"]" "About us is a conflict"
expect conflict '.data.counts.updated' 0 "a conflict isn't written"
[ "$(value "$ABOUT_US" TeaserText)" = '"Edited in the target"' ] || fail "the conflict changed the target's About us"
pull overwrite --delete --overwrite-local
expect overwrite "[.data.updated[].guid]" "[\"$ABOUT_US\"]" "--overwrite-local writes About us"
[ "$(value "$ABOUT_US" TeaserText)" = '"Edited in the source again"' ] || fail "--overwrite-local didn't write the source's About us"

echo "== local changes: Sync page B edited (kept), Sync page A1 moved to the recycle bin (a conflict, then restored)"
target set "$PAGE_B" TeaserText="Only in the target" --publish >/dev/null
target delete "$PAGE_A1" >/dev/null
pull local --delete
expect local "[.data.kept[].guid]" "[\"$PAGE_B\"]" "a local change only is kept"
expect local "[.data.conflicts[].guid]" "[\"$PAGE_A1\"]" "a local trash of synced content is a conflict"
expect local '.data.steps' 0 "nothing written for local changes"
[ "$(value "$PAGE_B" TeaserText)" = '"Only in the target"' ] || fail "the local change to Sync page B was overwritten"
[ "$(target get "$PAGE_A1" --json | jq -r '.data.deleted // false')" = true ] || fail "Sync page A1 was restored without --overwrite-local"
pull restore --delete --overwrite-local
expect restore "[.data.restored[].guid]" "[\"$PAGE_A1\"]" "--overwrite-local restores the locally trashed page"
[ "$(target get "$PAGE_A1" --json | jq -r '.data.deleted // false')" = false ] || fail "Sync page A1 is still in the recycle bin"

echo "== a language branch made in both, and a local draft behind values found the same"
# Sync page A gets a sv branch in the target and in the source: the target's isn't the sync's, so it is a conflict.
# About us gets the same published value in both and a draft in the target: found the same, but the draft stays a local
# change, so a later change in the source is a conflict, not written over the draft.
target translate "$PAGE_A" --lang sv --name "Sync page A (target)" --publish >/dev/null
target set "$ABOUT_US" TeaserText="The same in both" --publish >/dev/null
target set "$ABOUT_US" MetaTitle="A draft in the target" >/dev/null
serve_source
source_ translate "$PAGE_A" --lang sv --name "Sync page A (source)" --publish >/dev/null
source_ set "$ABOUT_US" TeaserText="The same in both" --publish >/dev/null
stop_source
pull branches --delete
expect branches "[.data.conflicts[] | select(.guid == \"$PAGE_A\") | .guid]" "[\"$PAGE_A\"]" "a branch made in both is a conflict"
[ "$(target get "$PAGE_A" --lang sv --json | jq -r '.data.name')" = "Sync page A (target)" ] || fail "the target's sv branch of Sync page A was written over"
serve_source
source_ set "$ABOUT_US" MetaDescription="Changed in the source" --publish >/dev/null
stop_source
pull draft --delete
expect draft "[.data.conflicts[] | select(.guid == \"$ABOUT_US\") | .guid]" "[\"$ABOUT_US\"]" "a local draft behind values found the same is a local change"
[ "$(target get "$ABOUT_US" --version latest --fields MetaTitle --json | jq -r '.data.properties.MetaTitle.value')" = "A draft in the target" ] || fail "the target's draft of About us was written over"

echo "OK: the baseline, the changes ($compared branches the same in both), the second pull, conflicts and local changes."
