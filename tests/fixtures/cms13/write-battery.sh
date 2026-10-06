#!/usr/bin/env bash
# Runs opticli's content writes against one site through the agent, checks each result, and checks that the site
# renders what was written: create, set (rich text and --values), block create, upload, area add/move/remove with
# display options, translate, publish, discard, unpublish, access, move, delete and restore.
#
#   write-battery.sh <site directory> <output directory>
#
# Run inside the dev container, e.g. on both CMS 13 sites (tests/fixtures/cms13/setup.sh) and a CMS 12 one:
#   tests/fixtures/cms13/write-battery.sh /demo/Alloy13 /tmp/writes/fresh
#   tests/fixtures/cms13/write-battery.sh /demo/Alloy13Up /tmp/writes/up
#
# It starts `opticli serve` when the site isn't running and stops it again at the end. Everything it makes is below the
# start page, named "Write battery <time>", and ends in the recycle bin. Each step's output is <label>.json; summary.tsv
# has one line per step: label, pass/FAIL, what was checked. Exits 1 when a step fails. OPTICLI defaults to the
# worktree's Debug build.
set -uo pipefail

if [[ $# -ne 2 ]]; then
  sed -n '2,15p' "$0"
  exit 1
fi

site="$1"
out="$2"
here="$(cd "$(dirname "$0")" && pwd)"
opticli="${OPTICLI:-dotnet $here/../../../src/OptiCli/bin/Debug/net8.0/opticli.dll}"

mkdir -p "$out"
rm -f "$out"/*.json "$out"/*.html "$out/summary.tsv"
cd "$site" || exit 1

o() { $opticli "$@" --json 2>/dev/null; }
failures=0

# step <label> <jq check on the output> <description> -- <opticli arguments>: runs the command, keeps its output and
# records whether the check holds.
step() {
  local label="$1" check="$2" what="$3"
  shift 4
  $opticli "$@" --json >"$out/$label.json" 2>"$out/$label.stderr"
  [[ -s "$out/$label.stderr" ]] || rm -f "$out/$label.stderr"
  if jq -e "$check" "$out/$label.json" >/dev/null 2>&1; then
    printf '%s\tpass\t%s\n' "$label" "$what" >>"$out/summary.tsv"
  else
    printf '%s\tFAIL\t%s (%s)\n' "$label" "$what" "$(jq -c '.error // .data' "$out/$label.json" 2>/dev/null | head -c 300)" >>"$out/summary.tsv"
    failures=$((failures + 1))
  fi
}

# page <label> <path> <text that must be there> [<text that must not be>]: fetches a page from the running site.
page() {
  local label="$1" path="$2" want="$3" unwanted="${4:-}"
  local status
  status="$(curl -s -o "$out/$label.html" -w '%{http_code}' "$base$path")"
  if [[ "$status" == 200 ]] && grep -qF "$want" "$out/$label.html" && { [[ -z "$unwanted" ]] || ! grep -qF "$unwanted" "$out/$label.html"; }; then
    printf '%s\tpass\t%s renders "%s"%s\n' "$label" "$path" "$want" "${unwanted:+ and not \"$unwanted\"}" >>"$out/summary.tsv"
  else
    printf '%s\tFAIL\t%s answered %s; wanted "%s"%s\n' "$label" "$path" "$status" "$want" "${unwanted:+ and not \"$unwanted\"}" >>"$out/summary.tsv"
    failures=$((failures + 1))
  fi
}

# offline <label> <path> <text>: the site no longer shows the text there (any status but 200, or 200 without it).
offline() {
  local label="$1" path="$2" text="$3"
  local status
  status="$(curl -s -o "$out/$label.html" -w '%{http_code}' "$base$path")"
  if [[ "$status" != 200 ]] || ! grep -qF "$text" "$out/$label.html"; then
    printf '%s\tpass\t%s no longer shows "%s" (%s)\n' "$label" "$path" "$text" "$status" >>"$out/summary.tsv"
  else
    printf '%s\tFAIL\t%s still shows "%s"\n' "$label" "$path" "$text" >>"$out/summary.tsv"
    failures=$((failures + 1))
  fi
}

started=0
if ! o serve --status | jq -e '.data.state == "running"' >/dev/null; then
  o serve | jq -c 'if .ok then {state: .data.state, agent: .data.agentDll, cms: .data.agent.cmsVersion} else .error end'
  started=1
fi
status="$(o serve --status)"
base="$(jq -r '.data.url' <<<"$status")"
echo "site: $site, agent $(jq -r '.data.agentDll' <<<"$status"), CMS $(jq -r '.data.agent.cmsVersion' <<<"$status"), $base"

start="$(o resolve / | jq -r '.data.ref')"
stamp="$(date +%H%M%S)"
name="Write battery $stamp"
segment="write-battery-$stamp"
png="$out/pixel.png"
base64 -d >"$png" <<<'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=='

step create '.ok and .data.published' "create a published page" -- \
  create "$start" --type StandardPage --name "$name" MetaTitle="Battery title" MainBody="<p>Battery body <strong>one</strong></p>" --publish
p="$(jq -r '.data.ref' "$out/create.json")"
step set-rich-text '.ok and .data.published and (.data.changes | map(.property) | index("MainBody"))' "set rich text, a string list (--values) and a string, publish" -- \
  set "$p" MetaDescription="Battery description" --values "{\"MainBody\":\"<p>Battery body <em>two</em> with <a href=\\\"/en/\\\">a link</a></p>\",\"MetaKeywords\":[\"battery\",\"cms13\"]}" --publish
step get-rich-text '.ok and (.data.properties.MainBody.value | test("Battery body <em>two</em>")) and (.data.properties.MetaKeywords.value == ["battery","cms13"])' "read back rich text and the list" -- \
  get "$p"

step upload '.ok and .data.published' "upload an image to the page's own folder" -- \
  upload "$png" --for "$p" --name "battery-pixel.png" --publish
img="$(jq -r '.data.ref' "$out/upload.json")"
step block-one '.ok and .data.published' "create a shared block in the page's own folder, with the image" -- \
  block create --type TeaserBlock --name "Battery teaser one" --for "$p" Heading="Battery teaser heading one" Text="Battery teaser text" Image="$img" --publish
b1="$(jq -r '.data.ref' "$out/block-one.json")"
step block-two '.ok and .data.published' "create a second block" -- \
  block create --type TeaserBlock --name "Battery teaser two" --for "$p" Heading="Battery teaser heading two" Text="Second" Image="$img" --publish
b2="$(jq -r '.data.ref' "$out/block-two.json")"
step block-text '.ok and .data.published' "change the block's text" -- \
  set "$b1" Text="Battery teaser text, changed" --publish

step area-add-wide '.ok and .data.published' "area add with display option wide" -- \
  area "$p" MainContentArea add "$b1" --display wide --publish
step area-add-narrow '.ok and .data.published' "area add at 0 with display option narrow" -- \
  area "$p" MainContentArea add "$b2" --at 0 --display narrow --publish
step area-read '.ok and ([.data.properties.MainContentArea.value[] | {ref, displayOption}] == [{ref: "'"$b2"'", displayOption: "narrow"}, {ref: "'"$b1"'", displayOption: "wide"}])' "read the area back: both items, both display options" -- \
  get "$p"
step area-move '.ok and .data.published' "area move 0 to 1" -- \
  area "$p" MainContentArea move 0 1 --publish
step area-moved '.ok and ([.data.properties.MainContentArea.value[] | .ref] == ["'"$b1"'", "'"$b2"'"])' "the items swapped places" -- \
  get "$p"
page render-area "/en/$segment/" "Battery teaser heading two"
# Alloy renders a display option as the item's CSS class.
page render-display-narrow "/en/$segment/" 'class="block narrow'
page render-display-wide "/en/$segment/" 'class="block wide'
step area-remove '.ok and .data.published' "area remove by ref" -- \
  area "$p" MainContentArea remove "ref:$b2" --publish
step area-removed '.ok and ([.data.properties.MainContentArea.value[] | {ref, displayOption}] == [{ref: "'"$b1"'", displayOption: "wide"}])' "one item left, display option kept" -- \
  get "$p"

step translate '.ok and .data.published' "translate to Swedish and publish" -- \
  translate "$p" --lang sv --name "Skrivbatteri $stamp" MainBody="<p>Svensk batteritext</p>" --publish

step draft '.ok and (.data.published | not)' "save a draft" -- \
  set "$p" MetaTitle="Draft only title"
draft="$(jq -r '.data.version' "$out/draft.json")"
step discard '.ok' "discard that draft" -- \
  discard "$draft"
step discarded '.ok and (.data | map(.ref) | index("'"$draft"'") | not)' "the draft is gone" -- \
  versions "$p"
step draft-two '.ok and (.data.published | not)' "save another draft" -- \
  set "$p" TeaserText="Battery teaser text for listings"
step publish '.ok and .data.published' "publish the latest version" -- \
  publish "$p"

step unpublish '.ok' "unpublish the Swedish branch" -- \
  unpublish "$p" --lang sv
previous="$(jq -r '.data.previouslyPublished // empty' "$out/unpublish.json")"
sv_path="$(o url "$p" --lang sv | jq -r '.data.languages[0].url // .data.url // empty' | sed -E 's#^https?://[^/]+##')"
offline render-sv-offline "${sv_path:-/sv/}" "Svensk batteritext"
step republish '.ok and .data.published' "publish the Swedish branch again (the unpublish's undo)" -- \
  publish "$p" --lang sv --version "${previous#*_}"

step access '.ok' "break inheritance and grant a role" -- \
  access "$p" --break-inheritance --grant CmsAdmins=FullAccess --grant CmsEditors=Read,Edit,Publish
step access-read '.ok and (.data.inherited | not) and ([.data.entries[] | select(.name == "CmsEditors")] | length == 1)' "read the access rights back" -- \
  access "$p"

step create-target '.ok and .data.published' "create a page to move into" -- \
  create "$start" --type StandardPage --name "$name target" --publish
target="$(jq -r '.data.ref' "$out/create-target.json")"
step move '.ok' "move the page below the target" -- \
  move "$p" --to "$target"
step moved '.ok and (.data.parent == "'"$target"'")' "its parent is the target" -- \
  get "$p"
step move-back '.ok' "move it back" -- \
  move "$p" --to "$start"

page render-en "/en/$segment/" "Battery body <em>two</em>" "Battery teaser heading two"
page render-block "/en/$segment/" "Battery teaser text, changed"
page render-image "/en/$segment/" "battery-pixel.png"
page render-sv "${sv_path:-/sv/}" "Svensk batteritext"

step delete '.ok' "delete the page (to the recycle bin)" -- \
  delete "$p" --ignore-references
step deleted '.ok and (.data.parent == "2")' "it is in the recycle bin" -- \
  get "$p"
step restore '.ok' "restore it" -- \
  restore "$p"
step restored '.ok and (.data.parent == "'"$start"'")' "its parent is the start page again" -- \
  get "$p"
page render-restored "/en/$segment/" "Battery body <em>two</em>"

# Clean up: both pages go to the recycle bin.
o delete "$p" --ignore-references >/dev/null
o delete "$target" --ignore-references >/dev/null
if [[ "$started" == 1 ]]; then
  o serve --stop | jq -c '{stopped: .data.stopped}'
fi

column -t -s $'\t' "$out/summary.tsv"
echo "$(grep -c $'\tpass\t' "$out/summary.tsv") passed, $failures failed"
[[ "$failures" == 0 ]]
