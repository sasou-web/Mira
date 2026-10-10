param([string]$Configuration = 'Release', [string]$Executable = '')
# End-to-end TorLink check: real TorLink in the Mira page, synthetic finished downloads, isolated library.
# Nothing touches the real TorLink state (TORLINK_STATE_DIR) or Jellyfin (demo mode).
# -Executable checks another build of Mira, for example the published dist\Mira\Mira.exe.
$ErrorActionPreference = 'Stop'
# The .NET SDK sends usage data unless told not to; the scripts tell it, on this PC as in the CI.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$workspace = Split-Path -Parent $PSScriptRoot
$output = Join-Path $workspace '.artifacts\torlink-check'
$exe = if ($Executable) { [IO.Path]::GetFullPath($Executable) } else { Join-Path $workspace "src\Mira.Desktop\bin\$Configuration\net8.0-windows10.0.19041.0\Mira.exe" }
if (-not (Test-Path -LiteralPath $exe)) { throw "Build Mira first: dotnet build Mira.sln -c $Configuration" }
if (Test-Path -LiteralPath $output) {
    # A browser process of the previous run may still be closing its profile, or a scanner may hold a file briefly.
    for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath $output); $i++) {
        try { Remove-Item -LiteralPath $output -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 }
    }
    if (Test-Path -LiteralPath $output) { throw "The previous check folder is still in use: $output" }
}
New-Item -ItemType Directory -Path $output | Out-Null
$previous = $env:TORLINK_STATE_DIR
$env:TORLINK_STATE_DIR = Join-Path $output 'state'
try {
    # One quoted string: Windows PowerShell does not quote array arguments containing spaces.
    $arguments = '--data "' + (Join-Path $output 'profile') + '" --demo --torlink-check "' + $output + '"'
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(240000)) { $process.Kill(); throw 'The TorLink check did not finish in time.' }
} finally { $env:TORLINK_STATE_DIR = $previous }
$result = Join-Path $output 'result.txt'
if (-not (Test-Path -LiteralPath $result)) { throw 'result.txt was not written.' }
# WebView2 ends with Mira: no browser process may keep the check profile open once Mira has closed.
$profileFolder = Join-Path $output 'profile'
$deadline = (Get-Date).AddSeconds(10)
do {
    $left = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($profileFolder, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
    if ($left.Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)
$line = if ($left.Count -eq 0) { 'PASS no WebView2 process outlives Mira' } else { "FAIL $($left.Count) WebView2 process(es) still use the profile 10 s after Mira closed" }
Add-Content -LiteralPath $result -Value $line -Encoding UTF8
Get-Content -LiteralPath $result -Encoding UTF8
if (Select-String -LiteralPath $result -Pattern '^FAIL' -Quiet) { exit 1 }
