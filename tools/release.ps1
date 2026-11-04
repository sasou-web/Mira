param([switch]$Unsigned, [switch]$DryRun)
# Publishes the version in src/Mira.Desktop/Mira.Desktop.csproj in one command, on the PC that holds the signing key:
#   1. checks gh, immutable releases on the repository, the signing key (it opens, installed copies trust it), a clean
#      checkout with no untracked file, then brings main up to date (fast-forward only) and waits for GitHub's Windows
#      build (ci.yml) on it: still running, it waits; failed, it re-runs what failed once, then stops if it fails again;
#   2. creates the release vX.Y.Z as a draft on that commit, with the notes from WhatsNew.json and no file yet (a draft
#      is seen by no one and has no tag yet), and the branch release/vX.Y.Z at that commit: the workflows run from it,
#      so main can move meanwhile;
#   3. runs windows.yml, mac.yml, web.yml and ios.yml on that branch for that draft, which build from the same commit
#      and attach the Windows files (built by GitHub, never on this PC, and signed by SignPath Foundation: two signing
#      requests to approve in SignPath while the script waits), the .dmg, mira-jellyfin-X.Y.Z.zip and
#      jellyfin-manifest.json, the iPhone's unsigned .ipa (AltStore signs it on the iPhone), with their .sha256, and
#      waits for all four. A run that fails (a Mac GitHub never found, a dropped download, a flaky test) has what failed
#      re-run, twice at most; the checks pull requests already passed (iPhone simulator, Mac Tailscale, icons) only warn
#      in a release;
#   4. takes those files from the runs it watched (their artifacts), checks them (Mira.Release, the checks Mira for
#      Windows makes too, and the Authenticode signatures of the Windows executables), signs mira-update.json for every
#      platform, checks that signature against the keys installed copies trust, and attaches it;
#   5. checks that the draft holds exactly the 14 expected files, each with the SHA-256 of the file checked here (all
#      but the manifest uploaded by GitHub Actions itself; one missing or different is attached again by its workflow),
#      then publishes it: the release appears whole, at once, with the notes from WhatsNew.json, and checks that GitHub
#      made it immutable; then has the Homebrew tap take the new Mac disk image.
# Stopped (closed window, lost connection, a workflow that keeps failing), the same command picks up where it was: the
# draft and the runs of that commit are kept in .artifacts/release-X.Y.Z.json, only what is missing is run again.
# Every call to GitHub is tried four times.
# -Unsigned has windows.yml skip SignPath (Windows files without Authenticode signature: before SignPath accepts the
# project, or while it is down). -DryRun does all of it but the publication: the draft stays, unseen, and the next run
# publishes it without building anything again.
# Works in Windows PowerShell 5.1 and PowerShell 7 (saved with a BOM; messages with an apostrophe in double quotes:
# PowerShell reads a typographic apostrophe as a single quote). jq expressions passed to gh hold no double quote: Windows
# PowerShell drops them from a native command's arguments. tools/release-check.ps1 runs it against a stand-in GitHub.
$ErrorActionPreference = 'Stop'
# The .NET SDK sends usage data unless told not to; the scripts tell it, on this PC as in the CI.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repository = 'sasou-web/Mira'
$workflows = @('windows.yml', 'web.yml', 'mac.yml', 'ios.yml')
# Re-runs of a release workflow's failed jobs, per workflow and per launch of this script.
$rerunsAllowed = 2
# Seconds between two looks at GitHub while waiting.
$pollSeconds = 30
# The branch the release workflows run on (release/vX.Y.Z, or main when GitHub refuses that branch).
$script:ref = 'main'

function Invoke-Native([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what a échoué (code $LASTEXITCODE)." }
}
# True when the command succeeds; its output is not shown. (Windows PowerShell stops on a redirected error stream.)
function Test-Native([scriptblock]$command) {
    $ErrorActionPreference = 'Continue'
    & $command *> $null
    return $LASTEXITCODE -eq 0
}
# What the command writes when it succeeds, nothing otherwise; its errors are not shown.
function Get-Native([scriptblock]$command) {
    $ErrorActionPreference = 'Continue'
    $output = & $command 2> $null
    if ($LASTEXITCODE -eq 0) { $output }
}
# Runs a command that talks to GitHub up to four times, 5, 15 then 45 s apart: a dropped connection or a 502 is common
# and never a reason to stop. $retryAlready, when given, tells whether a failed attempt did its work after all (its
# answer lost on the way back): nothing is done twice. Returns what the command printed. (Locals named retry*: the
# command reads the caller's variables.)
function Invoke-Retry([string]$retryWhat, [scriptblock]$retryCommand, [scriptblock]$retryAlready = $null) {
    $retryDelays = @(5, 15, 45)
    for ($retryAttempt = 0; ; $retryAttempt++) {
        $ErrorActionPreference = 'Continue'
        $retryOutput = & $retryCommand
        $retryCode = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($retryCode -eq 0) { return $retryOutput }
        if ($retryAlready -and (& $retryAlready)) { return }
        if ($retryAttempt -ge $retryDelays.Count) {
            throw "$retryWhat a échoué $($retryAttempt + 1) fois de suite (code $retryCode). Vérifie ta connexion (et https://www.githubstatus.com), puis relance la même commande : elle reprend où elle s’est arrêtée."
        }
        Write-Warning "$retryWhat a échoué (code $retryCode) : nouvel essai dans $($retryDelays[$retryAttempt]) s."
        Start-Sleep -Seconds $retryDelays[$retryAttempt]
    }
}
# What a read-only command answers, tried three times, 5 s apart; nothing when it keeps failing.
function Get-Answer([scriptblock]$answerCommand) {
    for ($answerAttempt = 0; $answerAttempt -lt 3; $answerAttempt++) {
        if ($answerAttempt) { Start-Sleep -Seconds 5 }
        $ErrorActionPreference = 'Continue'
        $answerOutput = & $answerCommand 2> $null
        $answerCode = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($answerCode -eq 0) { return $answerOutput }
    }
}
# The runs of a workflow started by hand (workflow_dispatch) on the release branch, newest first: id, status and page.
function Get-Runs([string]$workflow) {
    $lines = @(Invoke-Retry "gh run list ($workflow)" { gh run list --repo $repository --workflow $workflow --branch $script:ref --event workflow_dispatch --limit 30 --json databaseId,status,url --jq '.[] | [.databaseId, .status, .url] | @tsv' })
    foreach ($line in $lines) {
        if (-not $line) { continue }
        $id, $status, $url = $line -split "`t"
        [pscustomobject]@{ Workflow = $workflow; Id = [string]$id; Status = $status; Url = $url }
    }
}
# Runs $workflow on the release branch for the draft (with $more, other "-f name=value" inputs) and returns that run's
# id: the one gh names, or else the first new one in the list. A dispatch whose answer was lost may have started the run
# anyway: it is looked for before trying again, never started twice.
function Start-ReleaseWorkflow([string]$workflow, [string]$tag, [string]$commit, [string[]]$more = @()) {
    $known = @(Get-Runs $workflow | ForEach-Object { $_.Id })
    $printed = @(Invoke-Retry "gh workflow run $workflow" { gh workflow run $workflow --repo $repository --ref $script:ref -f "release=$tag" -f "commit=$commit" @more } {
        Start-Sleep -Seconds 10
        @(Get-Runs $workflow | Where-Object { $known -notcontains $_.Id }).Count -gt 0
    })
    $named = $null
    if (($printed -join ' ') -match '/actions/runs/(\d+)') { $named = $Matches[1] }
    # GitHub lists a dispatched run a few seconds later.
    for ($attempt = 0; $attempt -lt 36; $attempt++) {
        $new = @(Get-Runs $workflow | Where-Object { if ($named) { $_.Id -eq $named } else { $known -notcontains $_.Id } })
        if ($new.Count) { return $new[-1].Id }
        Start-Sleep -Seconds 5
    }
    throw "GitHub ne montre pas l’exécution de $workflow lancée pour $tag : regarde https://github.com/$repository/actions, puis relance la même commande."
}
# Whether the release is a draft, and its commit, as "true<tab><commit>"; empty when there is no release by that name.
function Get-ReleaseState([string]$tag) {
    [string](Get-Answer { gh release view $tag --repo $repository --json isDraft,targetCommitish --jq '[(.isDraft | tostring), .targetCommitish] | @tsv' })
}
# A run as GitHub shows it now (status, conclusion, attempt, commit, branch, page, jobs of its latest attempt with their
# steps), or nothing when GitHub does not answer.
function Get-RunView([string]$id) {
    $json = Get-Native { gh run view $id --repo $repository --json status,conclusion,attempt,url,headSha,headBranch,jobs }
    if ($json) { ($json -join "`n") | ConvertFrom-Json }
}
# What did not pass in a run whose files reached the draft: other jobs (the icon on macOS 26) and checks that only warn
# in a release (continue-on-error).
function Get-Warnings($run) {
    foreach ($job in @($run.jobs)) {
        if ($job.conclusion -and @('success', 'skipped', 'neutral') -notcontains $job.conclusion) { "le job « $($job.name) » ($($job.conclusion))"; continue }
        foreach ($step in @($job.steps)) { if ($step.conclusion -eq 'failure') { "« $($step.name) » ($($job.name))" } }
    }
}
# Has the failed jobs of a run, and those after them, run again (a cancelled job counts: a Mac that never came);
# what passed keeps its result and its files. The whole run when GitHub refuses that.
function Restart-Run([string]$id) {
    if (Test-Native { gh run rerun $id --failed --repo $repository }) { return }
    $restarted = { Start-Sleep -Seconds 5; $view = Get-RunView $id; $view -and $view.status -ne 'completed' }
    if (& $restarted) { return }
    Invoke-Retry "gh run rerun $id" { gh run rerun $id --repo $repository } $restarted | Out-Null
}
# Waits for the runs ($ids: workflow -> run id) until each has attached its files to the draft (its job « release »
# passed), re-running what failed $rerunsAllowed times at most. $after: workflow -> the first attempt to look at (a
# re-run asked before). Returns workflow -> run as last seen (progress goes to the host, not to what it returns).
function Wait-ReleaseRuns([hashtable]$ids, [hashtable]$after = @{}) {
    $finished = @{}; $expected = @{}; $reruns = @{}; $shown = @{}; $askedAt = @{}; $silent = 0
    foreach ($workflow in $after.Keys) { $expected[$workflow] = $after[$workflow]; $askedAt[$workflow] = Get-Date }
    while ($finished.Count -lt $ids.Count) {
        foreach ($workflow in @($ids.Keys)) {
            if ($finished.ContainsKey($workflow)) { continue }
            $run = Get-RunView $ids[$workflow]
            if (-not $run) {
                if (++$silent -ge 40) { throw "GitHub ne répond plus depuis 20 minutes (gh run view) : vérifie ta connexion, puis relance la même commande : elle reprend où elle s’est arrêtée." }
                continue
            }
            $silent = 0
            if ($expected.ContainsKey($workflow) -and [int]$run.attempt -lt $expected[$workflow]) {
                if (((Get-Date) - $askedAt[$workflow]).TotalMinutes -gt 15) { throw "GitHub n’a pas relancé $workflow en 15 minutes ($($run.url)) : relance la même commande." }
                continue
            }
            $seen = "$($run.status) $($run.attempt)"
            if ($shown[$workflow] -ne $seen) {
                $shown[$workflow] = $seen
                $state = switch ($run.status) { 'queued' { 'attend une machine de GitHub' } 'waiting' { 'attend une machine de GitHub' } 'pending' { 'attend une machine de GitHub' } 'in_progress' { 'en cours' } 'completed' { 'fini' } default { $run.status } }
                Write-Host "$workflow : $state (essai $($run.attempt)) : $($run.url)"
            }
            if ($run.status -ne 'completed') { continue }
            $attach = @($run.jobs | Where-Object { $_.name -eq 'release' }) | Select-Object -First 1
            if ($attach -and $attach.conclusion -eq 'success') {
                $finished[$workflow] = $run
                Write-Host "$workflow : ses fichiers sont sur le brouillon."
                foreach ($warning in @(Get-Warnings $run)) { Write-Warning "$workflow : $warning n’a pas réussi, sans bloquer la release ; à regarder : $($run.url)" }
                continue
            }
            $failed = @($run.jobs | Where-Object { $_.conclusion -and @('success', 'skipped', 'neutral') -notcontains $_.conclusion } | ForEach-Object { "« $($_.name) » ($($_.conclusion))" })
            $why = if ($failed.Count) { $failed -join ', ' } else { "conclusion $($run.conclusion)" }
            $done = [int]$reruns[$workflow]
            if ($done -ge $rerunsAllowed) {
                throw "$workflow a échoué $($done + 1) fois de suite ($why) : ce n’est sans doute plus un accident, regarde $($run.url). Si c’en est un quand même (machines de GitHub en panne : https://www.githubstatus.com), relance la même commande : elle reprend où elle s’est arrêtée, sans refaire ce qui a réussi."
            }
            Write-Warning "$workflow a échoué ($why, essai $($run.attempt)) : je relance ce qui a échoué ($($done + 1)/$rerunsAllowed). $($run.url)"
            Restart-Run $ids[$workflow]
            $reruns[$workflow] = $done + 1
            $expected[$workflow] = [int]$run.attempt + 1
            $askedAt[$workflow] = Get-Date
        }
        if ($finished.Count -lt $ids.Count) { Start-Sleep -Seconds $pollSeconds }
    }
    return $finished
}
# Waits until ci.yml passed on $commit: started, then finished. A failure is re-run once (a runner, a flaky test); a
# second one stops here.
function Wait-WindowsBuild([string]$commit) {
    $short = $commit.Substring(0, 7)
    $since = Get-Date; $rerun = $null; $said = ''
    while ($true) {
        $checks = @(Invoke-Retry 'gh run list (ci.yml)' { gh run list --repo $repository --workflow ci.yml --commit $commit --event push --limit 10 --json databaseId,status,conclusion,attempt,url --jq '.[] | [.databaseId, .status, .conclusion, .attempt, .url] | @tsv' } |
            Where-Object { $_ } | ForEach-Object { , ($_ -split "`t") })
        if (@($checks | Where-Object { $_[1] -eq 'completed' -and $_[2] -eq 'success' }).Count) {
            Write-Output "ci.yml a réussi sur $short."
            return
        }
        if (-not $checks.Count) {
            if (((Get-Date) - $since).TotalMinutes -gt 15) { throw "GitHub n’a lancé aucun ci.yml pour $short en 15 minutes : regarde https://github.com/$repository/actions/workflows/ci.yml, puis relance." }
            if ($said -ne 'none') { $said = 'none'; Write-Output "ci.yml n’a pas encore démarré pour $short : j’attends." }
        }
        elseif ($rerun -and [int]$checks[0][3] -le $rerun) {
            if (((Get-Date) - $since).TotalMinutes -gt 15) { throw "GitHub n’a pas relancé ci.yml en 15 minutes ($($checks[0][4])) : relance la même commande." }
        }
        elseif ($checks[0][1] -ne 'completed') {
            if ($said -ne "run $($checks[0][3])") { $said = "run $($checks[0][3])"; Write-Output "ci.yml tourne encore pour $short ($($checks[0][4])) : j’attends sa fin (20 minutes environ)." }
        }
        elseif (-not $rerun) {
            Write-Warning "ci.yml a échoué pour $short ($($checks[0][4])) : je relance ce qui a échoué, une fois (machine de GitHub, test instable)."
            $rerun = [int]$checks[0][3]
            $since = Get-Date
            Restart-Run $checks[0][0]
        }
        else {
            throw "ci.yml a encore échoué pour $short après une relance ($($checks[0][4])) : ce n’est sans doute pas un accident. Corrige main, puis relance ce script."
        }
        Start-Sleep -Seconds $pollSeconds
    }
}
# Points the branch release/vX.Y.Z at $commit (made if missing); false when GitHub refuses it.
function Set-ReleaseBranch([string]$branch, [string]$commit) {
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $at = "$(Get-Native { gh api "repos/$repository/git/ref/heads/$branch" --jq .object.sha })".Trim()
        if ($at -eq $commit) { return $true }
        if ($attempt) { Start-Sleep -Seconds 5 }
        if ($at) { [void](Test-Native { gh api -X PATCH "repos/$repository/git/refs/heads/$branch" -f "sha=$commit" -F force=true }) }
        else { [void](Test-Native { gh api -X POST "repos/$repository/git/refs" -f "ref=refs/heads/$branch" -f "sha=$commit" }) }
    }
    return "$(Get-Native { gh api "repos/$repository/git/ref/heads/$branch" --jq .object.sha })".Trim() -eq $commit
}
# Mira's own files of the application folder (its Tailscale node included), taken out of the archive to check their signatures.
function Expand-Checked([string]$archive, [string]$folder) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($name in @('Mira.exe', 'Mira.dll', 'Mira.Core.dll', 'mira-tailscale.exe')) {
            $entry = $zip.Entries | Where-Object { ($_.FullName -replace '\\', '/') -eq "Mira/$name" } | Select-Object -First 1
            if (-not $entry) { throw "Mira/$name manque dans $([IO.Path]::GetFileName($archive))." }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $folder $name), $true)
            Join-Path $folder $name
        }
    } finally { $zip.Dispose() }
}

Push-Location $workspace
try {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI (gh) introuvable : installe-le (https://cli.github.com), puis lance « gh auth login »."
    }
    # The account gh will use, only (gh auth status also fails for another, stale account or host).
    if (-not (Get-Answer { gh api user --jq .login })) { throw "gh ne peut pas se servir de ton compte GitHub (ou GitHub ne répond pas) : lance « gh auth login », puis relance." }
    # Immutable releases: once published, neither the files nor the tag of a release can change, even from this account.
    # Mira for Windows has Jellyfin install Mira web from one release, checked once: without them, it could be swapped
    # afterwards. GitHub answers 200 with "enabled" (its documentation also announces a 404 when they are off).
    if ((Get-Answer { gh api "repos/$repository/immutable-releases" --jq .enabled }) -ne 'true') {
        throw "Les releases immuables ne sont pas activées sur $repository (ou gh ne peut pas le lire) : sans elles, les fichiers d’une release publiée pourraient encore être remplacés. Active-les une fois : https://github.com/$repository/settings, section « Releases », case « Enable release immutability » (ou « gh api -X PUT repos/$repository/immutable-releases »), puis relance."
    }
    # mira-update.json is signed at the end, once the workflows have built every platform: the key is needed first. A
    # new key would be refused by every installed copy: a lost one comes back from its backup.
    $key = if ($env:APPDATA) { Join-Path $env:APPDATA 'Mira Release\update-signing.key' } else { '%APPDATA%\Mira Release\update-signing.key' }
    if (-not ($env:APPDATA -and (Test-Path -LiteralPath $key))) {
        throw "Clé de signature introuvable ($key) : sans elle, les copies installées ne se mettraient pas à jour. Restaure-la avec « dotnet run --project tools/Mira.Release -c Release -- import-backup --in <copie> » ; ne lance pas « keygen » : une nouvelle clé serait refusée par toutes les copies installées."
    }

    # What is published is main as it is on GitHub, nothing else.
    if (git status --porcelain --untracked-files=no) { throw 'Des fichiers suivis sont modifiés : valide-les ou mets-les de côté (git stash) avant de publier.' }
    # Nothing is built on this PC any more, but Mira.Release (the notes, the manifest) runs from this folder.
    $untracked = @(git ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw "git ls-files a échoué (code $LASTEXITCODE)." }
    if ($untracked.Count) {
        $shown = ($untracked | Select-Object -First 15) -join ', '
        if ($untracked.Count -gt 15) { $shown += " (et $($untracked.Count - 15) autres)" }
        throw "Fichiers absents de git : $shown. Valide-les, déplace-les ou supprime-les avant de publier."
    }
    Invoke-Native 'git checkout main' { git checkout main }
    Invoke-Retry 'git pull' { git pull --ff-only } | Out-Host
    $commit = "$(git rev-parse HEAD)".Trim()
    $remote = "$(git config branch.main.remote)".Trim()
    # git pull is content when main is ahead; GitHub would only refuse those commits at the very end.
    if ($commit -ne "$(git rev-parse '@{u}')".Trim()) {
        throw "main a des commits absents de GitHub (git log $remote/main..main) : pousse-les ou retire-les avant de publier."
    }

    [xml]$projectXml = Get-Content -LiteralPath (Join-Path $workspace 'src/Mira.Desktop/Mira.Desktop.csproj')
    $version = [string]$projectXml.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version x.y.z attendue dans Mira.Desktop.csproj, trouvé « $version »." }
    $tag = "v$version"
    $short = $commit.Substring(0, 7)
    $draft = Get-ReleaseState $tag
    if ($draft -and -not $draft.StartsWith("true`t")) { throw "La release $tag est déjà publiée sur GitHub : rien à publier. (Pour une nouvelle version, change <Version> dans les csproj et ajoute son entrée à WhatsNew.json.)" }
    # GitHub attaches a release to an existing tag whatever --target says.
    if (Invoke-Retry 'git ls-remote' { git ls-remote --tags $remote "refs/tags/$tag" }) {
        throw "Le tag $tag existe déjà sur GitHub : la release s’y attacherait, quel que soit le commit publié. Supprime-le (git push $remote --delete $tag) ou change de version."
    }
    # The SDK global.json asks for, read once main is up to date: without it, dotnet only says that no compatible SDK
    # was found. Mira.Release needs it for the notes, the key and the signature.
    $sdk = [string](Get-Content -LiteralPath (Join-Path $workspace 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $channel = ($sdk.Split('.')[0..1]) -join '.'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (Test-Native { dotnet --version })) {
        throw "SDK .NET $channel introuvable (global.json demande $sdk ou un SDK $channel plus récent) : installe le SDK .NET $channel pour Windows x64 depuis https://dotnet.microsoft.com/download/dotnet/$channel, puis relance."
    }
    # The key opens on this Windows account and installed copies trust it: checked in seconds, not after the workflows.
    # (Its first build restores packages from NuGet: tried again on a dropped connection.)
    $tool = Join-Path $workspace 'tools/Mira.Release/Mira.Release.csproj'
    if (-not (Test-Native { dotnet build $tool -c Release })) { Start-Sleep -Seconds 10; Invoke-Native 'dotnet build Mira.Release' { dotnet build $tool -c Release } }
    Invoke-Native 'Mira.Release check-key' { dotnet run --project $tool -c Release -- check-key }
    # The release page's text, from the version's entry in WhatsNew.json (Mira.Release stops without one); its "Code
    # signing policy" line credits SignPath only when the Windows files will carry its signature.
    $notes = Join-Path $workspace ".artifacts/release-notes-$version.md"
    Remove-Item -LiteralPath $notes -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path (Split-Path -Parent $notes) -Force | Out-Null
    $notesOptions = @(); if ($Unsigned) { $notesOptions += '--unsigned' }
    # The banner on top of the page (tools/banner), committed by banner.yml on the preparation branch.
    if (Test-Path -LiteralPath (Join-Path $workspace "docs/releases/$version.jpg")) { $notesOptions += '--banner' }
    else { Write-Warning "Pas de bannière pour $version (docs/releases/$version.jpg) : la release paraîtra sans. Elle se fait sur la branche de préparation : tools/banner/scenes.json, puis banner.yml (Run workflow)." }
    Invoke-Native 'Mira.Release notes' { dotnet run --project $tool -c Release -- notes --version $version --out $notes @notesOptions }

    # Only a commit GitHub's Windows build and tests passed (ci.yml, at each push to main) is published.
    Wait-WindowsBuild $commit
    # The last checks of the other platforms on main: they run again in the release and only warn there.
    foreach ($workflow in @('mac.yml', 'ios.yml', 'web.yml')) {
        $last = [string](Get-Answer { gh run list --repo $repository --workflow $workflow --branch main --event push --limit 1 --json conclusion,url --jq '.[] | [.conclusion, .url] | @tsv' })
        $conclusion, $url = $last -split "`t"
        if ($conclusion -and @('success', 'skipped', 'neutral') -notcontains $conclusion) {
            Write-Warning "Le dernier $workflow de main n’a pas réussi ($url) : ses tests ne bloquent pas la release, mais regarde si c’est un vrai problème."
        }
    }

    # Picks up an earlier launch for this very commit and draft: the runs it started stay, only what is missing or
    # failed is run again. Otherwise everything starts afresh.
    $stateFile = Join-Path $workspace ".artifacts/release-$version.json"
    $saved = $null
    if (Test-Path -LiteralPath $stateFile) { try { $saved = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json } catch { $saved = $null } }
    $runIds = @{}
    $resume = $saved -and $draft -eq "true`t$commit" -and $saved.commit -eq $commit -and [bool]$saved.unsigned -eq [bool]$Unsigned
    $branch = "release/$tag"
    if ($resume) {
        $script:ref = [string]$saved.ref
        foreach ($property in @($saved.runs.PSObject.Properties)) {
            if ($workflows -notcontains $property.Name) { continue }
            $run = Get-RunView ([string]$property.Value)
            if ($run -and $run.headSha -eq $commit) { $runIds[$property.Name] = [string]$property.Value }
        }
        Write-Output "Reprise du lancement précédent de $tag sur $short ($($runIds.Count) exécution(s) gardée(s))."
    }
    # The workflows run on a branch of their own at the commit: whatever is merged into main meanwhile, they build and
    # run what was there. Main as before when GitHub refuses that branch.
    if (-not $resume) {
        if (Set-ReleaseBranch $branch $commit) { $script:ref = $branch }
        else {
            $script:ref = 'main'
            Write-Warning "GitHub refuse la branche $branch : les workflows tournent sur main, qui ne doit plus bouger d’ici la publication (ne fusionne rien)."
        }
    }
    elseif ($script:ref -ne 'main' -and -not (Set-ReleaseBranch $branch $commit)) {
        throw "GitHub ne laisse pas remettre la branche $branch sur $short : relance la même commande dans quelques minutes."
    }
    $saveState = {
        New-Item -ItemType Directory -Path (Split-Path -Parent $stateFile) -Force | Out-Null
        [pscustomobject]@{ commit = $commit; unsigned = [bool]$Unsigned; ref = $script:ref; runs = $runIds } | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8
    }

    # A run still going that this launch does not follow (an attempt whose record was lost) would attach its own files:
    # cancelled, and waited for.
    $kept = @($runIds.Values)
    $stray = @(foreach ($workflow in $workflows) { Get-Runs $workflow | Where-Object { $_.Status -ne 'completed' -and $kept -notcontains $_.Id } })
    if ($stray.Count) {
        foreach ($run in $stray) {
            Write-Output "Exécution précédente de $($run.Workflow) arrêtée : $($run.Url)"
            [void](Test-Native { gh run cancel $run.Id --repo $repository })
        }
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            $still = @($stray | Where-Object { $run = Get-RunView $_.Id; -not $run -or $run.status -ne 'completed' })
            if (-not $still.Count) { break }
            if ($attempt -eq 29) { throw "Des exécutions précédentes ne s’arrêtent pas ($(($still | ForEach-Object { $_.Url }) -join ', ')) : annule-les sur GitHub, puis relance." }
            Start-Sleep -Seconds 10
        }
    }

    if (-not $resume) {
        # A draft has no tag and is seen by no one: one left by an earlier launch for another commit (or a -DryRun with
        # other options) is replaced. A published release is not.
        if ($draft) {
            Invoke-Retry 'gh release delete' { gh release delete $tag --repo $repository --yes } { -not (Get-ReleaseState $tag) } | Out-Null
            Write-Output "Brouillon de $tag laissé par un lancement précédent : supprimé."
        }
        Remove-Item -LiteralPath $stateFile -ErrorAction SilentlyContinue
        Invoke-Retry 'gh release create' { gh release create $tag --repo $repository --draft --target $commit --title "Mira $version" --notes-file $notes } { (Get-ReleaseState $tag) -eq "true`t$commit" } | Out-Null
        Write-Output "Brouillon de $tag créé sur $short, sans fichier encore."
    }
    else { Invoke-Retry 'gh release edit (notes)' { gh release edit $tag --repo $repository --title "Mira $version" --notes-file $notes } | Out-Null }
    & $saveState

    # Every platform, built by GitHub from the same commit and attached to the draft: the Windows files, signed by
    # SignPath Foundation unless -Unsigned; the Mac disk image; Mira web; Mira for iPhone.
    foreach ($workflow in $workflows) {
        if ($runIds.ContainsKey($workflow)) { continue }
        $more = @()
        if ($workflow -eq 'windows.yml' -and $Unsigned) { $more = @('-f', 'sign=false') }
        $runIds[$workflow] = Start-ReleaseWorkflow $workflow $tag $commit $more
        & $saveState
        Write-Output "$workflow lancé : https://github.com/$repository/actions/runs/$($runIds[$workflow])"
    }
    if ($Unsigned) { Write-Warning 'Les fichiers Windows de cette release ne seront pas signés (-Unsigned).' }
    else { Write-Output "SignPath va te demander d’approuver deux signatures pendant windows.yml (l’application, puis l’installateur) : https://app.signpath.io, ou le lien de son e-mail. Chacune attend une heure au plus ; le script attend." }
    Write-Output "Les quatre plateformes se construisent (25 à 35 minutes, surtout l’iPhone) ; ce qui échoue est relancé tout seul."
    $runs = Wait-ReleaseRuns $runIds
    foreach ($workflow in $workflows) {
        # Run at the commit of the draft, from the release branch: otherwise not the run to publish.
        $run = $runs[$workflow]
        if ($run.headSha -ne $commit -or $run.headBranch -ne $script:ref) { throw "$workflow a tourné sur « $($run.headBranch) $($run.headSha) », pas sur $script:ref en $short ($($run.url)) : ce n’est pas l’exécution de cette release. Relance la même commande." }
    }

    # What is signed comes from the runs watched above (their artifacts), not from whatever the draft holds now; the
    # draft's copies are then compared with them, digest by digest.
    $packages = Join-Path $workspace 'dist/packages'
    $release = Join-Path $workspace 'dist/release'
    # Apart from the files Mira.Release signs into mira-update.json: the .ipa reaches the iPhone through AltStore.
    $ios = Join-Path $workspace 'dist/ios'
    foreach ($folder in @($packages, $release, $ios)) {
        if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
        New-Item -ItemType Directory -Path $folder | Out-Null
    }
    $names = @("Mira-$version-win-x64-setup.exe", "Mira-$version-win-x64-portable.exe", "Mira-$version-win-x64.zip")
    $windows = @($names | ForEach-Object { $_; "$_.sha256" })
    $mac = @("Mira-$version-mac-arm64.dmg", "Mira-$version-mac-arm64.dmg.sha256")
    $web = @("mira-jellyfin-$version.zip", 'jellyfin-manifest.json')
    $others = $mac + $web
    $iphone = @("Mira-$version-ios.ipa", "Mira-$version-ios.ipa.sha256")
    # Which workflow attaches which file: one missing or different on the draft is attached again by it.
    $attachedBy = @{}
    foreach ($name in $windows) { $attachedBy[$name] = 'windows.yml' }
    foreach ($name in $mac) { $attachedBy[$name] = 'mac.yml' }
    foreach ($name in $web) { $attachedBy[$name] = 'web.yml' }
    foreach ($name in $iphone) { $attachedBy[$name] = 'ios.yml' }
    $artifacts = @{ 'windows.yml' = 'Mira-win-x64-release'; 'mac.yml' = 'Mira-mac-arm64'; 'web.yml' = 'Mira-web-release'; 'ios.yml' = 'Mira-ios' }
    foreach ($workflow in $workflows) {
        $artifact = $artifacts[$workflow]
        $into = switch ($workflow) { 'windows.yml' { $packages } 'ios.yml' { $ios } default { $release } }
        $download = Join-Path $workspace ".artifacts/download/$artifact"
        $id = $runIds[$workflow]
        Invoke-Retry "gh run download ($artifact)" {
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Recurse -Force }
            gh run download $id --repo $repository --name $artifact --dir $download
        } | Out-Null
        Get-ChildItem -LiteralPath $download -File | Copy-Item -Destination $into -Force
    }
    $missing = @($windows | Where-Object { -not (Test-Path -LiteralPath (Join-Path $packages $_)) }) + @($others | Where-Object { -not (Test-Path -LiteralPath (Join-Path $release $_)) }) +
        @($iphone | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ios $_)) })
    if ($missing.Count) { throw "Les exécutions de $tag n’ont pas produit : $($missing -join ', '). Regarde leurs pages ci-dessus, puis relance." }
    # The Authenticode signatures SignPath Foundation put on the installer, the portable Mira and, in the archive,
    # Mira's own files: valid, by that publisher, timestamped.
    if (-not $Unsigned) {
        $checked = @(Expand-Checked (Join-Path $packages "Mira-$version-win-x64.zip") (Join-Path $workspace '.artifacts/release-signed'))
        & (Join-Path $PSScriptRoot 'check-signed.ps1') -Files (@($names[0..1] | ForEach-Object { Join-Path $packages $_ }) + $checked)
    }

    # Signed for every platform, then checked as installed copies will: the keys they trust, every file's size and
    # SHA-256, the .dmg against its .sha256, Jellyfin's manifest against the plugin.
    Invoke-Native 'Mira.Release manifest' { dotnet run --project $tool -c Release -- manifest --version $version --packages $packages --others $release }
    Invoke-Native 'Mira.Release verify' { dotnet run --project $tool -c Release -- verify --version $version --packages $packages --others $release }
    Invoke-Retry 'gh release upload' { gh release upload $tag (Join-Path $packages 'mira-update.json') (Join-Path $packages 'mira-update.json.sig') --repo $repository --clobber } | Out-Null

    # The .ipa against its .sha256 (« <hash>  <name> », as shasum writes it).
    $ipaHash = (Get-FileHash -LiteralPath (Join-Path $ios $iphone[0]) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-Content -LiteralPath (Join-Path $ios $iphone[1]) -Raw).Trim() -ne "$ipaHash  $($iphone[0])") { throw "$($iphone[1]) ne correspond pas à $($iphone[0])." }
    # Exactly the 14 files on the draft made for this commit, each with the SHA-256 of the file checked here; those built
    # by GitHub uploaded by GitHub Actions itself.
    $expected = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
    foreach ($name in $windows + @('mira-update.json', 'mira-update.json.sig')) { $expected[$name] = (Get-FileHash -LiteralPath (Join-Path $packages $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
    foreach ($name in $others) { $expected[$name] = (Get-FileHash -LiteralPath (Join-Path $release $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
    foreach ($name in $iphone) { $expected[$name] = (Get-FileHash -LiteralPath (Join-Path $ios $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
    # A draft has no tag: its assets are read through its API address.
    $api = "$(Invoke-Retry 'gh release view' { gh release view $tag --repo $repository --json apiUrl --jq .apiUrl })".Trim() -replace '^https://api\.github\.com/', ''
    if (-not $api) { throw "gh release view ne donne pas l’adresse du brouillon de $tag." }
    for ($check = 0; ; $check++) {
        $listed = @(Invoke-Retry 'gh api (fichiers du brouillon)' { gh api "$api/assets?per_page=100" --jq '.[] | [.name, .state, .digest, .uploader.login, .id] | @tsv' })
        $problems = New-Object System.Collections.Generic.List[string]
        $again = @{}
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($line in $listed) {
            if (-not $line) { continue }
            $name, $state, $digest, $uploader, $assetId = $line -split "`t"
            if (-not $expected.ContainsKey($name)) {
                # Seen by no one yet: what does not belong goes.
                Write-Output "$name n’appartient pas à $tag : retiré du brouillon."
                Invoke-Retry "gh api (retirer $name)" { gh api -X DELETE "repos/$repository/releases/assets/$assetId" } | Out-Null
                continue
            }
            [void]$seen.Add($name)
            $problem = $null
            if ($digest -ne "sha256:$($expected[$name])" -or $state -ne 'uploaded') { $problem = "$name ($digest, $state) différent de celui vérifié ici (sha256:$($expected[$name]))" }
            elseif ($attachedBy.ContainsKey($name) -and $uploader -ne 'github-actions[bot]') { $problem = "$name envoyé par $uploader, pas par GitHub Actions" }
            if ($problem) { $problems.Add($problem); if ($attachedBy.ContainsKey($name)) { $again[$attachedBy[$name]] = $true } }
        }
        foreach ($name in $expected.Keys) {
            if (-not $seen.Contains($name)) { $problems.Add("$name manquant"); if ($attachedBy.ContainsKey($name)) { $again[$attachedBy[$name]] = $true } }
        }
        if (-not $problems.Count) { break }
        if ($check -ge 1 -or -not $again.Count) { throw "Le brouillon de $tag ne contient pas exactement les fichiers vérifiés : $($problems -join ' ; '). Relance la même commande." }
        # Attached again by the job « release » of their workflow, from the same artifact (its build is not run again).
        Write-Warning "Le brouillon de $tag n’a pas exactement les fichiers vérifiés ($($problems -join ' ; ')) : je les fais envoyer de nouveau."
        $after = @{}; $redo = @{}
        foreach ($workflow in $again.Keys) {
            $job = @($runs[$workflow].jobs | Where-Object { $_.name -eq 'release' }) | Select-Object -First 1
            Invoke-Retry "gh run rerun ($workflow, release)" { gh run rerun --job $job.databaseId --repo $repository } | Out-Null
            $after[$workflow] = [int]$runs[$workflow].attempt + 1
            $redo[$workflow] = $runIds[$workflow]
        }
        $redone = Wait-ReleaseRuns $redo $after
        foreach ($workflow in $redone.Keys) { $runs[$workflow] = $redone[$workflow] }
    }
    if ((Get-ReleaseState $tag) -ne "true`t$commit") { throw "Le brouillon de $tag ne vise plus $commit : relance." }

    if ($DryRun) {
        Write-Output "Tout est prêt, sans publier (-DryRun) : le brouillon de $tag a ses $($expected.Count) fichiers vérifiés et signés, et personne d’autre ne le voit."
        Write-Output "Le prochain lancement (sans -DryRun) le publie sans rien reconstruire ; « gh release delete $tag --repo $repository --yes » le supprime."
        return
    }
    Invoke-Retry 'gh release edit (publication)' { gh release edit $tag --repo $repository --draft=false --latest } { $now = Get-ReleaseState $tag; $now -and -not $now.StartsWith("true`t") } | Out-Null
    Write-Output "Mira $version est publiée, avec ses $($expected.Count) fichiers : https://github.com/$repository/releases/tag/$tag"
    # From here on the release is out: what follows warns, never stops.
    Remove-Item -LiteralPath $stateFile -ErrorAction SilentlyContinue
    if ($script:ref -ne 'main') { [void](Test-Native { gh api -X DELETE "repos/$repository/git/refs/heads/$branch" }) }
    if ("$(Get-Answer { gh api "repos/$repository/releases/tags/$tag" --jq .immutable })".Trim() -ne 'true') {
        Write-Warning "GitHub ne dit pas la release $tag immuable : ses fichiers pourraient encore être remplacés. Vérifie le réglage « Enable release immutability » (https://github.com/$repository/settings)."
    }
    # GitHub signs an attestation of an immutable release's files; gh 2.81 and later can check it. (« gh release <unknown>
    # --help » succeeds too: only the list of subcommands tells.)
    if (((Get-Native { gh release --help }) -join "`n") -match '(?m)^\s+verify\b') {
        if (-not (Test-Native { gh release verify $tag --repo $repository })) { Write-Warning "La release $tag est publiée, mais « gh release verify $tag » a échoué : regarde son attestation sur https://github.com/$repository/releases/tag/$tag." }
        else { Write-Output "Attestation de $tag vérifiée (gh release verify)." }
    }
    else { Write-Output "Cette version de gh ne connaît pas « gh release verify » (gh 2.81 ou plus récent) : attestation de la release non vérifiée." }
    # The Homebrew tap (sasou-web/homebrew-mira) checks for a new release every 3 hours; asked now, it takes it in minutes.
    if (Test-Native { gh workflow run bump.yml --repo sasou-web/homebrew-mira }) { Write-Output 'Homebrew : le cask passe à cette version dans quelques minutes (bump.yml lancé).' }
    else { Write-Warning "Homebrew : bump.yml du tap ne se lance pas d’ici ; il prendra cette version de lui-même dans les 3 heures." }
} finally {
    Pop-Location
}
