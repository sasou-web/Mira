param()
# Runs tools/release.ps1 against a stand-in GitHub: gh, git, dotnet and Start-Sleep are functions here, which the
# script calls in their place (a function wins over a program of the same name, and the script sees the functions of
# whoever runs it). The stand-in keeps a draft, its files, the release workflows' runs and their attempts, and plays
# what went wrong in past releases:
#   A. ci.yml still running then failed once; a lost connection on git pull; a stale draft of another commit; mac.yml
#      failed once then passed with its icon check failed; ios.yml failed once; web.yml put a wrong copy on the draft.
#      The release must come out whole, each failure re-run on its own, the wrong copy attached again by its workflow.
#   B. ios.yml failed three times: the launch stops; the next one picks up the same draft and runs, re-runs only
#      ios.yml and publishes, without starting the three other workflows again.
# Run by ci.yml in Windows PowerShell 5.1. Writes only in .artifacts and dist (ignored by git) and a temporary folder.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src/Mira.Desktop/Mira.Desktop.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$release = Join-Path $PSScriptRoot 'release.ps1'
$stateFile = Join-Path $root ".artifacts/release-$version.json"

function Get-Sha([byte[]]$bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) } finally { $sha.Dispose() }
}
# The files a release workflow's artifact holds: name -> bytes.
function Get-ArtifactFiles([string]$workflow) {
    $v = $global:MiraHub.Version
    $files = [ordered]@{}
    switch ($workflow) {
        'windows.yml' { foreach ($name in @("Mira-$v-win-x64-setup.exe", "Mira-$v-win-x64-portable.exe", "Mira-$v-win-x64.zip")) { $files[$name] = "windows $name" } }
        'mac.yml' { $files["Mira-$v-mac-arm64.dmg"] = 'mac disk image' }
        'web.yml' { $files["mira-jellyfin-$v.zip"] = 'web plugin'; $files['jellyfin-manifest.json'] = '[]' }
        'ios.yml' { $files["Mira-$v-ios.ipa"] = 'iphone app' }
    }
    $bytes = [ordered]@{}
    foreach ($name in @($files.Keys)) {
        $bytes[$name] = [Text.Encoding]::UTF8.GetBytes($files[$name])
        if ($workflow -ne 'web.yml') { $bytes["$name.sha256"] = [Text.Encoding]::UTF8.GetBytes("$(Get-Sha $bytes[$name])  $name`n") }
    }
    $bytes
}
function Add-Asset([string]$name, [byte[]]$bytes, [string]$uploader) {
    $hub = $global:MiraHub
    $hub.Release.Assets = @($hub.Release.Assets | Where-Object { $_.Name -ne $name })
    $hub.NextId++
    $hub.Release.Assets += [pscustomobject]@{ Id = $hub.NextId; Name = $name; Digest = "sha256:$(Get-Sha $bytes)"; Uploader = $uploader }
}
function New-Run([string]$workflow, [string]$branch, [string]$sha, [string[]]$plan, [string]$status = 'queued') {
    $hub = $global:MiraHub
    $hub.NextId++
    $run = [pscustomobject]@{ Id = [string]$hub.NextId; Workflow = $workflow; Branch = $branch; Sha = $sha; Plan = $plan; Attempt = 1; Status = $status; Conclusion = ''; Jobs = @() }
    $hub.Runs[$run.Id] = $run
    $run
}
# One step of a run each time someone looks at it: queued, in progress, then completed as its plan says for that
# attempt (success, success-warn: an other job failed, success-wrong: a wrong copy on the draft, fail).
function Step-Run($run) {
    $hub = $global:MiraHub
    if ($run.Status -eq 'queued') { $run.Status = 'in_progress'; return }
    if ($run.Status -ne 'in_progress') { return }
    $outcome = if ($run.Attempt -le $run.Plan.Count) { $run.Plan[$run.Attempt - 1] } else { 'success' }
    $run.Status = 'completed'
    $passed = $outcome -like 'success*'
    $hub.NextId++
    $build = [pscustomobject]@{ name = 'build'; conclusion = $(if ($passed) { 'success' } else { 'failure' }); databaseId = $hub.NextId; steps = @([pscustomobject]@{ name = 'Tests'; conclusion = $(if ($outcome -eq 'success-warn') { 'failure' } else { 'success' }) }) }
    $hub.NextId++
    $attach = [pscustomobject]@{ name = 'release'; conclusion = $(if ($passed) { 'success' } else { 'skipped' }); databaseId = $hub.NextId; steps = @() }
    $run.Jobs = @($build, $attach)
    if ($outcome -eq 'success-warn') { $run.Jobs += [pscustomobject]@{ name = 'icon-check'; conclusion = 'failure'; databaseId = 0; steps = @() } }
    $run.Conclusion = if ($passed -and $outcome -ne 'success-warn') { 'success' } else { 'failure' }
    if ($run.Workflow -eq 'ci.yml' -or -not $passed) { return }
    if (-not $hub.Release -or -not $hub.Release.Draft -or $hub.Release.Target -ne $run.Sha) { throw "Stand-in: $($run.Workflow) found no draft for its commit." }
    $files = Get-ArtifactFiles $run.Workflow
    foreach ($name in @($files.Keys)) {
        $bytes = $files[$name]
        if ($outcome -eq 'success-wrong' -and $run.Attempt -eq 1) { $bytes = [Text.Encoding]::UTF8.GetBytes('a copy cut halfway') }
        Add-Asset $name $bytes 'github-actions[bot]'
    }
}
function Get-Option([object[]]$arguments, [string]$name) {
    $i = [array]::IndexOf($arguments, $name)
    if ($i -ge 0 -and $i + 1 -lt $arguments.Count) { [string]$arguments[$i + 1] }
}
# A run's fields as gh prints them for --json a,b,c --jq '... | @tsv'.
function Format-Fields($run, [string]$fields) {
    $values = foreach ($field in $fields.Split(',')) {
        switch ($field) {
            'databaseId' { $run.Id } 'status' { $run.Status } 'conclusion' { $run.Conclusion } 'attempt' { $run.Attempt }
            'url' { "https://github.com/sasou-web/Mira/actions/runs/$($run.Id)" } default { throw "Stand-in: field $field." }
        }
    }
    $values -join "`t"
}

function gh {
    $hub = $global:MiraHub
    $a = @($args | ForEach-Object { if ($_ -is [array]) { $_ -join ',' } else { [string]$_ } })
    $hub.Calls++
    if ($hub.Calls -gt 4000) { throw 'Stand-in: release.ps1 asked GitHub more than 4000 times (a loop that never ends).' }
    $hub.Log.Add($a -join ' ')
    $global:LASTEXITCODE = 0
    $fail = { $global:LASTEXITCODE = 1 }
    $words = @($a | Where-Object { $_ -notlike '-*' })
    switch ($a[0]) {
        'api' {
            $method = Get-Option $a '-X'
            $path = @($a | Select-Object -Skip 1 | Where-Object { $_ -like 'repos/*' -or $_ -eq 'user' })[0]
            if ($path -eq 'user') { 'sasou-web'; return }
            if ($path -eq 'repos/sasou-web/Mira/immutable-releases') { 'true'; return }
            if ($path -like 'repos/sasou-web/Mira/git/ref/heads/*') {
                $branch = $path.Substring('repos/sasou-web/Mira/git/ref/heads/'.Length)
                if ($hub.Branches.ContainsKey($branch)) { $hub.Branches[$branch] } else { & $fail }
                return
            }
            if ($path -eq 'repos/sasou-web/Mira/git/refs' -and $method -eq 'POST') {
                $hub.Branches[((Get-Option $a '-f') -replace '^ref=refs/heads/', '')] = ($a | Where-Object { $_ -like 'sha=*' }) -replace '^sha=', ''
                return
            }
            if ($path -like 'repos/sasou-web/Mira/git/refs/heads/*') {
                $branch = $path.Substring('repos/sasou-web/Mira/git/refs/heads/'.Length)
                if ($method -eq 'DELETE') { $hub.Branches.Remove($branch); return }
                $hub.Branches[$branch] = ($a | Where-Object { $_ -like 'sha=*' }) -replace '^sha=', ''
                return
            }
            if ($path -like 'repos/sasou-web/Mira/releases/assets/*' -and $method -eq 'DELETE') {
                $id = [int]$path.Substring('repos/sasou-web/Mira/releases/assets/'.Length)
                $hub.Release.Assets = @($hub.Release.Assets | Where-Object { $_.Id -ne $id })
                return
            }
            if ($path -eq 'repos/sasou-web/Mira/releases/1/assets?per_page=100') {
                foreach ($asset in @($hub.Release.Assets)) { "$($asset.Name)`tuploaded`t$($asset.Digest)`t$($asset.Uploader)`t$($asset.Id)" }
                return
            }
            if ($path -eq "repos/sasou-web/Mira/releases/tags/v$($hub.Version)") { if ($hub.Release -and -not $hub.Release.Draft) { 'true' } else { & $fail }; return }
            throw "Stand-in: gh $($a -join ' ')"
        }
        'release' {
            $tag = $words[2]
            switch ($a[1]) {
                '--help' { "Available commands:`n  create`n  delete`n  edit`n  upload`n  view"; return }
                'view' {
                    if (-not $hub.Release -or $hub.Release.Tag -ne $tag) { & $fail; return }
                    $json = Get-Option $a '--json'
                    if ($json -eq 'apiUrl') { 'https://api.github.com/repos/sasou-web/Mira/releases/1' }
                    else { "$(([string]$hub.Release.Draft).ToLowerInvariant())`t$($hub.Release.Target)" }
                    return
                }
                'delete' { $hub.Release = $null; return }
                'create' {
                    if ($hub.Release) { & $fail; return }
                    if (-not (Test-Path -LiteralPath (Get-Option $a '--notes-file'))) { throw 'Stand-in: no notes file.' }
                    $hub.Release = [pscustomobject]@{ Tag = $tag; Draft = $true; Target = (Get-Option $a '--target'); Assets = @() }
                    return
                }
                'edit' { if ($a -contains '--draft=false') { $hub.Release.Draft = $false }; return }
                'upload' {
                    foreach ($file in @($words | Select-Object -Skip 3 | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })) { Add-Asset ([IO.Path]::GetFileName($file)) ([IO.File]::ReadAllBytes($file)) 'sasou-web' }
                    return
                }
            }
            throw "Stand-in: gh $($a -join ' ')"
        }
        'workflow' {
            $workflow = $words[2]
            if ($workflow -eq 'bump.yml') { $hub.Bumped = $true; return }
            $branch = Get-Option $a '--ref'
            if (-not $hub.Branches.ContainsKey($branch)) { throw "Stand-in: no branch $branch." }
            $hub.Dispatches++
            $run = New-Run $workflow $branch $hub.Branches[$branch] $hub.Plans[$workflow]
            "https://github.com/sasou-web/Mira/actions/runs/$($run.Id)"
            return
        }
        'run' {
            switch ($a[1]) {
                'list' {
                    $workflow = Get-Option $a '--workflow'; $fields = Get-Option $a '--json'
                    if ((Get-Option $a '--event') -eq 'push') {
                        if ($workflow -ne 'ci.yml') { "success`thttps://github.com/sasou-web/Mira/actions/runs/1"; return }
                        foreach ($run in @($hub.Runs.Values | Where-Object { $_.Workflow -eq 'ci.yml' -and $_.Sha -eq (Get-Option $a '--commit') })) { Step-Run $run; Format-Fields $run $fields }
                        return
                    }
                    $branch = Get-Option $a '--branch'
                    foreach ($run in @($hub.Runs.Values | Where-Object { $_.Workflow -eq $workflow -and $_.Branch -eq $branch } | Sort-Object { [int]$_.Id } -Descending)) { Format-Fields $run $fields }
                    return
                }
                'view' {
                    $run = $hub.Runs[$words[2]]
                    if (-not $run) { & $fail; return }
                    Step-Run $run
                    [pscustomobject]@{ status = $run.Status; conclusion = $run.Conclusion; attempt = $run.Attempt; url = "https://github.com/sasou-web/Mira/actions/runs/$($run.Id)"; headSha = $run.Sha; headBranch = $run.Branch; jobs = $run.Jobs } | ConvertTo-Json -Depth 6
                    return
                }
                'rerun' {
                    $job = Get-Option $a '--job'
                    $run = if ($job) { @($hub.Runs.Values | Where-Object { @($_.Jobs | Where-Object { [string]$_.databaseId -eq $job }).Count })[0] } else { $hub.Runs[$words[2]] }
                    if (-not $run -or $run.Status -ne 'completed') { & $fail; return }
                    $hub.Reruns++
                    $run.Attempt++; $run.Status = 'queued'; $run.Conclusion = ''
                    return
                }
                'cancel' { $run = $hub.Runs[$words[2]]; $run.Status = 'completed'; $run.Conclusion = 'cancelled'; return }
                'download' {
                    $run = $hub.Runs[$words[2]]
                    $folder = Get-Option $a '--dir'
                    New-Item -ItemType Directory -Path $folder -Force | Out-Null
                    $files = Get-ArtifactFiles $run.Workflow
                    foreach ($name in @($files.Keys)) { [IO.File]::WriteAllBytes((Join-Path $folder $name), $files[$name]) }
                    return
                }
            }
        }
    }
    throw "Stand-in: gh $($a -join ' ')"
}
function git {
    $hub = $global:MiraHub
    $global:LASTEXITCODE = 0
    switch ([string]$args[0]) {
        'pull' { if ($hub.PullFailures -gt 0) { $hub.PullFailures--; $global:LASTEXITCODE = 1 } }
        'rev-parse' { $hub.Commit }
        'config' { 'origin' }
    }
}
function dotnet {
    $a = @($args | ForEach-Object { [string]$_ })
    $global:LASTEXITCODE = 0
    if ($a[0] -eq '--version') { '10.0.100'; return }
    if ($a -contains 'notes') { Set-Content -LiteralPath (Get-Option $a '--out') -Value 'Notes' -Encoding UTF8; return }
    if ($a -contains 'manifest') {
        $packages = Get-Option $a '--packages'
        [IO.File]::WriteAllText((Join-Path $packages 'mira-update.json'), "{ `"version`": `"$($global:MiraHub.Version)`" }")
        [IO.File]::WriteAllText((Join-Path $packages 'mira-update.json.sig'), 'signature')
    }
}
function Start-Sleep { }

# A stand-in GitHub with ci.yml on the commit and the plan of each release workflow.
function New-Hub([hashtable]$plans, [string[]]$ci, [int]$pullFailures = 0) {
    $global:MiraHub = [pscustomobject]@{
        Version = $version; Commit = 'c0ffee' + ('0' * 34); Branches = @{ main = 'c0ffee' + ('0' * 34) }; Runs = @{}; Release = $null; NextId = 100
        Plans = $plans; PullFailures = $pullFailures; Dispatches = 0; Reruns = 0; Bumped = $false; Calls = 0; Log = (New-Object System.Collections.Generic.List[string])
    }
    [void](New-Run 'ci.yml' 'main' $global:MiraHub.Commit $ci 'in_progress')
}
function Invoke-Release {
    try { & $release -Unsigned *>&1 | ForEach-Object { Write-Host "    $_" }; $null }
    catch { $_.Exception.Message }
}
$failures = New-Object System.Collections.Generic.List[string]
function Assert([bool]$condition, [string]$what) {
    if ($condition) { Write-Output "  ok    $what" } else { Write-Output "  FAIL  $what"; $failures.Add($what) }
}
# The draft holds the 14 files, each one as its workflow built it (or as release.ps1 signed it), and is published.
function Assert-Published([string]$scenario) {
    $hub = $global:MiraHub
    Assert ($hub.Release -and -not $hub.Release.Draft) "$scenario : release publiée"
    $assets = @($hub.Release.Assets)
    Assert ($assets.Count -eq 14) "$scenario : 14 fichiers sur la release ($($assets.Count))"
    foreach ($workflow in @('windows.yml', 'web.yml', 'mac.yml', 'ios.yml')) {
        $files = Get-ArtifactFiles $workflow
        foreach ($name in @($files.Keys)) {
            $asset = @($assets | Where-Object { $_.Name -eq $name })[0]
            Assert ($asset -and $asset.Digest -eq "sha256:$(Get-Sha $files[$name])" -and $asset.Uploader -eq 'github-actions[bot]') "$scenario : $name, celui de $workflow"
        }
    }
    Assert (-not $hub.Branches.ContainsKey("release/v$version")) "$scenario : branche release/v$version retirée"
    Assert $hub.Bumped "$scenario : Homebrew prévenu"
    Assert (-not (Test-Path -LiteralPath $stateFile)) "$scenario : plus de reprise en attente"
}

$appData = $env:APPDATA
$keyFolder = Join-Path ([IO.Path]::GetTempPath()) "mira-release-check-$PID"
try {
    New-Item -ItemType Directory -Path (Join-Path $keyFolder 'Mira Release') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $keyFolder 'Mira Release\update-signing.key') -Value 'stand-in'
    $env:APPDATA = $keyFolder
    Remove-Item -LiteralPath $stateFile -ErrorAction SilentlyContinue

    Write-Output 'A. Ce qui a fait échouer les releases passées, réparé tout seul'
    New-Hub @{ 'windows.yml' = @('success'); 'web.yml' = @('success-wrong'); 'mac.yml' = @('fail', 'success-warn'); 'ios.yml' = @('fail', 'success') } @('fail', 'success') 1
    $global:MiraHub.Release = [pscustomobject]@{ Tag = "v$version"; Draft = $true; Target = 'dead' + ('0' * 36); Assets = @() }
    Add-Asset 'old.zip' ([byte[]](1, 2, 3)) 'github-actions[bot]'
    $error1 = Invoke-Release
    Assert (-not $error1) "A : aucun arrêt ($error1)"
    Assert ($global:MiraHub.Dispatches -eq 4) "A : quatre workflows lancés une fois chacun ($($global:MiraHub.Dispatches))"
    Assert ($global:MiraHub.Reruns -eq 4) "A : relancés ci.yml, mac.yml, ios.yml et l’envoi de web.yml ($($global:MiraHub.Reruns))"
    Assert (@($global:MiraHub.Runs.Values | Where-Object { $_.Workflow -ne 'ci.yml' -and $_.Branch -ne "release/v$version" }).Count -eq 0) 'A : workflows lancés sur la branche de la release'
    Assert-Published 'A'

    Write-Output 'B. Un workflow qui échoue trois fois, puis la même commande qui reprend'
    New-Hub @{ 'windows.yml' = @('success'); 'web.yml' = @('success'); 'mac.yml' = @('success'); 'ios.yml' = @('fail', 'fail', 'fail', 'success') } @('success')
    $error1 = Invoke-Release
    Assert ($error1 -like '*ios.yml a échoué 3 fois*') "B : arrêt après trois échecs d’ios.yml ($error1)"
    Assert ($global:MiraHub.Release -and $global:MiraHub.Release.Draft) 'B : le brouillon reste'
    Assert (Test-Path -LiteralPath $stateFile) 'B : la reprise est notée'
    $error2 = Invoke-Release
    Assert (-not $error2) "B : la reprise publie ($error2)"
    Assert ($global:MiraHub.Dispatches -eq 4) "B : rien de relancé de zéro ($($global:MiraHub.Dispatches) lancements)"
    Assert ($global:MiraHub.Reruns -eq 3) "B : seul ios.yml relancé, trois fois ($($global:MiraHub.Reruns))"
    Assert-Published 'B'
} finally {
    $env:APPDATA = $appData
    Remove-Item -LiteralPath $keyFolder -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $stateFile -ErrorAction SilentlyContinue
}
if ($failures.Count) {
    Write-Output "$($failures.Count) contrôle(s) en échec. Appels à GitHub du dernier scénario :"
    $global:MiraHub.Log | Select-Object -Last 60 | ForEach-Object { "    gh $_" }
    exit 1
}
Write-Output 'release.ps1 : tous les contrôles passent.'
