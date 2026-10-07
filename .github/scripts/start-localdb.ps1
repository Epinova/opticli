# Makes sure SQL Server Express LocalDB is installed and its default instance (MSSQLLocalDB) runs, for LocalDbTests on the
# Windows CI job. GitHub's windows-latest image has LocalDB as a Visual Studio component; when it isn't there, it is
# installed with Chocolatey (which the image has). Fails the step when LocalDB still isn't usable.
$ErrorActionPreference = 'Stop'

function Find-SqlLocalDb {
  $onPath = Get-Command SqlLocalDB.exe -ErrorAction SilentlyContinue
  if ($onPath) { return $onPath.Source }
  # Tools\Binn of the newest LocalDB version (the folder name is its major version: 150, 160, ...).
  Get-ChildItem "$env:ProgramFiles\Microsoft SQL Server\*\Tools\Binn\SqlLocalDB.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.FullName -replace '^.*Microsoft SQL Server\\(\d+)\\.*$', '$1') } -Descending |
    Select-Object -First 1 -ExpandProperty FullName
}

$sqlLocalDb = Find-SqlLocalDb
if (-not $sqlLocalDb) {
  Write-Host 'LocalDB not found; installing it with Chocolatey.'
  choco install sqllocaldb -y --no-progress
  if ($LASTEXITCODE -ne 0) { throw "choco install sqllocaldb failed ($LASTEXITCODE)." }
  $sqlLocalDb = Find-SqlLocalDb
  if (-not $sqlLocalDb) { throw 'SqlLocalDB.exe still not found after installing LocalDB.' }
}
Write-Host "Using $sqlLocalDb"
& $sqlLocalDb versions

# start creates the automatic MSSQLLocalDB instance when it doesn't exist yet.
& $sqlLocalDb start MSSQLLocalDB
if ($LASTEXITCODE -ne 0) { throw "SqlLocalDB start MSSQLLocalDB failed ($LASTEXITCODE)." }
& $sqlLocalDb info MSSQLLocalDB

$installed = Get-Item 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions' -ErrorAction SilentlyContinue
if (-not $installed -or $installed.SubKeyCount -eq 0) {
  throw "LocalDB runs, but its 'Installed Versions' registry key, which LocalDbTests looks for, is missing."
}
