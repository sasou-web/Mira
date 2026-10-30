param([switch]$Unsigned, [switch]$DryRun)
# Publishes the version in src/Mira.Desktop/Mira.Desktop.csproj in one command, on the PC that holds the signing key:
#   1. checks gh, immutable releases on the repository, the signing key (it opens, installed copies trust it), a clean
#      checkout with no untracked file, then brings main up to date (fast-forward only) and checks that GitHub's
#      Windows build (ci.yml) passed on it;
#   2. creates the release vX.Y.Z as a draft on that commit, with the notes from WhatsNew.json and no file yet: a draft
#      is seen by no one and has no tag yet;
#   3. runs windows.yml, mac.yml, web.yml and ios.yml on main for that draft, which build from the same commit and
#      attach the Windows files (built by GitHub, never on this PC, and signed by SignPath Foundation: two signing
#      requests to approve in SignPath while the script waits), the .dmg, mira-jellyfin-X.Y.Z.zip and
#      jellyfin-manifest.json, the iPhone's unsigned .ipa (AltStore signs it on the iPhone), with their .sha256, and
#      waits for all four;
#   4. takes those files from the runs it watched (their artifacts), checks them (Mira.Release, the checks Mira for
#      Windows makes too, and the Authenticode signatures of the Windows executables), signs mira-update.json for every
#      platform, checks that signature against the keys installed copies trust, and attaches it;
#   5. checks that the draft holds exactly the 14 expected files, each with the SHA-256 of the file checked here (all
#      but the manifest uploaded by GitHub Actions itself), then publishes it: the release appears whole, at once, with
#      the notes from WhatsNew.json, and checks that GitHub made it immutable.
# -Unsigned has windows.yml skip SignPath (Windows files without Authenticode signature: before SignPath accepts the
# project, or while it is down). -DryRun does all of it but the publication: the draft stays, unseen, and the next run
# replaces it.
# Works in Windows PowerShell 5.1 and PowerShell 7 (saved with a BOM; messages with an apostrophe in double quotes:
# PowerShell reads a typographic apostrophe as a single quote). jq expressions passed to gh hold no double quote: Windows
# PowerShell drops them from a native command's arguments.
$ErrorActionPreference = 'Stop'
# The .NET SDK sends usage data unless told not to; the scripts tell it, on this PC as in the CI.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repository = 'sasou-web/Mira'
$workflows = @('windows.yml', 'web.yml', 'mac.yml', 'ios.yml')

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
    $lines = @(gh run list --repo $repository --workflow $workflow --branch main --event workflow_dispatch --limit 30 --json databaseId,status,url --jq '.[] | [.databaseId, .status, .url] | @tsv')
    if ($LASTEXITCODE -ne 0) { throw "gh ne peut pas lister les exécutions de $workflow (code $LASTEXITCODE)." }
    foreach ($line in $lines) {
        if (-not $line) { continue }
        $id, $status, $url = $line -split "`t"
        [pscustomobject]@{ Workflow = $workflow; Id = $id; Status = $status; Url = $url }
    }
}
# Runs $workflow on main for the draft (with $more, other "-f name=value" inputs) and returns that run: the one gh
# names, or else the first new one in the list.
function Start-ReleaseWorkflow([string]$workflow, [string]$tag, [string]$commit, [string[]]$more = @()) {
    $known = @(Get-Runs $workflow | ForEach-Object { $_.Id })
    $printed = @(gh workflow run $workflow --repo $repository --ref main -f "release=$tag" -f "commit=$commit" @more)
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
    if (-not (Test-Native { gh api user --silent })) { throw "gh ne peut pas se servir de ton compte GitHub (ou GitHub ne répond pas) : lance « gh auth login »." }
    # Immutable releases: once published, neither the files nor the tag of a release can change, even from this account.
    # Mira for Windows has Jellyfin install Mira web from one release, checked once: without them, it could be swapped
    # afterwards. GitHub answers 200 with "enabled" (its documentation also announces a 404 when they are off).
    if ((Get-Native { gh api "repos/$repository/immutable-releases" --jq .enabled }) -ne 'true') {
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
        throw "ci.yml n’a pas réussi pour $short ($($checks[0][2])). Si c’était un accident (réseau, machine de GitHub), relance-le sur cette page (« Re-run failed jobs ») ; sinon corrige main. Puis relance ce script."
    }

    # The SDK global.json asks for, read once main is up to date: without it, dotnet only says that no compatible SDK
    # was found. Mira.Release needs it for the notes, the key and the signature.
    $sdk = [string](Get-Content -LiteralPath (Join-Path $workspace 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $channel = ($sdk.Split('.')[0..1]) -join '.'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not (Test-Native { dotnet --version })) {
        throw "SDK .NET $channel introuvable (global.json demande $sdk ou un SDK $channel plus récent) : installe le SDK .NET $channel pour Windows x64 depuis https://dotnet.microsoft.com/download/dotnet/$channel, puis relance."
    }
    # The key opens on this Windows account and installed copies trust it: checked in seconds, not after the workflows.
    $tool = Join-Path $workspace 'tools/Mira.Release/Mira.Release.csproj'
    Invoke-Native 'Mira.Release check-key' { dotnet run --project $tool -c Release -- check-key }
    # The release page's text, from the version's entry in WhatsNew.json (Mira.Release stops without one); its "Code
    # signing policy" line credits SignPath only when the Windows files will carry its signature.
    $notes = Join-Path $workspace ".artifacts/release-notes-$version.md"
    Remove-Item -LiteralPath $notes -ErrorAction SilentlyContinue
    $notesOptions = @(); if ($Unsigned) { $notesOptions += '--unsigned' }
    # The banner on top of the page (tools/banner), committed by banner.yml on the preparation branch.
    if (Test-Path -LiteralPath (Join-Path $workspace "docs/releases/$version.jpg")) { $notesOptions += '--banner' }
    else { Write-Warning "Pas de bannière pour $version (docs/releases/$version.jpg) : la release paraîtra sans. Elle se fait sur la branche de préparation : tools/banner/scenes.json, puis banner.yml (Run workflow)." }
    Invoke-Native 'Mira.Release notes' { dotnet run --project $tool -c Release -- notes --version $version --out $notes @notesOptions }

    # A run still going from an earlier attempt would attach its own files to the new draft.
    foreach ($workflow in $workflows) {
        $running = @(Get-Runs $workflow | Where-Object { $_.Status -ne 'completed' })
        if ($running.Count) { throw "Une exécution précédente de $workflow tourne encore ($($running[0].Url)) : attends sa fin ou annule-la (gh run cancel $($running[0].Id) --repo $repository), puis relance." }
    }
    if ($draft) {
        Invoke-Native 'gh release delete' { gh release delete $tag --repo $repository --yes }
        Write-Output "Brouillon de $tag laissé par un lancement précédent : supprimé."
    }
    Invoke-Native 'gh release create' { gh release create $tag --repo $repository --draft --target $commit --title "Mira $version" --notes-file $notes }
    Write-Output "Brouillon de $tag créé sur $short, sans fichier encore."

    # Every platform, built by GitHub from the same commit and attached to the draft: the Windows files, signed by
    # SignPath Foundation unless -Unsigned; the Mac disk image; Mira web; Mira for iPhone.
    $runs = @()
    foreach ($workflow in $workflows) {
        $more = @()
        if ($workflow -eq 'windows.yml' -and $Unsigned) { $more = @('-f', 'sign=false') }
        $run = Start-ReleaseWorkflow $workflow $tag $commit $more
        $runs += $run
        Write-Output "$($run.Workflow) : $($run.Url)"
    }
    if ($Unsigned) { Write-Warning 'Les fichiers Windows de cette release ne seront pas signés (-Unsigned).' }
    else { Write-Output "SignPath va te demander d’approuver deux signatures pendant windows.yml (l’application, puis l’installateur) : https://app.signpath.io, ou le lien de son e-mail. Chacune attend une heure au plus ; le script attend." }
    foreach ($run in $runs) {
        gh run watch $run.Id --repo $repository --exit-status --interval 30
        if ($LASTEXITCODE -ne 0) { throw "$($run.Workflow) a échoué : $($run.Url). Corrige, puis relance." }
        # Run on main, at the commit of the draft: main moved meanwhile, and the run is not the one to sign.
        $head = ([string](Get-Native { gh run view $run.Id --repo $repository --json headSha,headBranch --jq '[.headSha, .headBranch] | @tsv' })).Trim()
        if ($head -ne "$commit`tmain") { throw "$($run.Workflow) a tourné sur « $head », pas sur main en $short ($($run.Url)) : main a changé pendant la publication. Relance." }
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
    $others = @("Mira-$version-mac-arm64.dmg", "Mira-$version-mac-arm64.dmg.sha256", "mira-jellyfin-$version.zip", 'jellyfin-manifest.json')
    $iphone = @("Mira-$version-ios.ipa", "Mira-$version-ios.ipa.sha256")
    $artifacts = @{ 'windows.yml' = 'Mira-win-x64-release'; 'mac.yml' = 'Mira-mac-arm64'; 'web.yml' = 'Mira-web-release'; 'ios.yml' = 'Mira-ios' }
    foreach ($run in $runs) {
        $artifact = $artifacts[$run.Workflow]
        $into = switch ($run.Workflow) { 'windows.yml' { $packages } 'ios.yml' { $ios } default { $release } }
        Invoke-Native "gh run download ($artifact)" { gh run download $run.Id --repo $repository --name $artifact --dir $into }
    }
    $missing = @($windows | Where-Object { -not (Test-Path -LiteralPath (Join-Path $packages $_)) }) + @($others | Where-Object { -not (Test-Path -LiteralPath (Join-Path $release $_)) }) +
        @($iphone | Where-Object { -not (Test-Path -LiteralPath (Join-Path $ios $_)) })
    if ($missing.Count) { throw "Les exécutions de $tag n’ont pas produit : $($missing -join ', '). Regarde les exécutions ci-dessus, puis relance." }
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
    Invoke-Native 'gh release upload' { gh release upload $tag (Join-Path $packages 'mira-update.json') (Join-Path $packages 'mira-update.json.sig') --repo $repository --clobber }

    # The .ipa against its .sha256 (« <hash>  <name> », as shasum writes it).
    $ipaHash = (Get-FileHash -LiteralPath (Join-Path $ios $iphone[0]) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-Content -LiteralPath (Join-Path $ios $iphone[1]) -Raw).Trim() -ne "$ipaHash  $($iphone[0])") { throw "$($iphone[1]) ne correspond pas à $($iphone[0])." }
    # Exactly the 14 files on the draft made for this commit, each with the SHA-256 of the file checked here; those built
    # by GitHub uploaded by GitHub Actions itself.
    $expected = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
    foreach ($name in $windows + @('mira-update.json', 'mira-update.json.sig')) { $expected[$name] = (Get-FileHash -LiteralPath (Join-Path $packages $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
    foreach ($name in $others) { $expected[$name] = (Get-FileHash -LiteralPath (Join-Path $release $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
    foreach ($name in $iphone) { $expected[$name] = (Get-FileHash -LiteralPath (Join-Path $ios $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
    $byActions = $windows + $others + $iphone
    # A draft has no tag: its assets are read through its API address.
    $api = ([string](Get-Native { gh release view $tag --repo $repository --json apiUrl --jq .apiUrl })).Trim() -replace '^https://api\.github\.com/', ''
    if (-not $api) { throw "gh release view a échoué pour $tag." }
    $listed = @(gh api "$api/assets?per_page=100" --jq '.[] | [.name, .state, .digest, .uploader.login] | @tsv')
    if ($LASTEXITCODE -ne 0) { throw "gh api $api/assets a échoué (code $LASTEXITCODE)." }
    $problems = New-Object System.Collections.Generic.List[string]
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($line in $listed) {
        if (-not $line) { continue }
        $name, $state, $digest, $uploader = $line -split "`t"
        [void]$seen.Add($name)
        if (-not $expected.ContainsKey($name)) { $problems.Add("$name en trop") }
        elseif ($digest -ne "sha256:$($expected[$name])" -or $state -ne 'uploaded') { $problems.Add("$name ($digest, $state) différent de celui vérifié ici (sha256:$($expected[$name]))") }
        elseif ($byActions -contains $name -and $uploader -ne 'github-actions[bot]') { $problems.Add("$name envoyé par $uploader, pas par GitHub Actions") }
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
    # From here on the release is out: what follows warns, never stops.
    if (([string](Get-Native { gh api "repos/$repository/releases/tags/$tag" --jq .immutable })).Trim() -ne 'true') {
        Write-Warning "GitHub ne dit pas la release $tag immuable : ses fichiers pourraient encore être remplacés. Vérifie le réglage « Enable release immutability » (https://github.com/$repository/settings)."
    }
    # GitHub signs an attestation of an immutable release's files; gh 2.81 and later can check it. (« gh release <unknown>
    # --help » succeeds too: only the list of subcommands tells.)
    if (((Get-Native { gh release --help }) -join "`n") -match '(?m)^\s+verify\b') {
        $ErrorActionPreference = 'Continue'
        gh release verify $tag --repo $repository
        if ($LASTEXITCODE -ne 0) { Write-Warning "La release $tag est publiée, mais « gh release verify $tag » a échoué : regarde son attestation sur https://github.com/$repository/releases/tag/$tag." }
        $ErrorActionPreference = 'Stop'
    }
    else { Write-Output "Cette version de gh ne connaît pas « gh release verify » (gh 2.81 ou plus récent) : attestation de la release non vérifiée." }
} finally {
    Pop-Location
}
