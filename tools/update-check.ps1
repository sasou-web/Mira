param([switch]$SkipBuild, [switch]$SkipInstaller, [int]$Port = 18097)
# End-to-end check of Mira's automatic updates on this PC, without GitHub and without the real signing key:
#   1. packages of this source ("old") and of the same source numbered one patch higher ("new"); the new
#      mira-update.json is signed with a throwaway test key, and a loopback server stands in for GitHub's releases;
#   2. zip folder: the update is found, downloaded, checked and installed when Mira closes; the data folder is kept;
#      the new version then clears its download and finds itself up to date;
#   3. portable exe: same through the notice's restart button, Mira reopening in the new version;
#   4. installer (unless -SkipInstaller): silent install of "old" in a test folder, update when Mira closes, silent uninstall;
#   5. installer again, through the restart button: Setup shows its progress, then reopens the installed Mira.
# Every Mira runs offscreen with an isolated --data profile. The installer rewrites the Start menu shortcut and the Shell
# identity: both are given back to the copy of Mira they named before. Results: .artifacts/update-check/result.txt
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path $workspace '.artifacts\update-check'
New-Item -ItemType Directory -Path $root -Force | Out-Null
[xml]$projectXml = Get-Content -LiteralPath (Join-Path $workspace 'src\Mira.Desktop\Mira.Desktop.csproj')
$old = [string]$projectXml.Project.PropertyGroup.Version
$parts = $old.Split('.'); $new = '{0}.{1}.{2}' -f $parts[0], $parts[1], ([int]$parts[2] + 1)
$oldPackages = Join-Path $root 'old'; $newPackages = Join-Path $root 'new'
$testKey = Join-Path $root 'test-signing.key'; $publicFile = Join-Path $root 'test-signing.pub'
$restartLabel = 'Red' + [char]0xE9 + 'marrer'
$results = New-Object System.Collections.Generic.List[string]
function Pass([string]$text) { $results.Add("PASS $text"); Write-Host "PASS $text" }
function Fail([string]$text) { $results.Add("FAIL $text"); throw $text }
function Wait-For([scriptblock]$condition, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }
    return [bool](& $condition)
}
function Log-Has([string]$data, [string]$pattern) {
    $log = Join-Path $data 'updates\update.log'
    return (Test-Path -LiteralPath $log) -and ((Get-Content -LiteralPath $log -Encoding UTF8 -Raw) -match $pattern)
}
function Version-Of([string]$exe) { if (Test-Path -LiteralPath $exe) { [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion } }
function Start-Mira([string]$exe, [string]$data) {
    $arguments = "--data `"$data`" --demo --offscreen --update-feed http://127.0.0.1:$Port/releases --update-key $public"
    Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $exe) -PassThru
}
function Close-Mira($process) {
    $process.Refresh()
    if (-not $process.HasExited) { Wait-For { $process.Refresh(); $process.MainWindowHandle -ne [IntPtr]::Zero } 20 | Out-Null; $null = $process.CloseMainWindow() }
    if (-not $process.WaitForExit(40000)) { $process.Kill(); Fail 'Mira did not close' }
}
function Running([string]$exe) { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $exe } catch { $false } }) }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
function Invoke-Button([int]$processId, [string]$name, [int]$seconds) {
    $automation = [System.Windows.Automation.AutomationElement]
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $windows = $automation::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($automation::ProcessIdProperty, $processId)))
        foreach ($window in $windows) {
            $condition = New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition($automation::NameProperty, $name)),
                (New-Object System.Windows.Automation.PropertyCondition($automation::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
            $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($button) { $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true }
        }
        Start-Sleep -Milliseconds 200
    }
    return $false
}
function Shortcut-Target {
    $link = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Mira.lnk'
    if (Test-Path -LiteralPath $link) { (New-Object -ComObject WScript.Shell).CreateShortcut($link).TargetPath }
}
# The log line of a verified download names its file: each step checks that it went through its own kind of update.
function Ready-Line([string]$file) { "pr.te : $([regex]::Escape($new)) \($([regex]::Escape($file))\)" }
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7D5E2C41-9B8A-4F63-A1E7-3C5B9D2F8A64}_is1'
$installed = Join-Path $root 'installed'
function Test-Install { (Test-Path $uninstallKey) -and ([string](Get-ItemProperty $uninstallKey).InstallLocation).TrimEnd('\') -eq $installed }
# Inno Setup's uninstaller ends from a copy in the temporary folder and deletes unins000.exe last: an installation made in the
# same folder before that would lose its own uninstaller (Mira would then update it as a plain folder).
function Uninstall-Test {
    Start-Process -FilePath (Join-Path $installed 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' | Out-Null
    return (Wait-For { -not (Test-Path -LiteralPath (Join-Path $installed 'Mira.exe')) -and -not (Test-Path $uninstallKey) -and -not (Test-Path -LiteralPath (Join-Path $installed 'unins000.exe')) -and -not (Test-Path -LiteralPath (Join-Path $installed 'unins000.dat')) -and @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like '_iu*' }).Count -eq 0 } 90)
}

$server = $null; $shortcutBefore = Shortcut-Target
try {
    if (-not $SkipBuild) {
        foreach ($folder in @($oldPackages, $newPackages)) { if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force } }
        Remove-Item -LiteralPath $testKey, $publicFile -ErrorAction SilentlyContinue
        dotnet build (Join-Path $workspace 'Mira.sln') -c Release -nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
        $tool = Join-Path $workspace 'tools\Mira.Release\bin\Release\net8.0-windows\Mira.Release.exe'
        $key = & $tool keygen --key $testKey
        if ($LASTEXITCODE -ne 0) { throw 'Test key creation failed.' }
        Set-Content -LiteralPath $publicFile -Value $key -Encoding ascii
        & (Join-Path $PSScriptRoot 'package.ps1') -Output $oldPackages -SigningKey (Join-Path $root 'no-signing-key') | Out-Host
        & (Join-Path $PSScriptRoot 'package.ps1') -Version $new -Output $newPackages -SigningKey $testKey | Out-Host
    }
    $public = (Get-Content -LiteralPath $publicFile -Raw).Trim()
    foreach ($name in @('mira-update.json', 'mira-update.json.sig', "Mira-$new-win-x64.zip", "Mira-$new-win-x64-portable.exe")) {
        if (-not (Test-Path -LiteralPath (Join-Path $newPackages $name))) { Fail "new package $name present" }
    }
    Pass "packages $old (old) and $new (new, manifest signed with a test key)"
    $serverExe = Join-Path $workspace 'tests\Mira.Tests\bin\Release\net8.0-windows10.0.19041.0\Mira.Tests.exe'
    $server = Start-Process -FilePath $serverExe -ArgumentList "--update-server `"$newPackages`" $Port $new" -WorkingDirectory $workspace -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 2

    # 2. Zip folder, installed when Mira closes.
    $folder = Join-Path $root 'folder'
    if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    Expand-Archive -LiteralPath (Join-Path $oldPackages "Mira-$old-win-x64.zip") -DestinationPath $folder
    $exe = Join-Path $folder 'Mira\Mira.exe'; $data = Join-Path $folder 'Mira\data'
    New-Item -ItemType Directory -Path $data -Force | Out-Null; Set-Content -LiteralPath (Join-Path $data 'marker.txt') -Value 'donnees de test' -Encoding ascii
    if ((Version-Of $exe) -ne "$old.0") { Fail "folder copy starts in $old" }
    $mira = Start-Mira $exe $data
    if (-not (Wait-For { Log-Has $data (Ready-Line "Mira-$new-win-x64.zip") } 120)) { Fail "folder: $new zip found, downloaded and verified" }
    Pass "folder: $new zip found on the feed, downloaded and verified (signature, size, SHA-256), then unpacked"
    Close-Mira $mira
    if (-not (Wait-For { (Version-Of $exe) -eq "$new.0" -and (Log-Has $data 'install.e depuis') } 90)) { Fail 'folder: new files in place after closing' }
    Pass "folder: installed when Mira closed, Mira.exe now $(Version-Of $exe)"
    if ((Get-Content -LiteralPath (Join-Path $data 'marker.txt') -Raw).Trim() -ne 'donnees de test') { Fail 'folder: data kept' }
    Pass 'folder: data folder untouched'
    $mira = Start-Mira $exe $data
    if (-not (Wait-For { (Log-Has $data "jour \($([regex]::Escape($new))\)") -and -not (Test-Path -LiteralPath (Join-Path $data "updates\$new")) } 60)) { Fail 'folder: new version up to date, download cleared' }
    Pass 'folder: the new version finds itself up to date and clears its download'
    Close-Mira $mira

    # 3. Portable executable, through the notice's restart button.
    $portable = Join-Path $root 'portable'
    if (Test-Path -LiteralPath $portable) { Remove-Item -LiteralPath $portable -Recurse -Force }
    New-Item -ItemType Directory -Path $portable | Out-Null
    $exe = Join-Path $portable "Mira-$old-win-x64-portable.exe"; $data = Join-Path $portable 'data'
    Copy-Item -LiteralPath (Join-Path $oldPackages "Mira-$old-win-x64-portable.exe") -Destination $exe
    New-Item -ItemType Directory -Path $data -Force | Out-Null; Set-Content -LiteralPath (Join-Path $data 'marker.txt') -Value 'donnees de test' -Encoding ascii
    $mira = Start-Mira $exe $data
    if (-not (Wait-For { Log-Has $data (Ready-Line "Mira-$new-win-x64-portable.exe") } 120)) { Fail "portable: $new executable found, downloaded and verified" }
    Pass "portable: $new executable downloaded and verified"
    if (-not (Invoke-Button $mira.Id $restartLabel 8)) { Fail 'portable: the notice offers a restart' }
    if (-not $mira.WaitForExit(40000)) { Fail 'portable: Mira closes for the restart' }
    if (-not (Wait-For { (Version-Of $exe) -eq "$new.0" } 90)) { Fail 'portable: executable replaced' }
    if (-not (Wait-For { (Running $exe).Count -gt 0 } 40)) { Fail 'portable: Mira reopens' }
    Pass "portable: replaced in place and reopened in $(Version-Of $exe) after the restart button"
    if ((Get-Content -LiteralPath (Join-Path $data 'marker.txt') -Raw).Trim() -ne 'donnees de test' -or -not (Log-Has $data 'install.e depuis')) { Fail 'portable: data kept and installation logged' }
    if (-not (Wait-For { (Log-Has $data "version $([regex]::Escape($new)) en place") -and -not (Test-Path -LiteralPath (Join-Path $data 'updates\pending.json')) } 30)) { Fail 'portable: the new version records the installation' }
    Pass 'portable: data folder untouched, the reopened version records the installation'
    foreach ($process in Running $exe) { Close-Mira $process }

    # 4. Installer: silent install of the old version in a test folder, update when Mira closes, silent uninstall.
    # The test uses the real installer identity: it is skipped on a PC where Mira is installed with it.
    if (-not $SkipInstaller -and (Test-Path $uninstallKey) -and -not (Test-Install)) { $results.Add('INFO installer step skipped: Mira is installed on this PC with the installer'); $SkipInstaller = $true }
    if (-not $SkipInstaller) {
        # Left behind by an interrupted run.
        if ((Test-Install) -and (Test-Path -LiteralPath (Join-Path $installed 'unins000.exe')) -and -not (Uninstall-Test)) { Fail 'installer: earlier test installation removed' }
        if (Test-Path -LiteralPath $installed) { Remove-Item -LiteralPath $installed -Recurse -Force }
        $setup = Start-Process -FilePath (Join-Path $oldPackages "Mira-$old-win-x64-setup.exe") -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=`"!mpvengine`" /DIR=`"$installed`" /LOG=`"$root\install-old.log`"" -PassThru -Wait
        $exe = Join-Path $installed 'Mira.exe'; $data = Join-Path $installed 'data'
        if ($setup.ExitCode -ne 0 -or (Version-Of $exe) -ne "$old.0" -or -not (Test-Path -LiteralPath (Join-Path $installed 'unins000.exe'))) { Fail "installer: $old installed silently" }
        Pass "installer: $old installed in a test folder"
        New-Item -ItemType Directory -Path $data -Force | Out-Null; Set-Content -LiteralPath (Join-Path $data 'marker.txt') -Value 'donnees de test' -Encoding ascii
        $mira = Start-Mira $exe $data
        if (-not (Wait-For { Log-Has $data (Ready-Line "Mira-$new-win-x64-setup.exe") } 120)) { Fail "installer: $new setup downloaded and verified" }
        Pass "installer: $new setup downloaded and verified"
        Close-Mira $mira
        $setupRunning = { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "Mira-$new-win-x64-setup*" }).Count -gt 0 }
        if (-not (Wait-For { (Version-Of $exe) -eq "$new.0" -and -not (& $setupRunning) } 180)) { Fail 'installer: silent update when Mira closed' }
        if ((Get-Content -LiteralPath (Join-Path $data 'marker.txt') -Raw).Trim() -ne 'donnees de test') { Fail 'installer: data kept' }
        if (-not (Test-Path -LiteralPath (Join-Path $data 'updates\setup.log'))) { Fail 'installer: updated by its setup' }
        Pass "installer: updated silently by its setup to $(Version-Of $exe) when Mira closed, data folder untouched"
        if (-not (Uninstall-Test)) { Fail 'installer: silent uninstall' }
        Pass 'installer: uninstalled silently (data kept, as on any silent uninstall)'
        Remove-Item -LiteralPath $installed -Recurse -Force

        # 5. Installer through the restart button: Setup shows its progress, then reopens the installed Mira (normal profile).
        $setup = Start-Process -FilePath (Join-Path $oldPackages "Mira-$old-win-x64-setup.exe") -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=`"!mpvengine`" /DIR=`"$installed`" /LOG=`"$root\install-old-2.log`"" -PassThru -Wait
        if ($setup.ExitCode -ne 0 -or (Version-Of $exe) -ne "$old.0" -or -not (Test-Path -LiteralPath (Join-Path $installed 'unins000.exe'))) { Fail "installer restart: $old installed again" }
        New-Item -ItemType Directory -Path $data -Force | Out-Null; Set-Content -LiteralPath (Join-Path $data 'marker.txt') -Value 'donnees de test' -Encoding ascii
        $mira = Start-Mira $exe $data
        if (-not (Wait-For { Log-Has $data (Ready-Line "Mira-$new-win-x64-setup.exe") } 120)) { Fail "installer restart: $new setup downloaded and verified" }
        if (-not (Invoke-Button $mira.Id $restartLabel 8)) { Fail 'installer restart: the notice offers a restart' }
        if (-not $mira.WaitForExit(40000)) { Fail 'installer restart: Mira closes for the restart' }
        if (-not (Wait-For { (Version-Of $exe) -eq "$new.0" -and -not (& $setupRunning) } 180)) { Fail 'installer restart: updated' }
        if (-not (Wait-For { (Running $exe).Count -gt 0 -and (Log-Has $data "version $([regex]::Escape($new)) en place") } 60)) { Fail 'installer restart: Setup reopens the new version' }
        if ((Get-Content -LiteralPath (Join-Path $data 'marker.txt') -Raw).Trim() -ne 'donnees de test' -or -not (Test-Path -LiteralPath (Join-Path $data 'updates\setup.log'))) { Fail 'installer restart: updated by its setup, data kept' }
        Pass "installer: updated by its setup through the restart button, reopened in $(Version-Of $exe), data folder untouched"
        foreach ($process in Running $exe) { Close-Mira $process }
        if (-not (Uninstall-Test)) { Fail 'installer restart: silent uninstall' }
        Remove-Item -LiteralPath $installed -Recurse -Force
    }
} catch {
    if (-not ($results | Where-Object { $_ -like 'FAIL*' })) { $results.Add("FAIL $($_.Exception.Message)") }
    Write-Host "FAIL $($_.Exception.Message)"
} finally {
    if ($server -and -not $server.HasExited) { $server.Kill() }
    foreach ($process in (Running (Join-Path $installed 'Mira.exe'))) { $process.Kill() }
    # A test installation left by a failure is removed; an installation elsewhere is never touched.
    if ((Test-Install) -and (Test-Path -LiteralPath (Join-Path $installed 'unins000.exe'))) {
        if (Uninstall-Test) { $results.Add('INFO test installation removed after the failure') } else { $results.Add("INFO test installation still registered: $installed") }
    }
    # The installer pointed the Start menu shortcut and the Shell identity at its test folder: give them back.
    if ($shortcutBefore -and (Test-Path -LiteralPath $shortcutBefore) -and (Shortcut-Target) -ne $shortcutBefore) {
        Start-Process -FilePath $shortcutBefore -ArgumentList '--register-windows' -Wait -WindowStyle Hidden
        $results.Add("INFO Start menu shortcut given back to $shortcutBefore")
    }
    Set-Content -LiteralPath (Join-Path $root 'result.txt') -Value $results -Encoding UTF8
}
