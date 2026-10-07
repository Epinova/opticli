# Makes sure SQL Server Express LocalDB is installed and its default instance (MSSQLLocalDB) runs, for LocalDbTests on the
# Windows CI job. GitHub's windows-latest image has LocalDB as a Visual Studio component, which is used when it is there.
# Only when it isn't, Microsoft's own SqlLocalDB.msi (SQL Server 2022 LocalDB, 16.0.1000.6) is downloaded from
# download.microsoft.com, checked against a pinned SHA-256, and installed. Fails the step when anything doesn't match or
# LocalDB still isn't usable.
$ErrorActionPreference = 'Stop'

# The MSI and its SHA-256, as Chocolatey's sqllocaldb 16.0.1000.6 package pins them (its moderation verifies the download).
$msiUrl = 'https://download.microsoft.com/download/3/8/d/38de7036-2433-4207-8eae-06e247e17b25/SqlLocalDB.msi'
$msiSha256 = '224D483992EF60368DAC70CEA174DCFAF43A3CA06ADA331C67DC6119A26490F6'

function Find-SqlLocalDb {
  $onPath = Get-Command SqlLocalDB.exe -ErrorAction SilentlyContinue
  if ($onPath) { return $onPath.Source }
  # Tools\Binn of the newest LocalDB version (the folder name is its major version: 150, 160, ...).
  Get-ChildItem "$env:ProgramFiles\Microsoft SQL Server\*\Tools\Binn\SqlLocalDB.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [int]($_.FullName -replace '^.*Microsoft SQL Server\\(\d+)\\.*$', '$1') } -Descending |
    Select-Object -First 1 -ExpandProperty FullName
}

function Install-SqlLocalDb {
  $msi = Join-Path ([System.IO.Path]::GetTempPath()) 'SqlLocalDB.msi'
  Write-Host "LocalDB not found; downloading $msiUrl"
  Invoke-WebRequest -Uri $msiUrl -OutFile $msi -UseBasicParsing
  $actual = (Get-FileHash -Path $msi -Algorithm SHA256).Hash
  if ($actual -ne $msiSha256) {
    Remove-Item $msi -Force
    throw "SqlLocalDB.msi has SHA-256 $actual, expected ${msiSha256}: not installing it. Microsoft may have replaced the file; check it and update the pinned hash."
  }
  $log = Join-Path ([System.IO.Path]::GetTempPath()) 'SqlLocalDB-install.log'
  $install = Start-Process msiexec.exe -Wait -PassThru -ArgumentList @('/i', "`"$msi`"", '/qn', '/norestart', '/l*v', "`"$log`"", 'IACCEPTSQLLOCALDBLICENSETERMS=YES')
  # 3010 and 1641: installed, a restart is wanted (LocalDB runs without one).
  if ($install.ExitCode -notin 0, 3010, 1641) {
    Get-Content $log -Tail 40 -ErrorAction SilentlyContinue
    throw "msiexec /i SqlLocalDB.msi failed with exit code $($install.ExitCode)."
  }
}

$sqlLocalDb = Find-SqlLocalDb
if (-not $sqlLocalDb) {
  Install-SqlLocalDb
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
