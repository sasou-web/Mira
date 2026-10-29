param(
    [string]$Executable = 'src/Mira.Desktop/bin/Release/net10.0-windows10.0.19041.0/Mira.exe',
    [Parameter(Mandatory = $true)][string]$Jellyfin,
    [string]$Media = '.artifacts/media',
    [string]$Output = '.artifacts/server',
    [string]$Work = '.artifacts/server-work',
    [string]$User = 'mira',
    [string]$Password = 'mira-check'
)
# Mira for Windows against a real Jellyfin (Mira.exe --server-check, see docs/TESTS.md), run by ci.yml on the Windows
# runner: $Jellyfin is a published Jellyfin (the folder of jellyfin.dll, run by dotnet), $Media the test media of
# tools/web/media.sh, to which a 4-minute film at 4 Mb/s is added here (« Coupure Test »: converted under 1.5 Mb/s, long
# enough to be cut halfway). A fresh Jellyfin prepared by tools/web/setup.mjs, libmpv as Mira installs it
# (tools/mpv-engine.ps1), then Mira signed in at this PC's network address; the check stops and starts Jellyfin by the
# two scripts written here. ffmpeg, Node.js and 7-Zip on the PATH. Windows PowerShell 5.1 and PowerShell 7.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Work = [IO.Path]::GetFullPath($Work); $Output = [IO.Path]::GetFullPath($Output); $Media = [IO.Path]::GetFullPath($Media); $Jellyfin = [IO.Path]::GetFullPath($Jellyfin)
New-Item -ItemType Directory -Force -Path $Work, $Output | Out-Null

$profilePath = Join-Path $Work 'profile'
if (Test-Path -LiteralPath $profilePath) { Remove-Item -LiteralPath $profilePath -Recurse -Force }
& (Join-Path $PSScriptRoot 'mpv-engine.ps1') -Work $Work -ProfileFolder $profilePath

$ffmpeg = (Get-Command ffmpeg).Source
$cut = Join-Path $Media 'Films/Coupure Test (2025)'
if (-not (Test-Path -LiteralPath $cut)) {
    New-Item -ItemType Directory -Force -Path $cut | Out-Null
    & $ffmpeg -loglevel error -y -f lavfi -i 'testsrc2=size=1280x720:rate=24:duration=240' -f lavfi -i 'sine=frequency=500:sample_rate=48000:duration=240' `
        -c:v libx264 -preset ultrafast -pix_fmt yuv420p -b:v 4M -minrate 4M -maxrate 4M -bufsize 2M -x264-params nal-hrd=cbr -c:a aac -b:a 128k (Join-Path $cut 'Coupure Test (2025).mkv')
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg could not make « Coupure Test » ($LASTEXITCODE)" }
}

# Jellyfin, started and stopped by two scripts that the check runs too (MIRA_START, MIRA_STOP). Stopped as a PC that
# goes off: the process ends at once, its conversions with it.
$data = Join-Path $Work 'jellyfin'
if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
foreach ($folder in 'data', 'cache', 'config', 'log') { New-Item -ItemType Directory -Force -Path (Join-Path $data $folder) | Out-Null }
$dotnet = (Get-Command dotnet).Source
$processFile = Join-Path $Work 'jellyfin.pid'
$arguments = @((Join-Path $Jellyfin 'jellyfin.dll'), '--datadir', (Join-Path $data 'data'), '--cachedir', (Join-Path $data 'cache'), '--configdir', (Join-Path $data 'config'),
    '--logdir', (Join-Path $data 'log'), '--nowebclient', '--ffmpeg', $ffmpeg) | ForEach-Object { "'" + ('"' + $_ + '"').Replace("'", "''") + "'" }
$start = Join-Path $Work 'start-jellyfin.ps1'; $stop = Join-Path $Work 'stop-jellyfin.ps1'
Set-Content -LiteralPath $start -Encoding UTF8 -Value @(
    "`$ErrorActionPreference = 'Stop'",
    "`$process = Start-Process -FilePath '$($dotnet.Replace("'", "''"))' -ArgumentList @($($arguments -join ', ')) -WindowStyle Hidden -PassThru",
    "Set-Content -LiteralPath '$($processFile.Replace("'", "''"))' -Value `$process.Id")
Set-Content -LiteralPath $stop -Encoding UTF8 -Value @(
    "`$id = [int](Get-Content -LiteralPath '$($processFile.Replace("'", "''"))')",
    "Stop-Process -Id `$id -Force -ErrorAction SilentlyContinue",
    "Get-Process -Name ffmpeg -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue",
    "Wait-Process -Id `$id -Timeout 30 -ErrorAction SilentlyContinue")
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$env:MIRA_START = "`"$powershell`" -NoProfile -ExecutionPolicy Bypass -File `"$start`""
$env:MIRA_STOP = "`"$powershell`" -NoProfile -ExecutionPolicy Bypass -File `"$stop`""
& $powershell -NoProfile -ExecutionPolicy Bypass -File $start
if ($LASTEXITCODE -ne 0) { throw "Jellyfin did not start ($LASTEXITCODE)" }

try {
    & node (Join-Path $PSScriptRoot 'web/setup.mjs') 'http://127.0.0.1:8096' $Media $User $Password
    if ($LASTEXITCODE -ne 0) { throw 'Jellyfin setup failed.' }

    # This PC's address on its network: Mira streams from Jellyfin there, as from another PC (at 127.0.0.1 it reads the file).
    $address = [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object { $_.OperationalStatus -eq 'Up' } |
        ForEach-Object { $_.GetIPProperties().UnicastAddresses } | ForEach-Object { $_.Address } |
        Where-Object { $_.AddressFamily -eq 'InterNetwork' -and -not [Net.IPAddress]::IsLoopback($_) -and -not $_.ToString().StartsWith('169.254.') } | Select-Object -First 1
    if (-not $address) { throw 'No network address on this PC.' }
    $server = "http://$($address):8096"
    $ping = Invoke-WebRequest -UseBasicParsing -Uri "$server/System/Ping" -TimeoutSec 10
    Write-Output "Jellyfin at $server ($($ping.StatusCode))"

    $result = Join-Path $Output 'result.txt'
    if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result -Force }
    $env:MIRA_CHECK_SERVER = $server; $env:MIRA_CHECK_USER = $User; $env:MIRA_CHECK_PASSWORD = $Password
    $run = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -PassThru -ArgumentList @('--data', "`"$profilePath`"", '--server-check', "`"$Output`"")
    if (-not $run.WaitForExit(900000)) { $run.Kill(); throw 'The server check did not finish within 15 minutes.' }
    if (-not (Test-Path -LiteralPath $result)) { throw "The server check wrote no result (exit code $($run.ExitCode))." }
    $lines = Get-Content -LiteralPath $result
    $lines | Write-Output
    $failed = @($lines | Where-Object { $_ -like 'FAIL*' }).Count
    $passed = @($lines | Where-Object { $_ -like 'PASS*' }).Count
    if ($failed -gt 0 -or $passed -eq 0) {
        Get-ChildItem -LiteralPath (Join-Path $data 'log') -Filter '*.log' | ForEach-Object { Write-Output "--- $($_.Name)"; Get-Content -LiteralPath $_.FullName -Tail 80 }
        throw "Server check: $passed passed, $failed failed."
    }
    Write-Output "Server check: $passed passed."
}
finally { & $powershell -NoProfile -ExecutionPolicy Bypass -File $stop }
