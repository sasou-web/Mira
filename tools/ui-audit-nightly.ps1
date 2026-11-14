param(
    # Everything of the nightly audit: its own clone of Mira (repo), the .NET SDK (dotnet), the reports (reports), its log.
    [string]$Root = 'C:\Apps\mira-qa',
    # No audit while someone uses the PC: no keyboard or mouse for this long, no full-screen app, no presentation.
    [int]$IdleMinutes = 20,
    [int]$KeepNights = 14,
    [switch]$Force,
    # Registers (or replaces) the scheduled task, for the account running this, then stops: nothing audited.
    [switch]$Install,
    [string]$At = '03:30'
)
# The nightly interface audit on the PC of Jellyfin (a scheduled task, « Mira - audit de l'interface », runs it at
# 03:30 through conhost --headless: no window). Updates its own clone of Mira to main, builds Mira for Windows with
# its checks, runs tools/ui-audit.ps1 (French then English) against the Jellyfin this PC serves, compares with the
# night before, keeps 14 nights in $Root\reports\<date>, and writes $Root\reports\latest.json for Vigie. Nothing leaves
# the PC but git fetch from GitHub, NuGet packages from nuget.org (only those not yet in the cache) and the requests
# to Jellyfin; .NET's telemetry is off. Windows PowerShell 5.1.
$ErrorActionPreference = 'Stop'
$reports = Join-Path $Root 'reports'; New-Item -ItemType Directory -Force -Path $reports | Out-Null
$log = Join-Path $Root 'ui-audit.log'
function Say([string]$text) { $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $text; Add-Content -LiteralPath $log -Value $line -Encoding UTF8; Write-Output $line }
function Latest([string]$state, [string]$detail, [string]$folder) {
    $json = [ordered]@{ state = $state; detail = $detail; folder = $folder; at = (Get-Date).ToString('s') } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $reports 'latest.json'), $json, (New-Object Text.UTF8Encoding($false)))
}

if ($Install) {
    # A copy of this script in $Root, run as it is (installing again updates it): what main brings is built and audited,
    # never run as the task itself. conhost --headless: no console window, and not Windows Terminal either (the default
    # terminal of Windows 11 would open one).
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    $script = Join-Path $Root 'ui-audit-nightly.ps1'
    if ($PSCommandPath -ne $script) { Copy-Item -LiteralPath $PSCommandPath -Destination $script -Force }
    $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\conhost.exe" -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$script`" -Root `"$Root`""
    $trigger = New-ScheduledTaskTrigger -Daily -At $At
    # Only with the session open (the window needs the desktop), never started late in the day, two hours at most.
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 2) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName 'Mira - audit de l''interface' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Audit nocturne de l''interface de Mira contre le Jellyfin de ce PC (tools/ui-audit-nightly.ps1). Rapports : C:\Apps\mira-qa\reports.' -Force | Out-Null
    Write-Output "Scheduled every day at $At : $script"
    return
}

# One at a time.
$lock = New-Object Threading.Mutex($false, 'Local\Mira-ui-audit-nightly')
if (-not $lock.WaitOne(0)) { Say 'Already running: nothing done.'; return }
try {
    if (-not $Force) {
        Add-Type -Namespace MiraAudit -Name Desk -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
[DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
[DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
'@
        $info = New-Object MiraAudit.Desk+LASTINPUTINFO; $info.cbSize = 8
        [void][MiraAudit.Desk]::GetLastInputInfo([ref]$info)
        $idle = ([uint32][Environment]::TickCount - $info.dwTime) / 60000
        $state = 0; [void][MiraAudit.Desk]::SHQueryUserNotificationState([ref]$state)
        $owner = 0; [void][MiraAudit.Desk]::GetWindowThreadProcessId([MiraAudit.Desk]::GetForegroundWindow(), [ref]$owner)
        $front = (Get-Process -Id $owner -ErrorAction SilentlyContinue).ProcessName
        # 3 Direct3D full screen, 4 presentation; or a player in front (Mira's own player is a window of Mira). Not 2,
        # « busy »: Vigie's kiosk is a full-screen window all day long.
        if ($idle -lt $IdleMinutes -or $state -in 3, 4 -or $front -in 'Mira', 'vlc', 'mpv', 'mpc-hc64', 'mpc-be64', 'Video.UI', 'Microsoft.Media.Player', 'wmplayer') {
            Say ("Skipped: PC in use (idle {0:0} min, notification state {1}, in front: {2})." -f $idle, $state, $front)
            Latest 'skipped' 'PC en cours d''utilisation : audit remis à la nuit suivante.' ''
            return
        }
    }
    $env:DOTNET_ROOT = Join-Path $Root 'dotnet'
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    if (-not (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) { throw "No .NET SDK in $env:DOTNET_ROOT." }
    $repo = Join-Path $Root 'repo'
    if (-not (Test-Path -LiteralPath (Join-Path $repo '.git'))) { git clone --quiet https://github.com/sasou-web/Mira.git $repo 2>&1 | Out-Null }
    Push-Location $repo
    try {
        # Its own clone: main as on GitHub, whatever was left in it.
        git fetch --quiet origin main 2>&1 | Out-Null
        git reset --quiet --hard origin/main 2>&1 | Out-Null
        git clean -fdq -e .artifacts 2>&1 | Out-Null
        $commit = (git rev-parse --short HEAD).Trim()
        if (-not (Test-Path -LiteralPath (Join-Path $repo 'tools\ui-audit.ps1'))) {
            Say "main at ${commit} has no tools/ui-audit.ps1 yet: nothing audited."
            Latest 'waiting' 'L''audit n''est pas encore dans main.' ''
            return
        }
        Say "main at ${commit}: building."
        $build = & dotnet build src/Mira.Desktop/Mira.Desktop.csproj -c Release 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Build failed: " + (($build | Select-String -Pattern 'error' | Select-Object -First 3) -join ' / ') }
        $night = Join-Path $reports (Get-Date -Format 'yyyy-MM-dd')
        $previous = Get-ChildItem -LiteralPath $reports -Directory | Where-Object { $_.FullName -ne $night -and (Test-Path (Join-Path $_.FullName 'fr\findings.json')) } | Sort-Object Name | Select-Object -Last 1
        $arguments = @{ Output = $night; Language = @('fr', 'en'); Executable = (Join-Path $repo 'src\Mira.Desktop\bin\Release\net10.0-windows10.0.19041.0\Mira.exe') }
        if ($previous) { $arguments.Previous = $previous.FullName }
        Say "Auditing into $night."
        try { & (Join-Path $repo 'tools\ui-audit.ps1') @arguments | ForEach-Object { Say $_ } }
        catch { Say "Audit failed: $_"; Latest 'failed' "$_" $night; throw }
        $fr = Get-Content -LiteralPath (Join-Path $night 'fr\summary.txt') -Encoding UTF8 | Select-Object -First 1
        $compare = Join-Path $night 'fr\compare.md'
        $change = if (Test-Path -LiteralPath $compare) { (Get-Content -LiteralPath $compare -Encoding UTF8 | Where-Object { $_ -like '`**' } | Select-Object -First 1) -replace '\*', '' } else { '' }
        Latest 'ok' ("Mira $commit · $fr" + $(if ($change) { " · $change" } else { '' })) $night
        Say "Done: $fr $change"
    }
    finally { Pop-Location }
    # The oldest nights go.
    Get-ChildItem -LiteralPath $reports -Directory | Sort-Object Name -Descending | Select-Object -Skip $KeepNights | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
}
catch { Say "Failed: $_"; throw }
finally { $lock.ReleaseMutex(); $lock.Dispose() }
