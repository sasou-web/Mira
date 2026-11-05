param(
    [string]$Executable = 'src/Mira.Desktop/bin/Release/net10.0-windows10.0.19041.0/Mira.exe',
    [string]$Output = '.artifacts/smooth',
    [ValidateSet('edge', 'offscreen')][string]$Placement = 'edge',
    [int]$TimeoutSeconds = 300
)
# How smooth Mira for Windows' pages are (--smooth-check, src/Mira.Desktop/Checks/MainWindow.SmoothCheck.cs): Mira
# signed in to a Jellyfin answered inside it, the wheel down and up the pages, the rail's pages, a title's page and
# Settings, frame by frame. A profile of its own in $Output\profile (removed first, never the user's), the report
# (report.txt, report.json, summary.md) and a few captures in $Output. The window never comes forward: "edge" leaves
# its first pixel on the screen's last one, under every other window; "offscreen" puts it wholly off screen. Numbers
# only: nothing fails on them, only a check that could not run. Windows PowerShell 5.1 and PowerShell 7.
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
$data = Join-Path $Output 'profile'
if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
New-Item -ItemType Directory -Force -Path $data | Out-Null
foreach ($name in @('report.txt', 'report.json', 'summary.md')) { Remove-Item -LiteralPath (Join-Path $Output $name) -ErrorAction SilentlyContinue }

$arguments = @('--data', "`"$data`"", '--smooth-check', "`"$Output`"", '--smooth-placement', $Placement)
$run = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList $arguments -PassThru
# Windows PowerShell gives the exit code only to a process whose handle was read while it ran.
$null = $run.Handle
if (-not $run.WaitForExit($TimeoutSeconds * 1000)) { $run.Kill(); throw "The smooth check did not finish within $TimeoutSeconds s." }
$report = Join-Path $Output 'report.txt'
if (-not (Test-Path -LiteralPath $report)) { throw "The smooth check wrote no report (exit code $($run.ExitCode))." }
Get-Content -LiteralPath $report -Encoding UTF8
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8 -Value "### Smoothness ($Placement)"
    Get-Content -LiteralPath (Join-Path $Output 'summary.md') -Encoding UTF8 | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8
}
if ($run.ExitCode -ne 0) { throw "The smooth check failed (exit code $($run.ExitCode))." }
