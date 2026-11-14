param(
    [string]$Executable = 'src/Mira.Desktop/bin/Release/net10.0-windows10.0.19041.0/Mira.exe',
    [string]$Output = '.artifacts/ui-audit',
    # The Mira whose session is borrowed (its session.protected and device-id: the same account, the same device for
    # Jellyfin, never a new sign-in). The installed copy by default.
    [string]$SignedIn = (Join-Path $env:LOCALAPPDATA 'Programs\Mira\data'),
    # fr, en or both (« fr,en » also, as -File passes it).
    [string[]]$Language = @('fr', 'en'),
    # A run to compare with (its folder): new and gone defects in compare.md.
    [string]$Previous = '',
    [int]$TimeoutMinutes = 40
)
# The interface audit of Mira for Windows (--ui-audit, src/Mira.Desktop/Checks/MainWindow.UiAudit.cs): Mira signed in
# to the Jellyfin of $SignedIn walks its screens and menus at three sizes and writes, per language, report.md,
# findings.json, captures/ and annotated/ in $Output\<language>. A profile of its own in $Output\profile (removed first,
# never the user's: only the session is copied, DPAPI of this Windows account). The window is never activated and stays
# under every other window; its menus show for an instant. Nothing leaves the PC but the requests to that Jellyfin.
# Windows PowerShell 5.1 and PowerShell 7.
$ErrorActionPreference = 'Stop'
$Language = @($Language | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($lang in $Language) { if ($lang -notin 'fr', 'en') { throw "Language: fr or en, not $lang." } }
# Against PowerShell's folder, not the process's (a scheduled task starts in System32).
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$exe = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Executable)
if (-not (Test-Path -LiteralPath $exe)) { throw "No Mira.exe at $exe (built with the checks: -p:MiraValidation=true, the default)." }
foreach ($name in @('session.protected', 'device-id')) {
    if (-not (Test-Path -LiteralPath (Join-Path $SignedIn $name))) { throw "No $name in $SignedIn : sign in to Mira there first." }
}
# A Mira built here needs the .NET runtime: never the box that asks to install it (it would wait on screen all night).
$env:DOTNET_DISABLE_GUI_ERRORS = '1'
$failed = @()
foreach ($lang in $Language) {
    $data = Join-Path $Output 'profile'
    $out = Join-Path $Output $lang
    foreach ($folder in @($data, $out)) { if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force } }
    New-Item -ItemType Directory -Force -Path $data, $out | Out-Null
    foreach ($name in @('session.protected', 'device-id')) { Copy-Item -LiteralPath (Join-Path $SignedIn $name) -Destination $data }
    $arguments = @('--data', "`"$data`"", '--ui-audit', "`"$out`"", '--language', $lang)
    $run = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    # Windows PowerShell gives the exit code only to a process whose handle was read while it ran.
    $null = $run.Handle
    if (-not $run.WaitForExit($TimeoutMinutes * 60 * 1000)) { $run.Kill(); $failed += "$lang : no end after $TimeoutMinutes min"; continue }
    Remove-Item -LiteralPath $data -Recurse -Force -ErrorAction SilentlyContinue
    $summary = Join-Path $out 'summary.txt'
    if (-not (Test-Path -LiteralPath $summary)) { $failed += "$lang : no report (exit code $($run.ExitCode))"; continue }
    $line = (Get-Content -LiteralPath $summary -Encoding UTF8 | Select-Object -First 1)
    Write-Output "$lang : $line"
    if ($line -notlike 'OK*') { $failed += "$lang : $line" }
    if ($Previous -and (Test-Path -LiteralPath (Join-Path $Previous "$lang\findings.json"))) {
        $before = @(Get-Content -LiteralPath (Join-Path $Previous "$lang\findings.json") -Raw -Encoding UTF8 | ConvertFrom-Json)
        $now = @(Get-Content -LiteralPath (Join-Path $out 'findings.json') -Raw -Encoding UTF8 | ConvertFrom-Json)
        $key = { param($x) "$($x.kind)|$($x.element)|$($x.detail)" }
        $old = @{}; foreach ($x in $before) { $old[(& $key $x)] = $x }
        $new = @{}; foreach ($x in $now) { $new[(& $key $x)] = $x }
        $lines = @("# Depuis l'audit précédent ($lang)", '')
        $added = @($now | Where-Object { $_.severity -ne 'info' -and -not $old.ContainsKey((& $key $_)) })
        $gone = @($before | Where-Object { $_.severity -ne 'info' -and -not $new.ContainsKey((& $key $_)) })
        $lines += "**$($added.Count) nouveau(x), $($gone.Count) disparu(s).**", ''
        if ($added.Count) { $lines += '## Nouveaux', ''; $added | ForEach-Object { $lines += "- [$($_.severity)] $($_.element) : $($_.detail)" + $(if ($_.capture) { " ([voir]($($_.capture)))" } else { '' }) }; $lines += '' }
        if ($gone.Count) { $lines += '## Disparus', ''; $gone | ForEach-Object { $lines += "- [$($_.severity)] $($_.element) : $($_.detail)" } }
        [IO.File]::WriteAllLines((Join-Path $out 'compare.md'), [string[]]$lines, (New-Object Text.UTF8Encoding($false)))
    }
}
if ($failed.Count) { throw ('The interface audit failed: ' + ($failed -join '; ')) }
