param(
    [string]$Executable = 'src/Mira.Desktop/bin/Release/net10.0-windows10.0.19041.0/Mira.exe',
    [Parameter(Mandatory = $true)][string]$Jellyfin,
    [string]$Media = '.artifacts/media',
    [string]$Output = '.artifacts/server',
    [string]$Work = '.artifacts/server-work',
    [string]$User = 'mira',
    [string]$Password = 'mira-check',
    [string]$Testnet
)
# Mira for Windows against a real Jellyfin (Mira.exe --server-check, see docs/TESTS.md), run by ci.yml on the Windows
# runner: $Jellyfin is a published Jellyfin (the folder of jellyfin.dll, run by dotnet), $Media the test media of
# tools/web/media.sh, to which a 4-minute film at 4 Mb/s is added here (« Coupure Test »: converted under 1.5 Mb/s, long
# enough to be cut halfway). A fresh Jellyfin prepared by tools/web/setup.mjs, libmpv as Mira installs it
# (tools/mpv-engine.ps1), then Mira signed in at this PC's network address; the check stops and starts Jellyfin by the
# two scripts written here. With -Testnet (mira-testnet.exe of src/Mira.Tailscale), then « Tailscale intégré »
# (Mira.exe --tailnet-check, run twice on a profile of its own): Mira's own node on a Tailscale network of this PC only,
# whose « Jellyfin PC » exposes the same Jellyfin. ffmpeg, Node.js and 7-Zip on the PATH. Windows PowerShell 5.1 and
# PowerShell 7.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Work = [IO.Path]::GetFullPath($Work); $Output = [IO.Path]::GetFullPath($Output); $Media = [IO.Path]::GetFullPath($Media); $Jellyfin = [IO.Path]::GetFullPath($Jellyfin)
New-Item -ItemType Directory -Force -Path $Work, $Output | Out-Null

$profilePath = Join-Path $Work 'profile'
if (Test-Path -LiteralPath $profilePath) { Remove-Item -LiteralPath $profilePath -Recurse -Force }
& (Join-Path $PSScriptRoot 'mpv-engine.ps1') -Work $Work -ProfileFolder $profilePath

# The real ffmpeg, not Chocolatey's shim: Jellyfin looks for ffprobe beside it.
$ffmpeg = (Get-Command ffmpeg).Source
if ($ffmpeg -like '*\chocolatey\bin\*') { $ffmpeg = (Get-ChildItem -Path (Join-Path (Split-Path (Split-Path $ffmpeg)) 'lib\ffmpeg') -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1).FullName }
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $ffmpeg) 'ffprobe.exe'))) { throw "No ffprobe beside $ffmpeg" }
Write-Output "ffmpeg: $ffmpeg"
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

# One run of Mira.exe on a check: its lines PASS, INFO and FAIL in the log, a failure when one failed or none passed.
function Invoke-MiraCheck([string]$Name, [string[]]$Arguments, [string]$Result, [int]$Minutes) {
    if (Test-Path -LiteralPath $Result) { Remove-Item -LiteralPath $Result -Force }
    $run = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -PassThru -ArgumentList $Arguments
    if (-not $run.WaitForExit($Minutes * 60000)) { $run.Kill(); throw "The $Name did not finish within $Minutes minutes." }
    if (-not (Test-Path -LiteralPath $Result)) { throw "The $Name wrote no result (exit code $($run.ExitCode))." }
    $lines = Get-Content -LiteralPath $Result
    $lines | Write-Output
    $failed = @($lines | Where-Object { $_ -like 'FAIL*' }).Count
    $passed = @($lines | Where-Object { $_ -like 'PASS*' }).Count
    if ($failed -gt 0 -or $passed -eq 0) { throw "The $Name`: $passed passed, $failed failed." }
    Write-Output "The $Name`: $passed passed."
}

$testnetProcess = $null
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

    $env:MIRA_CHECK_SERVER = $server; $env:MIRA_CHECK_USER = $User; $env:MIRA_CHECK_PASSWORD = $Password
    Invoke-MiraCheck 'server check' @('--data', "`"$profilePath`"", '--server-check', "`"$Output`"") (Join-Path $Output 'result.txt') 15

    if ($Testnet) {
        # The test tailnet: its coordination server, relay and STUN on 127.0.0.1:18090, its « Jellyfin PC » exposing this
        # Jellyfin on port 8096 of its Tailscale addresses; ready once it has written its description.
        $tailnetProfile = Join-Path $Work 'tailnet-profile'; $tailnetOutput = Join-Path $Output 'tailnet'; $info = Join-Path $Work 'testnet.json'
        foreach ($path in $tailnetProfile, $info) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }
        New-Item -ItemType Directory -Force -Path $tailnetOutput | Out-Null
        & (Join-Path $PSScriptRoot 'mpv-engine.ps1') -Work $Work -ProfileFolder $tailnetProfile
        $testnetProcess = Start-Process -FilePath ([IO.Path]::GetFullPath($Testnet)) -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $Work 'testnet-out.log') -RedirectStandardError (Join-Path $Work 'testnet.log') `
            -ArgumentList @('-addr', '127.0.0.1:18090', '-forward', '8096=127.0.0.1:8096', '-info', "`"$info`"")
        for ($i = 0; -not (Test-Path -LiteralPath $info); $i++) {
            if ($testnetProcess.HasExited -or $i -ge 240) { throw 'mira-testnet did not start.' }
            Start-Sleep -Milliseconds 250
        }
        Write-Output "Test tailnet: $((Get-Content -LiteralPath $info -Raw).Trim())"
        # Signed in on a fresh profile, then Mira started again on it, then signed out.
        $arguments = @('--data', "`"$tailnetProfile`"", '--tailnet-check', "`"$tailnetOutput`"", '--tailnet-control', 'http://127.0.0.1:18090')
        Invoke-MiraCheck 'tailnet check' $arguments (Join-Path $tailnetOutput 'result.txt') 10
        Invoke-MiraCheck 'tailnet check, Mira started again' $arguments (Join-Path $tailnetOutput 'result-again.txt') 6
    }
}
catch {
    Get-ChildItem -LiteralPath (Join-Path $data 'log') -Filter '*.log' | ForEach-Object { Write-Output "--- $($_.Name)"; Get-Content -LiteralPath $_.FullName -Tail 80 }
    if ($testnetProcess) {
        # Mira's node (its log on this PC hides login pages), then the test tailnet.
        $node = Join-Path $Work 'tailnet-profile/tailscale/mira-tailscale.log'
        foreach ($log in $node, (Join-Path $Work 'testnet.log')) { if (Test-Path -LiteralPath $log) { Write-Output "--- $(Split-Path -Leaf $log)"; Get-Content -LiteralPath $log -Tail 80 } }
    }
    throw
}
finally {
    if ($testnetProcess -and -not $testnetProcess.HasExited) { Stop-Process -Id $testnetProcess.Id -Force -ErrorAction SilentlyContinue }
    & $powershell -NoProfile -ExecutionPolicy Bypass -File $stop
}
