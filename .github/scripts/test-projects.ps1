# Runs every test project under tests/ on its own, after a Release build: from the solution, they would share one .trx
# name and overwrite it. report-test-failures.ps1 reads the results.
$failed = $false
foreach ($project in Get-ChildItem tests -Recurse -Filter *.csproj) {
  dotnet test $project.FullName -c Release --no-build --logger trx --results-directory "TestResults/$($project.BaseName)"
  if ($LASTEXITCODE -ne 0) { $failed = $true }
}
if ($failed) { exit 1 }
