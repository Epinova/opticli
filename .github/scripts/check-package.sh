#!/usr/bin/env bash
# Checks a packed OptiCli .nupkg: both site agent builds ship under agent/ (cms12: .NET 8, cms13: .NET 10), and the packed
# tool starts and prints its embedded skill. Usage: check-package.sh <dir with the .nupkg>
set -euo pipefail

package="$(find "$1" -maxdepth 1 -name 'OptiCli.*.nupkg' ! -name 'OptiCli.Mcp.*' | head -n 1)"
if [ -z "$package" ]; then
  echo "::error::No OptiCli .nupkg in $1."
  exit 1
fi

contents="$(mktemp -d)"
trap 'rm -rf "$contents"' EXIT
unzip -q "$package" -d "$contents"

tool="$contents/tools/net8.0/any"
for file in opticli.dll agent/cms12/OptiCli.Agent.dll agent/cms13/OptiCli.Agent.dll; do
  if [ ! -f "$tool/$file" ]; then
    echo "::error::$(basename "$package") has no tools/net8.0/any/$file."
    exit 1
  fi
done
# Each build is the one for its CMS major: serve injects agent/cms<major>/ into the site, and the other build can't run there.
for build in "cms12 v8.0" "cms13 v10.0"; do
  read -r folder framework <<<"$build"
  if ! grep -aq ".NETCoreApp,Version=$framework" "$tool/agent/$folder/OptiCli.Agent.dll"; then
    echo "::error::$(basename "$package")'s agent/$folder/OptiCli.Agent.dll isn't built for .NET ${framework#v}."
    exit 1
  fi
done
if [ -e "$tool/agent/OptiCli.Agent.dll" ]; then
  echo "::error::$(basename "$package") still has the single agent/OptiCli.Agent.dll of older packages."
  exit 1
fi

version="$(dotnet "$tool/opticli.dll" --version)"
skill="$(dotnet "$tool/opticli.dll" skill print | sed -n 's/^opticli-version:[[:space:]]*//p')"
echo "$(basename "$package"): opticli $version, skill $skill"
if [ "$version" != "$skill" ]; then
  echo "::error::The packed tool is version $version but its embedded skill says $skill."
  exit 1
fi
