param([switch]$SkipBuild, [switch]$DryRun)
# Publishes the version in src/Mira.Desktop/Mira.Desktop.csproj in one command, on the PC that holds the signing key:
#   1. checks gh, the signing key, Inno Setup, a clean checkout with no untracked file (it would be built or embedded),
#      then brings main up to date (fast-forward only) and checks that GitHub's Windows build (ci.yml) passed on it;
#   2. builds the Windows packages (tools/package.ps1), unless -SkipBuild uses those this script built from the same
#      commit (a -DryRun, an attempt that stopped later);
#   3. creates the release vX.Y.Z as a draft on that commit, with the Windows files and their .sha256: a draft is seen
#      by no one and has no tag yet;
#   4. runs mac.yml and web.yml for that draft, which build from the same commit and attach the .dmg and its .sha256,
#      mira-jellyfin-X.Y.Z.zip and jellyfin-manifest.json, and waits for both;
#   5. downloads those files, checks them (Mira.Release, the checks Mira for Windows makes too), signs mira-update.json
#      for every platform, checks that signature against the keys installed copies trust, and attaches it;
#   6. checks that the draft holds exactly the 12 expected files, at their sizes here, then publishes it: the release
#      appears whole, at once, with the notes from WhatsNew.json.
# -DryRun does all of it but the publication: the draft stays, unseen, and the next run replaces it.
# Works in Windows PowerShell 5.1 and PowerShell 7 (saved with a BOM; messages with an apostrophe in double quotes:
# PowerShell reads a typographic apostrophe as a single quote). jq expressions passed to gh hold no double quote: Windows
# PowerShell drops them from a native command's arguments.
$ErrorActionPreference = 'Stop'
# The .NET SDK sends usage data unless told not to; the scripts tell it, on this PC as in the CI.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repository = 'sasou-web/Mira'
$workflows = @('web.yml', 'mac.yml')

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
# The runs of a workflow started by hand (workflow_dispatch), newest first: id, status and page.
function Get-Runs([string]$workflow) {
    $lines = @(gh run list --repo $repository --workflow $workflow --event workflow_dispatch --limit 30 --json databaseId,status,url --jq '.[] | [.databaseId, .status, .url] | @tsv')
    if ($LASTEXITCODE -ne 0) { throw "gh ne peut pas lister les exécutions de $workflow (code $LASTEXITCODE)." }
    foreach ($line in $lines) {
        if (-not $line) { continue }
        $id, $status, $url = $line -split "`t"
        [pscustomobject]@{ Workflow = $workflow; Id = $id; Status = $status; Url = $url }
    }
}
# Runs $workflow on main for the draft and returns that run: the one gh names, or else the first new one in the list.
function Start-ReleaseWorkflow([string]$workflow, [string]$tag, [string]$commit) {
    $known = @(Get-Runs $workflow | ForEach-Object { $_.Id })
    $printed = @(gh workflow run $workflow --repo $repository --ref main -f "release=$tag" -f "commit=$commit")
    if ($LASTEXITCODE -ne 0) { throw "gh workflow run $workflow a échoué (code $LASTEXITCODE)." }
    $named = $null
    if (($printed -join ' ') -match '/actions/runs/(\d+)') { $named = $Matches[1] }
    # GitHub lists a dispatched run a few seconds later.
    for ($attempt = 0; $attempt -lt 36; $attempt++) {
        $new = @(Get-Runs $workflow | Where-Object { if ($named) { $_.Id -eq $named } else { $known -notcontains $_.Id } })
        if ($new.Count) { return $new[-1] }
        Start-Sleep -Seconds 5
    }
    throw "GitHub ne montre pas l’exécution de $workflow lancée pour $tag : regarde https://github.com/$repository/actions, puis relance."
}
# Whether the release is a draft, and its commit, as "true<tab><commit>"; empty when there is no release by that name.
function Get-ReleaseState([string]$tag) {
    [string](Get-Native { gh release view $tag --repo $repository --json isDraft,targetCommitish --jq '[(.isDraft | tostring), .targetCommitish] | @tsv' })
}

Push-Location $workspace
try {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI (gh) introuvable : installe-le (https://cli.github.com), puis lance « gh auth login »."
    }
    # The account gh will use, only (gh auth status also fails for another, stale account or host).
    if (-not (Test-Native { gh api user --silent })) { throw "gh ne peut pas se servir de ton compte GitHub (ou GitHub ne répond pas) : lance « gh auth login »." }
    # mira-update.json is signed at the end, once the workflows have built the other platforms: the key is needed even
    # with -SkipBuild. A new key would be refused by every installed copy: a lost one comes back from its backup.
    $key = if ($env:APPDATA) { Join-Path $env:APPDATA 'Mira Release\update-signing.key' } else { '%APPDATA%\Mira Release\update-signing.key' }
    if (-not ($env:APPDATA -and (Test-Path -LiteralPath $key))) {
        throw "Clé de signature introuvable ($key) : sans elle, les copies installées ne se mettraient pas à jour. Restaure-la avec « dotnet run --project tools/Mira.Release -c Release -- import-backup --in <copie> » ; ne lance pas « keygen » : une nouvelle clé serait refusée par toutes les copies installées."
    }
    if (-not $SkipBuild) {
        $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
            Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
        if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) introuvable : il construit l’installateur, Mira-X.Y.Z-win-x64-setup.exe." }
    }

    # What is published is main as it is on GitHub, nothing else.
    if (git status --porcelain --untracked-files=no) { throw 'Des fichiers suivis sont modifiés : valide-les ou mets-les de côté (git stash) avant de publier.' }
    # The packages are built from this folder: a file git does not know would be compiled or embedded all the same.
    $untracked = @(git ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw "git ls-files a échoué (code $LASTEXITCODE)." }
    if ($untracked.Count) {
        $shown = ($untracked | Select-Object -First 15) -join ', '
        if ($untracked.Count -gt 15) { $shown += " (et $($untracked.Count - 15) autres)" }
        throw "Fichiers absents de git, qui seraient compilés ou embarqués dans la release : $shown. Valide-les, déplace-les ou supprime-les avant de publier."
    }
    Invoke-Native 'git checkout main' { git checkout main }
    Invoke-Native 'git pull' { git pull --ff-only }
    $commit = ([string](git rev-parse HEAD)).Trim()
    $remote = ([string](git config branch.main.remote)).Trim()
    # git pull is content when main is ahead; GitHub would only refuse those commits at the very end.
    if ($commit -ne ([string](git rev-parse '@{u}')).Trim()) {
        throw "main a des commits absents de GitHub (git log $remote/main..main) : pousse-les ou retire-les avant de publier."
    }

    [xml]$projectXml = Get-Content -LiteralPath (Join-Path $workspace 'src/Mira.Desktop/Mira.Desktop.csproj')
    $version = [string]$projectXml.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version x.y.z attendue dans Mira.Desktop.csproj, trouvé « $version »." }
    $tag = "v$version"
    # A draft has no tag and is seen by no one: one left by an earlier run (a -DryRun, an attempt that stopped) is
    # replaced below. A published release is not.
    $draft = Get-ReleaseState $tag
    if ($draft -and -not $draft.StartsWith("true`t")) { throw "La release $tag est déjà publiée sur GitHub : rien à publier." }
    # GitHub attaches a release to an existing tag whatever --target says.
    if (Get-Native { git ls-remote --tags $remote "refs/tags/$tag" }) {
        throw "Le tag $tag existe déjà sur GitHub : la release s’y attacherait, quel que soit le commit publié. Supprime-le (git push $remote --delete $tag) ou change de version."
    }
    # The Windows build and tests GitHub ran on this very commit (ci.yml, at each push to main): only a commit they
    # passed is published.
    $short = $commit.Substring(0, 7)
    $checks = @(gh run list --repo $repository --workflow ci.yml --commit $commit --event push --limit 10 --json status,conclusion,url --jq '.[] | [.status, .conclusion, .url] | @tsv')
    if ($LASTEXITCODE -ne 0) { throw "gh ne peut pas lister les exécutions de ci.yml (code $LASTEXITCODE)." }
    $checks = @($checks | Where-Object { $_ } | ForEach-Object { , ($_ -split "`t") })
    if (-not @($checks | Where-Object { $_[1] -eq 'success' }).Count) {
        if (-not $checks.Count) { throw "GitHub n’a pas encore construit $short (ci.yml, après le push sur main) : attends que https://github.com/$repository/actions/workflows/ci.yml le montre, puis relance." }
        if ($checks[0][0] -ne 'completed') { throw "ci.yml tourne encore pour $short ($($checks[0][2])) : attends sa réussite, puis relance." }
        throw "ci.yml n’a pas réussi pour $short ($($checks[0][2])) : corrige main avant de publier."
    }

    $packages = Join-Path $workspace 'dist/packages'
    $built = Join-Path $packages 'source-commit.txt'
    $notes = Join-Path $workspace ".artifacts/release-notes-$version.md"
    if (-not $SkipBuild) {
        # The SDK global.json asks for, read once main is up to date: without it, dotnet only says that no compatible SDK
        # was found.
        $sdk = [string](Get-Content -LiteralPath (Join-Path $workspace 'global.json') -Raw | ConvertFrom-Json).sdk.version
        $channel = ($sdk.Split('.')[0..1]) -join '.'
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (Test-Native { dotnet --version })) {
            throw "SDK .NET $channel introuvable (global.json demande $sdk ou un SDK $channel plus récent) : installe le SDK .NET $channel pour Windows x64 depuis https://dotnet.microsoft.com/download/dotnet/$channel, puis relance."
        }
        Remove-Item -LiteralPath $built, $notes -ErrorAction SilentlyContinue
        & (Join-Path $PSScriptRoot 'package.ps1')
        Set-Content -LiteralPath $built -Value $commit -Encoding ascii
    } elseif (-not (Test-Path -LiteralPath $built) -or ([string](Get-Content -LiteralPath $built)).Trim() -ne $commit) {
        throw "Les paquets de dist/packages ne sont pas ceux de main ($short) : relance sans -SkipBuild."
    }

    $names = @("Mira-$version-win-x64-setup.exe", "Mira-$version-win-x64-portable.exe", "Mira-$version-win-x64.zip")
    $windows = @($names | ForEach-Object { $_; "$_.sha256" })
    $missing = @($windows | Where-Object { -not (Test-Path -LiteralPath (Join-Path $packages $_)) })
    if ($missing.Count) { throw "Fichiers manquants dans dist/packages : $($missing -join ', ')." }
    # Checked before the workflows' long wait: a build stopped halfway leaves new files beside older digests.
    foreach ($name in $names) {
        $path = Join-Path $packages $name
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if (([string](Get-Content -LiteralPath "$path.sha256")).Trim().Split(' ')[0] -ne $hash) { throw "$name.sha256 ne correspond pas à $name : relance sans -SkipBuild." }
    }
    if (-not (Test-Path -LiteralPath $notes)) { throw "Notes de version introuvables ($notes) : l’entrée $version manque dans src/Mira.Core/WhatsNew.json." }

    # Immutable releases: once published, neither the files nor the tag can change, even from a stolen account. Read
    # only; GitHub answers 200 with "enabled" (its documentation also announces a 404 when they are off).
    $immutable = (Get-Native { gh api "repos/$repository/immutable-releases" --jq .enabled }) -eq 'true'
    if (-not $immutable) {
        Write-Output "Les releases immuables ne sont pas activées sur $repository (ou gh ne peut pas le lire) : une release publiée reste modifiable. Pour les activer, une fois : Settings → General → Releases → « Enable release immutability », ou « gh api -X PUT repos/$repository/immutable-releases »."
    }

    # A run still going from an earlier attempt would attach its own files to the new draft.
    foreach ($workflow in $workflows) {
        $running = @(Get-Runs $workflow | Where-Object { $_.Status -ne 'completed' })
        if ($running.Count) { throw "Une exécution précédente de $workflow tourne encore ($($running[0].Url)) : attends sa fin ou annule-la (gh run cancel $($running[0].Id) --repo $repository), puis relance." }
    }
    if ($draft) {
        Invoke-Native 'gh release delete' { gh release delete $tag --repo $repository --yes }
        Write-Output "Brouillon de $tag laissé par un lancement précédent : supprimé."
    }
    $arguments = @('release', 'create', $tag) + @($windows | ForEach-Object { Join-Path $packages $_ }) + @('--repo', $repository, '--draft', '--target', $commit, '--title', "Mira $version", '--notes-file', $notes)
    Invoke-Native 'gh release create' { gh @arguments }
    Write-Output "Brouillon de $tag créé sur $short, avec les fichiers Windows."

    # The Mac disk image and Mira web, built by GitHub from the same commit and attached to the draft.
    $runs = @($workflows | ForEach-Object { Start-ReleaseWorkflow $_ $tag $commit })
    foreach ($run in $runs) {
        Write-Output "$($run.Workflow) : $($run.Url)"
        gh run watch $run.Id --repo $repository --exit-status --interval 30
        if ($LASTEXITCODE -ne 0) { throw "$($run.Workflow) a échoué : $($run.Url). Corrige, puis relance (avec -SkipBuild, les paquets Windows sont repris)." }
    }

    $release = Join-Path $workspace 'dist/release'
    if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
    New-Item -ItemType Directory -Path $release | Out-Null
    $others = @("Mira-$version-mac-arm64.dmg", "Mira-$version-mac-arm64.dmg.sha256", "mira-jellyfin-$version.zip", 'jellyfin-manifest.json')
    $patterns = @($others | ForEach-Object { '--pattern'; $_ })
    Invoke-Native 'gh release download' { gh release download $tag --repo $repository --dir $release @patterns }
    $missing = @($others | Where-Object { -not (Test-Path -LiteralPath (Join-Path $release $_)) })
    if ($missing.Count) { throw "Le brouillon de $tag n’a pas reçu : $($missing -join ', '). Regarde les exécutions ci-dessus, puis relance." }

    # Signed for every platform, then checked as installed copies will: the keys they trust, every file's size and
    # SHA-256, the .dmg against its .sha256, Jellyfin's manifest against the plugin.
    $tool = Join-Path $workspace 'tools/Mira.Release/Mira.Release.csproj'
    Invoke-Native 'Mira.Release manifest' { dotnet run --project $tool -c Release -- manifest --version $version --packages $packages --others $release }
    Invoke-Native 'Mira.Release verify' { dotnet run --project $tool -c Release -- verify --version $version --packages $packages --others $release }
    Invoke-Native 'gh release upload' { gh release upload $tag (Join-Path $packages 'mira-update.json') (Join-Path $packages 'mira-update.json.sig') --repo $repository --clobber }

    # Exactly the 12 files, each at the size checked here, on the draft made for this commit.
    $expected = New-Object 'System.Collections.Generic.Dictionary[string,long]' ([StringComparer]::Ordinal)
    foreach ($name in $windows + @('mira-update.json', 'mira-update.json.sig')) { $expected[$name] = (Get-Item -LiteralPath (Join-Path $packages $name)).Length }
    foreach ($name in $others) { $expected[$name] = (Get-Item -LiteralPath (Join-Path $release $name)).Length }
    $listed = @(gh release view $tag --repo $repository --json assets --jq '.assets[] | [.name, .size, .state] | @tsv')
    if ($LASTEXITCODE -ne 0) { throw "gh release view a échoué (code $LASTEXITCODE)." }
    $problems = New-Object System.Collections.Generic.List[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($line in $listed) {
        if (-not $line) { continue }
        $name, $size, $state = $line -split "`t"
        [void]$seen.Add($name)
        if (-not $expected.ContainsKey($name)) { $problems.Add("$name en trop") }
        elseif ([long]$size -ne $expected[$name] -or $state -ne 'uploaded') { $problems.Add("$name ($size octets, $state) différent de celui vérifié ici ($($expected[$name]) octets)") }
    }
    foreach ($name in $expected.Keys) { if (-not $seen.Contains($name)) { $problems.Add("$name manquant") } }
    if ($problems.Count) { throw "Le brouillon de $tag ne contient pas exactement les fichiers vérifiés : $($problems -join ' ; ')." }
    if ((Get-ReleaseState $tag) -ne "true`t$commit") { throw "Le brouillon de $tag ne vise plus $commit : relance." }

    if ($DryRun) {
        Write-Output "Tout est prêt, sans publier (-DryRun) : le brouillon de $tag a ses $($expected.Count) fichiers vérifiés et signés, et personne d’autre ne le voit."
        Write-Output "Le prochain lancement le remplace ; « gh release delete $tag --repo $repository --yes » le supprime."
        return
    }
    Invoke-Native 'gh release edit' { gh release edit $tag --repo $repository --draft=false --latest }
    Write-Output "Mira $version est publiée, avec ses $($expected.Count) fichiers : https://github.com/$repository/releases/tag/$tag"
    if ($immutable) {
        # GitHub signs an attestation of an immutable release's files; gh 2.81 and later can check it.
        if (Test-Native { gh release verify --help }) { Invoke-Native 'gh release verify' { gh release verify $tag --repo $repository } }
        else { Write-Output "Cette version de gh ne connaît pas « gh release verify » (gh 2.81 ou plus récent) : attestation de la release non vérifiée." }
    }
} finally {
    Pop-Location
}
