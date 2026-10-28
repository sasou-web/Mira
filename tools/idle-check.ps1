param(
    [string]$Executable = 'src/Mira.Desktop/bin/Release/net10.0-windows10.0.19041.0/Mira.exe',
    [string]$Work = '.artifacts/idle-profile',
    [int]$Seconds = 60
)
# Mira for Windows at rest, as measured on a PC for 0.7.4 (Home, nothing touched, 60 s: 0.05 % of a core, 375 MB
# private, 0 bytes written): the offline demo's Home, left 20 s to settle, then measured for $Seconds s. Processor
# time as a share of one core, private memory, bytes written by the process (files and devices). The numbers go to the
# log, and to the job's summary in CI, to see a drift from one version to the next; none of them fails the run.
# Windows PowerShell 5.1 and PowerShell 7.
$ErrorActionPreference = 'Stop'
$Work = [IO.Path]::GetFullPath($Work)
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Work | Out-Null

$run = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -PassThru -ArgumentList @('--demo', '--data', "`"$Work`"")
function Measure-Mira {
    $process = Get-Process -Id $run.Id
    $wmi = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId = $($run.Id)"
    [pscustomobject]@{
        Cpu = $process.TotalProcessorTime.TotalMilliseconds
        Private = $process.PrivateMemorySize64
        Written = [double]$wmi.WriteTransferCount
        At = [Diagnostics.Stopwatch]::GetTimestamp()
    }
}
try {
    Start-Sleep -Seconds 20
    if ($run.HasExited) { throw "Mira closed by itself (exit code $($run.ExitCode))." }
    $before = Measure-Mira
    Start-Sleep -Seconds $Seconds
    if ($run.HasExited) { throw "Mira closed by itself (exit code $($run.ExitCode))." }
    $after = Measure-Mira
} finally {
    if (-not $run.HasExited) {
        [void]$run.CloseMainWindow()
        if (-not $run.WaitForExit(15000)) { $run.Kill() }
    }
}
$elapsed = ($after.At - $before.At) / [Diagnostics.Stopwatch]::Frequency * 1000
$culture = [Globalization.CultureInfo]::InvariantCulture
$cpu = (($after.Cpu - $before.Cpu) / $elapsed * 100).ToString('0.00', $culture)
$private = ($after.Private / 1MB).ToString('0', $culture)
$written = ($after.Written - $before.Written).ToString('0', $culture)
$line = "Mira at rest on its Home, $([math]::Round($elapsed / 1000)) s: $cpu % of a core, $private MB private, $written bytes written"
Write-Output $line
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "**$line** (offline demo, runner $env:RUNNER_OS)" }
