# Turns the .trx files test-projects.ps1 wrote into annotations, which show on the run's summary page without
# signing in (the log needs it). GitHub keeps 10 errors a step.
$files = @(Get-ChildItem TestResults -Recurse -Filter *.trx -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) { Write-Output "::error title=No test results::dotnet test wrote no .trx file" }
foreach ($file in $files) {
  [xml]$trx = Get-Content $file.FullName
  $summary = $trx.TestRun.ResultSummary
  $project = $file.Directory.Name
  Write-Output "::notice title=$project::$($summary.outcome): $($summary.Counters.passed) passed, $($summary.Counters.failed) failed of $($summary.Counters.total)"
  foreach ($info in @($summary.RunInfos.RunInfo | Where-Object { $_ -and $_.outcome -eq 'Error' -and $_.Text -notmatch '\[SKIP\]' })) {
    Write-Output "::error title=$project run::$(($info.Text -replace "`r?`n", ' | '))"
  }
  foreach ($result in @($trx.TestRun.Results.UnitTestResult | Where-Object outcome -eq 'Failed')) {
    $info = $result.Output.ErrorInfo
    $trace = ($info.StackTrace -split "`n" | Select-Object -First 4) -join ' | '
    Write-Output "::error title=$($result.testName)::$("$($info.Message) | $trace" -replace "`r?`n", ' ')"
  }
}
