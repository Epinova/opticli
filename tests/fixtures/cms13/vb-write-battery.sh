#!/usr/bin/env bash
# Builds and changes a Visual Builder experience on a CMS 13 site with opticli's writes, checks each result, and checks
# that the site renders what was written: create with a whole composition, composition add/set/move/remove (by key and
# by name, inline and shared elements, display templates and settings), write-back of get's composition, a section from a
# section blueprint, an experience from a blueprint, a content variation, an apply plan, publish, and the refusals.
#
#   vb-write-battery.sh <site directory> <output directory>
#
# Needs the Visual Builder fixture (tests/fixtures/cms13/VisualBuilderFixture.cs, which setup.sh adds), so it runs on the
# CMS 13 test sites and the CMS 13 edge-case site, inside the dev container:
#   tests/fixtures/cms13/vb-write-battery.sh /demo/Alloy13 /tmp/vb-writes/fresh
#   tests/fixtures/cms13/vb-write-battery.sh /demo/Alloy13Up /tmp/vb-writes/up
#
# It starts `opticli serve` when the site isn't running and stops it again at the end. Everything it makes is named
# "VB battery <time>" and ends in the recycle bin. Each step's output is <label>.json; summary.tsv has one line per step:
# label, pass/FAIL, what was checked. Exits 1 when a step fails. OPTICLI defaults to the worktree's Debug build.
set -uo pipefail

if [[ $# -ne 2 ]]; then
  sed -n '2,17p' "$0"
  exit 1
fi

site="$1"
out="$2"
here="$(cd "$(dirname "$0")" && pwd)"
opticli="${OPTICLI:-dotnet $here/../../../src/OptiCli/bin/Debug/net8.0/opticli.dll}"

mkdir -p "${out:?}"
rm -f "${out:?}"/*.json "${out:?}"/*.txt "${out:?}"/*.stderr "${out:?}/summary.tsv"
cd "$site" || exit 1

o() { $opticli "$@" --json 2>/dev/null; }
failures=0

# step <label> <jq check on the output> <description> -- <opticli arguments>
step() {
  local label="$1" check="$2" what="$3"
  shift 4
  $opticli "$@" --json >"$out/$label.json" 2>"$out/$label.stderr"
  [[ -s "$out/$label.stderr" ]] || rm -f "${out:?}/$label.stderr"
  if jq -e "$check" "$out/$label.json" >/dev/null 2>&1; then
    printf '%s\tpass\t%s\n' "$label" "$what" >>"$out/summary.tsv"
  else
    printf '%s\tFAIL\t%s (%s)\n' "$label" "$what" "$(jq -c '.error // .data' "$out/$label.json" 2>/dev/null | head -c 300)" >>"$out/summary.tsv"
    failures=$((failures + 1))
  fi
}

# page <label> <path> <text that must be there> [<text that must not be>]: the fixture's controller renders an experience as text.
page() {
  local label="$1" path="$2" want="$3" unwanted="${4:-}"
  local status
  status="$(curl -s -o "$out/$label.txt" -w '%{http_code}' "$base$path")"
  if [[ "$status" == 200 ]] && grep -qF "$want" "$out/$label.txt" && { [[ -z "$unwanted" ]] || ! grep -qF "$unwanted" "$out/$label.txt"; }; then
    printf '%s\tpass\t%s renders "%s"%s\n' "$label" "$path" "$want" "${unwanted:+ and not \"$unwanted\"}" >>"$out/summary.tsv"
  else
    printf '%s\tFAIL\t%s answered %s; wanted "%s"%s\n' "$label" "$path" "$status" "$want" "${unwanted:+ and not \"$unwanted\"}" >>"$out/summary.tsv"
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

# The fixture: its experience's parent takes experiences; its shared element is placed by reference.
parent="$(o get 7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b01 --fields name | jq -r '.data.parent')"
shared="$(o get 7a1c2e3d-5b6f-4a70-9c81-0d2e3f4a5b02 --fields name | jq -r '.data.ref')"
if [[ -z "$parent" || "$parent" == null || -z "$shared" || "$shared" == null ]]; then
  echo "The Visual Builder fixture isn't on this site (tests/fixtures/cms13/setup.sh adds it)."
  exit 1
fi
stamp="$(date +%H%M%S)"
name="VB battery $stamp"

cat >"$out/composition.json" <<EOF
{"sections": [
  {"type": "VbSection", "name": "Top", "displayTemplate": "vbSection", "displaySettings": {"background": "light"}, "rows": [
    {"name": "Row", "columns": [
      {"name": "Left", "elements": [{"type": "VbTextElement", "name": "Hello", "properties": {"Heading": "Battery hello", "Body": "<p>Battery <strong>rich</strong> text</p>"}}]},
      {"name": "Right", "elements": [{"ref": "$shared", "name": "Shared one"}, {"type": "VbLinkElement", "name": "Go", "properties": {"Heading": "Battery link", "Target": "$parent", "Link": "https://example.com/battery"}}]}]}]},
  {"type": "VbBanner", "name": "Banner", "properties": {"Title": "Battery banner"}}]}
EOF

step create-dry '.ok and .data.dryRun and (.data.composition | map(.name) == ["Top", "Banner"])' "create dry run lists the composition's sections" -- \
  create "$parent" --type VbExperience --name "$name" Summary="Battery summary" composition=@"$out/composition.json" --dry-run
step create '.ok and .data.saved and (.data.published | not)' "create the experience as a draft with its whole composition" -- \
  create "$parent" --type VbExperience --name "$name" Summary="Battery summary" composition=@"$out/composition.json"
e="$(jq -r '.data.ref' "$out/create.json")"
step get-composition '.ok and (.data.composition.sections | map(.name) == ["Top", "Banner"]) and (.data.composition.sections[1].nodeType == "component") and ([.data.composition.sections[0].rows[0].columns[1].elements[0].content.ref] == ["'"$shared"'"])' "get shows the composition, the banner as a component, the shared element by ref" -- \
  get "$e" --version latest --fields composition

step add-element '.ok and (.data.composition | length == 1) and (.data.composition[0].change == "added")' "add an inline element to a column named by name" -- \
  composition "$e" add element --in Left --type VbTextElement --name Added Heading="Battery added" "Body=<p>Added body</p>"
added="$(jq -r '.data.composition[0].key' "$out/add-element.json")"
step set-element '.ok and (.data.composition[0].changes | map(.field) | index("properties.Heading"))' "set an element's heading and its display template and setting" -- \
  composition "$e" set Hello Heading="Battery hello again" --template vbElement --setting color=accent
step move-element '.ok and (.data.composition[0].change == "moved") and (.data.composition[0].at == 0)' "move the added element (by key) to the other column" -- \
  composition "$e" move "$added" --in Right --at 0
step remove-banner '.ok and (.data.composition[0].change == "removed")' "remove the banner" -- \
  composition "$e" remove Banner
step bad-template '(.ok | not) and (.error.code == "usage") and (.error.message | test("no display template"))' "an unknown display template is refused" -- \
  composition "$e" set Hello --template nope --dry-run
step bad-place '(.ok | not) and (.error.message | test("outline"))' "an element in the outline is refused" -- \
  composition "$e" add element --type VbTextElement --dry-run
step bad-storage '(.ok | not) and (.error.message | test("stores its Visual Builder composition"))' "the storage properties aren't set directly" -- \
  set "$e" UnstructuredData= --dry-run

o get "$e" --version latest --fields composition --full | jq '{composition: .data.composition}' >"$out/written-back.json"
step write-back '.ok and (.data.composition == null)' "write the composition get shows back: nothing changes" -- \
  set "$e" --values "$(cat "$out/written-back.json")" --dry-run
jq '.composition.sections[0].rows[0].columns[1].name = "Right column"' "$out/written-back.json" >"$out/renamed.json"
step write-renamed '.ok and (.data.composition | map(.change) == ["changed"])' "write it back with a column renamed" -- \
  set "$e" --values "$(cat "$out/renamed.json")"

section_blueprint="$(o find --type VbSection --blueprints | jq -r '[.data[] | select(.blueprint)][0].ref')"
step add-blueprint-section '.ok and (.data.composition[0].holds >= 3)' "add a section from the section blueprint" -- \
  composition "$e" add section --blueprint "$section_blueprint" --name "Copied section"
step publish '.ok and .data.published' "publish the experience" -- \
  publish "$e"
$opticli get "$e" --text >"$out/get-text.txt" 2>&1
url="$(o get "$e" --fields name | jq -r '.data.url')"
page render "$url" "Battery hello again" "Battery banner"
page render-blueprint "$url" "From a blueprint"

step variation '.ok and .data.saved' "save a content variation of the experience" -- \
  set "$e" Summary="Battery variation summary" --variation vbBattery
step variation-composition '.ok and .data.published' "change the variation's composition and publish it" -- \
  composition "$e" set Hello Heading="Battery hello in the variation" --variation vbBattery --publish
step variation-read '.ok and (.data.variation == "vbBattery") and (.data.properties.Summary.value == "Battery variation summary")' "get --variation shows it" -- \
  get "$e" --variation vbBattery
step own-unchanged '.ok and (.data.properties.Summary.value == "Battery summary") and (.data.variation == null)' "the experience itself is as it was" -- \
  get "$e"
page render-own "$url" "Battery hello again" "in the variation"

experience_blueprint="$(o find --type VbExperience --blueprints | jq -r '[.data[] | select(.blueprint)][0].ref')"
step from-blueprint '.ok and .data.saved and (.data.composition | length >= 1)' "create an experience from a blueprint" -- \
  create "$parent" --blueprint "$experience_blueprint" --name "$name from blueprint" --publish
b="$(jq -r '.data.ref' "$out/from-blueprint.json")"

cat >"$out/vb-plan-input.json" <<EOF
{"operations": [
  {"op": "composition", "ref": "$e", "action": "add", "nodeType": "element", "in": "Left", "value": {"type": "VbTextElement", "name": "From plan", "properties": {"Heading": "Battery from plan"}}},
  {"op": "composition", "ref": "$e", "action": "move", "node": "From plan", "in": "Right column", "at": 0},
  {"op": "publish", "ref": "$e"}]}
EOF
step plan-dry '.ok and (.data.operations | map(.status) == ["valid", "simulated", "simulated"])' "a plan of composition edits is dry-run step after step" -- \
  apply "$out/vb-plan-input.json" --dry-run
step plan '.ok and (.data.operations | map(.status) | all(. == "saved"))' "run the plan" -- \
  apply "$out/vb-plan-input.json"
page render-plan "$url" "Battery from plan"

# Clean up: both experiences go to the recycle bin.
o delete "$e" --ignore-references >/dev/null
o delete "$b" --ignore-references >/dev/null
if [[ "$started" == 1 ]]; then
  o serve --stop | jq -c '{stopped: .data.stopped}'
fi

column -t -s $'\t' "$out/summary.tsv"
echo "$(grep -c $'\tpass\t' "$out/summary.tsv") passed, $failures failed"
[[ "$failures" == 0 ]]
