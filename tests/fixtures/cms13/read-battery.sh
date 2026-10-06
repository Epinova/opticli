#!/usr/bin/env bash
# Runs every opticli read command against one site and saves each command's JSON output, for comparing CMS 12 with
# CMS 13 (and a CMS 13 upgrade with the CMS 12 database it came from). Reads only: it never starts the site.
#
#   read-battery.sh <site directory> <output directory>
#
# Run inside the dev container, e.g. for the upgraded CMS 13 site and the CMS 12 site it was copied from:
#   tests/fixtures/cms13/read-battery.sh /demo/Alloy13Up /tmp/battery/up
#   tests/fixtures/cms13/read-battery.sh /demo/Alloy /tmp/battery/alloy
#   python3 tests/fixtures/cms13/compare-battery.py /tmp/battery/alloy /tmp/battery/up
#
# Refs are found per site (the start page, a product page, a shared block, an image, the Visual Builder fixture), so the
# same battery runs on the fresh site, whose ids differ. Each command's output is <label>.json; summary.tsv has one line
# per command: label, ok/failed, the error code, the command line. OPTICLI defaults to the worktree's Debug build.
# `env` is left out: it registers an external site for the project, which later commands then look for.
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
rm -f "$out"/*.json "$out/summary.tsv"
cd "$site" || exit 1

o() { $opticli "$@" --json 2>/dev/null; }
id_of() { o resolve "$1" | jq -r 'if .ok then .data.ref else empty end'; }
first_of() { o find --type "$1" --limit 1 | jq -r 'if .ok then (.data[0].ref // empty) else empty end'; }

start="$(id_of /)"
product="$(id_of /en/alloy-plan/)"
image="$(id_of /globalassets/pexels-charts.jpg)"
block="$(first_of TeaserBlock)"
site_url="$(o sites | jq -r '.data[0].url // empty')"
vb="$(id_of /en/visual-builder-fixture/)"
vb_element="$(o search "VB shared element" | jq -r '[.data[]? | select(.type == "VbTextElement")][0].ref // empty')"
echo "refs: start=$start product=$product image=$image block=$block vb=${vb:-none} vbElement=${vb_element:-none} url=$site_url"

run() {
  local label="$1"
  shift
  local file="$out/$label.json"
  $opticli "$@" --json >"$file" 2>"$out/$label.stderr"
  [[ -s "$out/$label.stderr" ]] || rm -f "$out/$label.stderr"
  local ok code
  ok="$(jq -r 'if .ok then "ok" else "failed" end' "$file" 2>/dev/null || echo "invalid")"
  code="$(jq -r '.error.code // ""' "$file" 2>/dev/null)"
  printf '%s\t%s\t%s\t%s\n' "$label" "$ok" "$code" "$*" >>"$out/summary.tsv"
}

run doctor doctor
run db-list db list
run sites sites
run languages languages
run types types
run types-instances types --sort instances
run types-unused types --unused
run types-orphaned types --orphaned
run type-start type StartPage
run type-product type ProductPage
run type-teaser type TeaserBlock
run tree-root tree 1
run tree-start tree "$start" --depth 3
run children-start children "$start"
run ancestors ancestors "$product"
run get-start get "$start"
run get-product get "$product"
run get-path get /en/about-us/
run get-block get "$block"
run get-image get "$image"
run url-start url "$start"
run url-product url "$product"
run url-image url "$image"
run resolve-root resolve /
run resolve-product resolve /en/alloy-plan/
run resolve-sv resolve /sv/
run resolve-assets resolve /globalassets/pexels-charts.jpg
[[ -n "$site_url" ]] && run resolve-host resolve "${site_url%/}/en/alloy-plan/"
run resolve-unknown-host resolve http://unknown.example/en/alloy-plan/
run find-standard find --type StandardPage
run find-where find --type ProductPage --where "Name~Alloy"
run find-under find --type StandardPage --under /en/about-us/
run find-draft find --type StandardPage --status draft
run search search Alloy
search_text="reseller"
run search-rich search "$search_text"
run where-used-block where-used "$block"
run where-used-image where-used "$image"
run allowed-in allowed-in TeaserBlock
run versions-start versions "$start"
run history-start history "$start"
run drafts drafts
run projects projects
run trash trash
run categories categories
run visitor-groups visitor-groups
run jobs jobs --all
run jobs-log jobs log PageArchiveJob
run jobs-log-readable jobs log "Automatic Emptying of Trash"
run jobs-log-all jobs log
run blob blob "$image"
run access access "$start"
run sql sql "SELECT COUNT(*) AS n FROM tblContent"

if [[ -n "$vb" ]]; then
  run vb-get get "$vb"
  run vb-tree tree "$start" --depth 1
  run vb-versions versions "$vb"
  run vb-url url "$vb"
  run vb-find find --type VbExperience
  run vb-find-element find --type VbTextElement
  run vb-search search "VB fixture"
  run vb-type-experience type VbExperience
  run vb-type-section type VbSection
  run vb-type-element type VbTextElement
  run vb-history history "$vb"
  [[ -n "$vb_element" ]] && run vb-where-used where-used "$vb_element"
  [[ -n "$vb_element" ]] && run vb-get-element get "$vb_element"
  for version in $(jq -r '.data[]?.ref // empty' "$out/vb-versions.json"); do
    run "vb-get-$version" get "$version"
  done
  blueprint="$(o tree 1 --depth 2 | jq -r '[.. | objects | select(.name? == "Blueprints") | .children[]?.ref][0] // empty')"
  [[ -n "$blueprint" ]] && run vb-get-blueprint get "$blueprint"
fi

failed="$(awk -F'\t' '$2 != "ok"' "$out/summary.tsv")"
echo "$(wc -l <"$out/summary.tsv") commands, $(awk -F'\t' '$2 == "ok"' "$out/summary.tsv" | wc -l) ok"
if [[ -n "$failed" ]]; then
  echo "not ok:"
  echo "$failed"
fi
