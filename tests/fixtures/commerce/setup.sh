#!/usr/bin/env bash
# Builds opticli's two Commerce test sites from nothing, from Optimizely's Commerce template (`dotnet new
# epi-commerce-empty`, EPiServer.Templates 2.0.1):
# - 12: CMS 12 + Commerce 14 in /demo/Commerce12 (net8.0), on the empty databases opticli-commerce12 (the CMS's,
#   EPiServerDB) and opticli-commerce12-ecf (Commerce's, EcfSqlConnection), port 5150.
# - 13: CMS 13 + Commerce 15 in /demo/Commerce13 (net10.0), on opticli-commerce13 and opticli-commerce13-ecf, port 5151.
# Both get two price types of their own (MEMBER 3 and OUTLET 4, in appsettings.json) and CommerceFixture.cs: a start
# page, a teaser block, an image media type, a small catalog model (a fashion node, product, variant and bundle), HTML
# pages for them, and Commerce's catalog routing below the start page. The CMS and Commerce make their schemas at the
# first start; then opticli (this checkout's build, or OPTICLI) serves the site and applies fixture.plan.json: the start
# page and a site definition for it (hosts commerce.localhost and *), an image, and a small catalog ("Fixture": a node,
# a product and a variant with the image in their CommerceMediaCollection, a price and stock) that the start page
# points at.
#
#   setup.sh [12|13|both]        (default: both)
#
# Needs dotnet with the EPiServer.Templates template package, sqlcmd and curl, and SQL Server credentials in
# SQLCMDSERVER, SQLCMDUSER and SQLCMDPASSWORD (default: localhost,1433 and sa). Run it again to update the model: sites
# and databases are kept unless FRESH=1, which makes both again from nothing (and drops their databases).
# Other settings: CMS12_VERSION (12.34.9), COMMERCE14_VERSION (14.48.0), CMS13_VERSION (13.5.1), COMMERCE15_VERSION
# (15.5.0), DIR12, DIR13, DB12, DB13 (the ECF database is the name plus -ecf), PORT12 (5150), PORT13 (5151), OPTICLI
# (default: dotnet <this checkout>/src/OptiCli/bin/Debug/net8.0/opticli.dll, built when missing).
set -euo pipefail

WHICH=${1:-both}
case "$WHICH" in 12|13|both) ;; *) sed -n '2,22p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;; esac
HERE=$(cd "$(dirname "$0")" && pwd)
REPO=$(cd "$HERE/../../.." && pwd)
CMS12_VERSION=${CMS12_VERSION:-12.34.9}
COMMERCE14_VERSION=${COMMERCE14_VERSION:-14.48.0}
CMS13_VERSION=${CMS13_VERSION:-13.5.1}
COMMERCE15_VERSION=${COMMERCE15_VERSION:-15.5.0}
DIR12=${DIR12:-/demo/Commerce12}
DIR13=${DIR13:-/demo/Commerce13}
DB12=${DB12:-opticli-commerce12}
DB13=${DB13:-opticli-commerce13}
PORT12=${PORT12:-5150}
PORT13=${PORT13:-5151}
export SQLCMDSERVER=${SQLCMDSERVER:-localhost,1433} SQLCMDUSER=${SQLCMDUSER:-sa}
: "${SQLCMDPASSWORD:?set SQLCMDPASSWORD to the SQL Server password}"
export SQLCMDPASSWORD

for db in "$DB12" "$DB13"; do
  case "$db" in opticli-commerce*) ;; *) echo "The databases must be named opticli-commerce...: this script drops them with FRESH=1." >&2; exit 1 ;; esac
  case "$db" in *[!A-Za-z0-9_-]*) echo "Database names may only have letters, digits, '_' and '-'." >&2; exit 1 ;; esac
done
for dir in "$DIR12" "$DIR13"; do
  if [ "$(realpath -m "$dir")" = / ]; then echo "$dir is the root directory; pick another directory." >&2; exit 1; fi
done

sql() { sqlcmd -C -b -h -1 -W -Q "SET NOCOUNT ON; $1"; }
db_exists() { [ "$(sql "SELECT COUNT(*) FROM sys.databases WHERE name = N'$1'")" != 0 ]; }
empty_db() {
  echo "== empty database: $1"
  sql "IF DB_ID(N'$1') IS NOT NULL BEGIN ALTER DATABASE [$1] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$1]; END; CREATE DATABASE [$1];"
}
connection() { printf 'Server=%s;Database=%s;User Id=%s;Password=%s;TrustServerCertificate=True;MultipleActiveResultSets=True' "$SQLCMDSERVER" "$1" "$SQLCMDUSER" "$SQLCMDPASSWORD"; }

# Points both connection strings (appsettings.Development.json, where the template puts them) at the site's databases.
connect() {
  local dir=$1 db=$2 cms ecf
  cms=$(connection "$db" | sed 's/[&|\\]/\\&/g')
  ecf=$(connection "$db-ecf" | sed 's/[&|\\]/\\&/g')
  sed -i -E "s|(\"EPiServerDB\": *\")[^\"]*(\")|\1$cms\2|; s|(\"EcfSqlConnection\": *\")[^\"]*(\")|\1$ecf\2|" "$dir/appsettings.Development.json"
  if ! grep -q "Database=$db;" "$dir/appsettings.Development.json" || ! grep -q "Database=$db-ecf;" "$dir/appsettings.Development.json"; then
    echo "No EPiServerDB and EcfSqlConnection connection strings found in $dir/appsettings.Development.json." >&2
    exit 1
  fi
}

# Two price types of the site's own next to Commerce's three (appsettings.json), as sites configure customer groups'
# prices: Commerce writes them to its PriceType table at start.
price_types() {
  python3 - "$1/appsettings.json" <<'EOF'
import json, sys
path = sys.argv[1]
with open(path, encoding="utf-8-sig") as f:
    settings = json.load(f)
options = settings.setdefault("EPiServer", {}).setdefault("Commerce", {}).setdefault("CatalogOptions", {})
options["SalePriceTypes"] = [
    {"Key": "MEMBER", "Value": 3, "Description": "Members"},
    {"Key": "OUTLET", "Value": 4, "Description": "Outlet"},
]
with open(path, "w", encoding="utf-8") as f:
    json.dump(settings, f, indent=2)
    f.write("\n")
EOF
}

# The template's project, at the given framework and versions. On CMS 13 the ASP.NET Identity UI is a package of its own.
project() {
  local dir=$1 name=$2 framework=$3 cms=$4 commerce=$5
  sed -i -E "s|<TargetFramework>[^<]*</TargetFramework>|<TargetFramework>$framework</TargetFramework>|" "$dir/$name.csproj"
  sed -i -E "s|(Include=\"EPiServer\.CMS\" Version=\")[^\"]*\"|\1$cms\"|; s|(Include=\"EPiServer\.Commerce\" Version=\")[^\"]*\"|\1$commerce\"|" "$dir/$name.csproj"
  # Commerce 15's admin UI loads EPiServer.OptimizelyIdentity without depending on its package.
  if [ "${cms%%.*}" = 13 ] && ! grep -q 'EPiServer.Cms.UI.AspNetIdentity' "$dir/$name.csproj"; then
    sed -i -E "s|(<PackageReference Include=\"EPiServer\.Commerce\" [^>]*/>)|\1\n    <PackageReference Include=\"EPiServer.Cms.UI.AspNetIdentity\" Version=\"$cms\" />\n    <PackageReference Include=\"EPiServer.OptimizelyIdentity\" Version=\"$cms\" />|" "$dir/$name.csproj"
  fi
  sed -i -E "s|(Include=\"EPiServer\.(Cms\.UI\.AspNetIdentity\|OptimizelyIdentity)\" Version=\")[^\"]*\"|\1$cms\"|" "$dir/$name.csproj"
  # The template was written for CMS 12. On CMS 13 AddCmsAspNetIdentity is in EPiServer.DependencyInjection, and
  # AddCommerce no longer adds the CMS.
  if [ "${cms%%.*}" = 13 ] && ! grep -q '^using EPiServer.DependencyInjection;' "$dir/Startup.cs"; then
    sed -i '1i using EPiServer.DependencyInjection;' "$dir/Startup.cs"
    sed -i -E 's|^( *)\.AddCommerce\(\)|\1.AddCms()\n\1.AddCommerce()|' "$dir/Startup.cs"
  fi
  if ! grep -q "Include=\"EPiServer.Commerce\" Version=\"$commerce\"" "$dir/$name.csproj" || ! grep -q "<TargetFramework>$framework<" "$dir/$name.csproj"; then
    echo "Couldn't set the framework and versions in $dir/$name.csproj." >&2
    exit 1
  fi
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

# Builds and starts the site (Development, without opticli's agent) until it answers, which makes the CMS's and
# Commerce's schemas and registers the content model, then stops it.
first_start() {
  local dir=$1 name=$2 framework=$3 port=$4 url="http://localhost:$4" log="$1/App_Data/setup-site.log" code=000
  echo "== build $dir"
  (cd "$dir" && dotnet build -nologo -v q -clp:ErrorsOnly)
  if curl -s -o /dev/null "$url/"; then
    echo "Something already listens on port $port; stop it or set another port." >&2
    exit 1
  fi
  echo "== start $url"
  mkdir -p "$dir/App_Data"
  (cd "$dir" && ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=$url exec dotnet "bin/Debug/$framework/$name.dll") >"$log" 2>&1 &
  SITE_PID=$!
  for _ in $(seq 1 180); do
    if ! kill -0 "$SITE_PID" 2>/dev/null; then
      tail -n 40 "$log" >&2
      echo "The site stopped; its log: $log" >&2
      exit 1
    fi
    # An empty CMS has no site yet: the login page answers once the site has started.
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 120 "$url/util/login" || true)
    [ "$code" = 200 ] && break
    sleep 2
  done
  if [ "$code" != 200 ]; then
    echo "The login page answered $code; the site's log: $log" >&2
    exit 1
  fi
  echo "login page: $code"
  stop_site
}

schema() { sqlcmd -C -b -h -1 -W -d "$1" -Q "SET NOCOUNT ON; DECLARE @v int; EXEC @v = dbo.sp_DatabaseVersion; SELECT @v"; }
commerce_schema() { sqlcmd -C -b -h -1 -W -d "$1" -Q "SET NOCOUNT ON; SELECT TOP 1 CONCAT(Major, '.', Minor, '.', Patch) FROM dbo.SchemaVersion ORDER BY Major DESC, Minor DESC, Patch DESC" 2>/dev/null || echo "(none)"; }

build_site() {
  local major=$1 dir name framework cms commerce db port
  if [ "$major" = 12 ]; then
    dir=$DIR12 name=Commerce12 framework=net8.0 cms=$CMS12_VERSION commerce=$COMMERCE14_VERSION db=$DB12 port=$PORT12
  else
    dir=$DIR13 name=Commerce13 framework=net10.0 cms=$CMS13_VERSION commerce=$COMMERCE15_VERSION db=$DB13 port=$PORT13
  fi
  if [ "${FRESH:-0}" = 1 ] || [ ! -f "$dir/$name.csproj" ]; then
    echo "== new site: $dir"
    rm -rf "$dir"
    dotnet new epi-commerce-empty -n "$name" -o "$dir" --no-update-check >/dev/null
    # The template's LocalDB files: the site runs on SQL Server.
    rm -f "$dir"/App_Data/*.mdf "$dir"/App_Data/*.ldf
  fi
  project "$dir" "$name" "$framework" "$cms" "$commerce"
  connect "$dir" "$db"
  price_types "$dir"
  mkdir -p "$dir/Models"
  cp "$HERE/CommerceFixture.cs" "$dir/Models/"
  for d in "$db" "$db-ecf"; do
    if [ "${FRESH:-0}" = 1 ] || ! db_exists "$d"; then
      empty_db "$d"
    fi
  done
  first_start "$dir" "$name" "$framework" "$port"
  echo "$db schema version: $(schema "$db"); $db-ecf Commerce schema: $(commerce_schema "$db-ecf")"
  fixture_content "$dir"
  echo "Start it with opticli:  cd $dir && opticli serve"
}

# The fixture's content, through the site with opticli's agents: the plan run again changes nothing.
fixture_content() {
  local dir=$1 opticli=${OPTICLI:-}
  if [ -z "$opticli" ]; then
    if [ ! -f "$REPO/src/OptiCli/bin/Debug/net8.0/opticli.dll" ]; then
      (cd "$REPO" && dotnet build src/OptiCli -nologo -v q -clp:ErrorsOnly)
    fi
    opticli="dotnet $REPO/src/OptiCli/bin/Debug/net8.0/opticli.dll"
  fi
  echo "== content: $HERE/fixture.plan.json"
  (cd "$dir" && $opticli serve --stop >/dev/null 2>&1 || true)
  (cd "$dir" && $opticli serve --build >/dev/null)
  local result status=0
  result=$(cd "$dir" && $opticli apply "$HERE/fixture.plan.json" --update-existing --publish --json) || status=$?
  (cd "$dir" && $opticli serve --stop >/dev/null)
  if [ "$status" != 0 ]; then
    echo "$result" >&2
    exit 1
  fi
  echo "$result" | grep -o '"status":"[a-z]*"' | sort | uniq -c | sed 's/^ */steps: /'
}

if [ "$WHICH" != 13 ]; then build_site 12; fi
if [ "$WHICH" != 12 ]; then build_site 13; fi
