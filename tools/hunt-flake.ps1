# Hunting an unidentified flake.
#
# The rule this script exists to keep: a failing run must be kept, with the name of the
# test that failed, whatever else happens. The sighting this is chasing was lost because
# the output scrolled and only the project's summary was read.
#
# Every run writes its full output to runs\run-NNN.log, pass or fail. A failing run is
# also copied to FAILED-run-NNN.log and the loop stops, so the artifact survives whatever
# is done next.

param(
    [int]$Runs = 20,
    [string]$Filter = '',
    [switch]$Solution
)

$ErrorActionPreference = 'Continue'
$repo = 'C:\GitProjects\DEEP_SCADA_DARBOX'
# Not `$runs`: that name is the parameter above, and assigning a path to it is an error that
# killed this script on its first outing -- after it had printed the failing test's name and
# before it had copied the log that explains it. The name it printed was the one thing it got
# right, and it got that right because the summary is printed before the copy.
$logDirectory = Join-Path $repo 'runs'

if (Test-Path $logDirectory) { Remove-Item $logDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $logDirectory | Out-Null

$env:SCADA_TEST_DB_PORT = '5433'
$env:SCADA_TEST_DB_HOST = 'localhost'

# The broker container the broker tests start leaks its own name sometimes; make sure the
# loop is not fighting yesterday's leftovers.
docker rm -f scada-broker-acl-probe 2>&1 | Out-Null

$target = if ($Solution) { 'ScadaDarbox.slnx' } else { 'tests\Gateway.Tests\ScadaDarbox.Gateway.Tests.csproj' }

$failures = 0
$summary = @()

for ($i = 1; $i -le $Runs; $i++) {
    $name = 'run-{0:D3}' -f $i
    $log = Join-Path $logDirectory "$name.log"

    $started = Get-Date
    if ($Filter) {
        dotnet test $target --no-build --nologo --filter $Filter *>&1 | Out-File -FilePath $log -Encoding utf8
    } else {
        dotnet test $target --no-build --nologo *>&1 | Out-File -FilePath $log -Encoding utf8
    }
    $elapsed = ((Get-Date) - $started).TotalSeconds

    $text = Get-Content $log -Raw

    # Every way a run can be red, and the name beside each: VSTest's own line, and the
    # cleanup-failure form that the fixture guard note in CLAUDE.md describes.
    $failedTests = @()
    $failedTests += [regex]::Matches($text, '(?m)^\s*Failed\s+(ScadaDarbox\.[^\s\[]+)\s*(\[[^\]]*\])?') |
        ForEach-Object { "$($_.Groups[1].Value) $($_.Groups[2].Value)" }
    $failedTests += [regex]::Matches($text, '\[xUnit\.net [^\]]+\]\s+(ScadaDarbox\.[^\s]+) \[FAIL\]') |
        ForEach-Object { $_.Groups[1].Value }
    $failedTests = $failedTests | Sort-Object -Unique

    $totals = [regex]::Matches($text, 'Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+)')
    $totalFailed = ($totals | ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    $totalPassed = ($totals | ForEach-Object { [int]$_.Groups[2].Value } | Measure-Object -Sum).Sum

    $red = ($failedTests.Count -gt 0) -or ($totalFailed -gt 0)
    $mark = if ($red) { 'RED  ' } else { 'green' }

    $line = "$name  $mark  passed=$totalPassed failed=$totalFailed  $([int]$elapsed)s"
    Write-Output $line
    $summary += $line

    if ($red) {
        $failures++
        Copy-Item $log (Join-Path $logDirectory "FAILED-$name.log") -Force
        Write-Output ''
        Write-Output '  ---- FAILED TESTS ----'
        if ($failedTests.Count -eq 0) {
            Write-Output '  (no test name matched; the whole log is kept)'
        } else {
            $failedTests | ForEach-Object { Write-Output "  $_" }
        }
        Write-Output '  ----------------------'

        # Kept, and the loop stops here: a flake that is chased past its own evidence is a
        # flake that gets re-explained rather than understood.
        break
    }
}

Write-Output ''
Write-Output "runs: $i  failures: $failures"
Write-Output "logs: $logDirectory"
