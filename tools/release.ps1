param([switch]$SkipBuild, [switch]$DryRun)
# Publishes the version in src/Mira.Desktop/Mira.Desktop.csproj in one command, on the PC that holds the signing key:
#   1. checks gh, the signing key, Inno Setup and a clean checkout, then brings main up to date (fast-forward only);
#   2. builds and signs the packages (tools/package.ps1), unless -SkipBuild uses those this script built from the same
#      commit (a -DryRun, an attempt that stopped later);
#   3. checks that the signed manifest describes those very files, then creates the GitHub release vX.Y.Z on that
#      commit, with its 8 files and the notes from WhatsNew.json.
# The workflows then add Mira-X.Y.Z-mac-arm64.dmg, mira-jellyfin-X.Y.Z.zip and jellyfin-manifest.json by themselves.
# -DryRun checks everything and prints the release command instead of running it.
# Works in Windows PowerShell 5.1 and PowerShell 7 (saved with a BOM; messages with an apostrophe in double quotes:
# PowerShell reads a typographic apostrophe as a single quote).
$ErrorActionPreference = 'Stop'
# The .NET SDK sends usage data unless told not to; the scripts tell it, on this PC as in the CI.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repository = 'sasou-web/Mira'

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

Push-Location $workspace
try {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI (gh) introuvable : installe-le (https://cli.github.com), puis lance « gh auth login »."
    }
    # The account gh will use, only (gh auth status also fails for another, stale account or host).
    if (-not (Test-Native { gh api user --silent })) { throw "gh ne peut pas se servir de ton compte GitHub (ou GitHub ne répond pas) : lance « gh auth login »." }
    if (-not $SkipBuild) {
        $key = if ($env:APPDATA) { Join-Path $env:APPDATA 'Mira Release\update-signing.key' } else { '%APPDATA%\Mira Release\update-signing.key' }
        if (-not ($env:APPDATA -and (Test-Path -LiteralPath $key))) {
            throw "Clé de signature introuvable ($key) : sans elle, les copies installées ne se mettraient pas à jour."
        }
        $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
            Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
        if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) introuvable : il construit l’installateur, Mira-X.Y.Z-win-x64-setup.exe." }
    }

    # What is published is main as it is on GitHub, nothing else.
    if (git status --porcelain --untracked-files=no) { throw 'Des fichiers suivis sont modifiés : valide-les ou mets-les de côté (git stash) avant de publier.' }
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
    # gh creates a release as a draft, uploads its files, then publishes it: an upload cut short leaves a draft, which
    # has no tag and is seen by no one. It is replaced below; a published release is not.
    $draft = Get-Native { gh release view $tag --repo $repository --json isDraft --jq .isDraft }
    if ($draft -and $draft -ne 'true') { throw "La release $tag est déjà publiée sur GitHub : rien à publier." }
    # GitHub attaches a release to an existing tag whatever --target says, and the workflows build from that tag.
    if (Get-Native { git ls-remote --tags $remote "refs/tags/$tag" }) {
        throw "Le tag $tag existe déjà sur GitHub : la release s’y attacherait, quel que soit le commit publié. Supprime-le (git push $remote --delete $tag) ou change de version."
    }

    $packages = Join-Path $workspace 'dist/packages'
    $built = Join-Path $packages 'source-commit.txt'
    $notes = Join-Path $workspace ".artifacts/release-notes-$version.md"
    if (-not $SkipBuild) {
        Remove-Item -LiteralPath $built, $notes -ErrorAction SilentlyContinue
        & (Join-Path $PSScriptRoot 'package.ps1')
        Set-Content -LiteralPath $built -Value $commit -Encoding ascii
    } elseif (-not (Test-Path -LiteralPath $built) -or ([string](Get-Content -LiteralPath $built)).Trim() -ne $commit) {
        throw "Les paquets de dist/packages ne sont pas ceux de main ($($commit.Substring(0, 7))) : relance sans -SkipBuild."
    }

    $names = @("Mira-$version-win-x64-setup.exe", "Mira-$version-win-x64-portable.exe", "Mira-$version-win-x64.zip")
    $files = @(@($names | ForEach-Object { $_; "$_.sha256" }) + @('mira-update.json', 'mira-update.json.sig') | ForEach-Object { Join-Path $packages $_ })
    $missing = @($files | Where-Object { -not (Test-Path -LiteralPath $_) })
    if ($missing.Count) { throw "Fichiers manquants dans dist/packages : $(($missing | ForEach-Object { Split-Path $_ -Leaf }) -join ', ')." }
    # Installed copies refuse a file whose size or SHA-256 is not the signed one: a build stopped halfway leaves new
    # files beside the previous manifest.
    $manifest = Get-Content -LiteralPath (Join-Path $packages 'mira-update.json') -Raw | ConvertFrom-Json
    foreach ($name in $names) {
        $path = Join-Path $packages $name
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $entry = @($manifest.files | Where-Object { $_.name -eq $name })
        $listed = ([string](Get-Content -LiteralPath "$path.sha256")).Trim().Split(' ')[0]
        if ($manifest.version -ne $version -or $entry.Count -ne 1 -or [long]$entry[0].size -ne (Get-Item -LiteralPath $path).Length -or $entry[0].sha256 -ne $hash -or $listed -ne $hash) {
            throw "mira-update.json ou $name.sha256 ne correspond pas à $name : relance sans -SkipBuild."
        }
    }
    if (-not (Test-Path -LiteralPath $notes)) { throw "Notes de version introuvables ($notes) : l’entrée $version manque dans src/Mira.Core/WhatsNew.json." }

    $arguments = @('release', 'create', $tag) + $files + @('--repo', $repository, '--target', $commit, '--title', "Mira $version", '--notes-file', $notes)
    if ($DryRun) {
        if ($draft) { Write-Output "Un brouillon de $tag reste d’une publication interrompue : il sera supprimé avant de publier." }
        Write-Output 'Tout est prêt. Commande de publication (non lancée, -DryRun) :'
        Write-Output ('gh ' + (($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '))
        return
    }
    if ($draft) {
        Invoke-Native 'gh release delete' { gh release delete $tag --repo $repository --yes }
        Write-Output "Brouillon de $tag laissé par une publication interrompue : supprimé."
    }
    Invoke-Native 'gh release create' { gh @arguments }
    Write-Output "Mira $version est publiée : https://github.com/$repository/releases/tag/$tag"
    Write-Output "Les workflows y ajoutent le .dmg, l’extension Jellyfin et son manifeste dans les minutes qui viennent."
} finally {
    Pop-Location
}
