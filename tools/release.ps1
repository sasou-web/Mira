param([switch]$SkipBuild, [switch]$DryRun)
# Publishes the version in src/Mira.Desktop/Mira.Desktop.csproj in one command, on the PC that holds the signing key:
#   1. checks gh, the signing key, Inno Setup and a clean checkout, then brings main up to date (fast-forward only);
#   2. builds and signs the packages (tools/package.ps1), unless -SkipBuild uses those already in dist/packages;
#   3. creates the GitHub release vX.Y.Z on that commit, with its 8 files and the notes from WhatsNew.json.
# The workflows then add Mira-X.Y.Z-mac-arm64.dmg, mira-jellyfin-X.Y.Z.zip and jellyfin-manifest.json by themselves.
# -DryRun checks everything and prints the release command instead of running it.
# Works in Windows PowerShell 5.1 and PowerShell 7 (saved with a BOM; messages with an apostrophe in double quotes:
# PowerShell reads a typographic apostrophe as a single quote).
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

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

Push-Location $workspace
try {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI (gh) introuvable : installe-le (https://cli.github.com), puis lance « gh auth login »."
    }
    if (-not (Test-Native { gh auth status })) { throw "gh n’est pas connecté à GitHub : lance « gh auth login »." }
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

    [xml]$projectXml = Get-Content -LiteralPath (Join-Path $workspace 'src/Mira.Desktop/Mira.Desktop.csproj')
    $version = [string]$projectXml.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version x.y.z attendue dans Mira.Desktop.csproj, trouvé « $version »." }
    $tag = "v$version"
    if (Test-Native { gh release view $tag }) { throw "La release $tag existe déjà sur GitHub : rien à publier." }

    if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'package.ps1') }

    $packages = Join-Path $workspace 'dist/packages'
    $names = @("Mira-$version-win-x64-setup.exe", "Mira-$version-win-x64-portable.exe", "Mira-$version-win-x64.zip")
    $files = @(@($names | ForEach-Object { $_; "$_.sha256" }) + @('mira-update.json', 'mira-update.json.sig') | ForEach-Object { Join-Path $packages $_ })
    $missing = @($files | Where-Object { -not (Test-Path -LiteralPath $_) })
    if ($missing.Count) { throw "Fichiers manquants dans dist/packages : $(($missing | ForEach-Object { Split-Path $_ -Leaf }) -join ', ')." }
    # The signed manifest must be this version's: one left by an earlier build would point updates at older files.
    if (-not (Select-String -LiteralPath (Join-Path $packages 'mira-update.json') -SimpleMatch "Mira-$version-" -Quiet)) {
        throw "mira-update.json ne décrit pas la $version : relance sans -SkipBuild."
    }
    $notes = Join-Path $workspace ".artifacts/release-notes-$version.md"
    if (-not (Test-Path -LiteralPath $notes)) { throw "Notes de version introuvables ($notes) : l’entrée $version manque dans src/Mira.Core/WhatsNew.json." }

    $arguments = @('release', 'create', $tag) + $files + @('--target', $commit, '--title', "Mira $version", '--notes-file', $notes)
    if ($DryRun) {
        Write-Output 'Tout est prêt. Commande de publication (non lancée, -DryRun) :'
        Write-Output ('gh ' + (($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '))
        return
    }
    Invoke-Native 'gh release create' { gh @arguments }
    Write-Output "Mira $version est publiée : https://github.com/sasou-web/Mira/releases/tag/$tag"
    Write-Output "Les workflows y ajoutent le .dmg, l’extension Jellyfin et son manifeste dans les minutes qui viennent."
} finally {
    Pop-Location
}
