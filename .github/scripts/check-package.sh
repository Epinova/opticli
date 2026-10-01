#!/usr/bin/env bash
# Checks a packed OptiCli .nupkg: the site agent ships under agent/, and the packed tool starts and prints its
# embedded skill. Usage: check-package.sh <dir with the .nupkg>
set -euo pipefail

package="$(find "$1" -maxdepth 1 -name 'OptiCli.*.nupkg' | head -n 1)"
if [ -z "$package" ]; then
  echo "::error::No OptiCli .nupkg in $1."
  exit 1
fi

contents="$(mktemp -d)"
trap 'rm -rf "$contents"' EXIT
unzip -q "$package" -d "$contents"

tool="$contents/tools/net8.0/any"
for file in opticli.dll agent/OptiCli.Agent.dll; do
  if [ ! -f "$tool/$file" ]; then
    echo "::error::$(basename "$package") has no tools/net8.0/any/$file."
    exit 1
  fi
done

version="$(dotnet "$tool/opticli.dll" --version)"
skill="$(dotnet "$tool/opticli.dll" skill print | sed -n 's/^opticli-version:[[:space:]]*//p')"
echo "$(basename "$package"): opticli $version, skill $skill"
if [ "$version" != "$skill" ]; then
  echo "::error::The packed tool is version $version but its embedded skill says $skill."
  exit 1
fi
