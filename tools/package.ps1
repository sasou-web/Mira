param([string]$Configuration = 'Release', [string]$Iscc = '', [string]$SigningKey = '', [string]$Version = '', [string]$Output = '',
    [string]$Publish = '', [string]$From = '', [string]$Hash = '')
# Release files in dist/packages, each with its .sha256:
#   Mira-<version>-win-x64.zip            the application folder (portable), with licences and notices
#   Mira-<version>-win-x64-portable.exe   the same application as one self-contained file (it creates data beside it)
#   Mira-<version>-win-x64-setup.exe      per-user installer (Inno Setup 6), when ISCC.exe is available
# and .artifacts/release-notes-<version>.md, the release page's short text (the version's entry in WhatsNew.json).
# Each package carries mira-tailscale.exe, the node of Built-in Tailscale, built first for that version by
# tools/tailscale/build.mjs (Go 1.21 or later and Node), with its Go modules' licences in licenses\mira-tailscale.
# A release's files are built by windows.yml, on GitHub, in three steps of this script, so that SignPath Foundation
# signs Mira's own files before the packages are made around them:
#   -Publish <dir>   publishes the application folder (<dir>\Mira) and the one-file Mira (<dir>\Portable\Mira.exe), then stops;
#   -From <dir>      makes the three packages and their .sha256 from such a folder (its files signed meanwhile) instead of publishing;
#   -Hash <file>     writes <file>.sha256 alone (the installer, once signed in its turn).
# A release's signed manifest, mira-update.json + .sig, also covers the Mac disk image and Mira web's plugin, which
# the workflows build on the release's draft: tools/release.ps1 signs it last, with the key of this PC.
# -Version and -Output build test packages under another version number and folder (update checks), never in dist;
# -SigningKey signs their manifest, Windows files only, with the throwaway key tools/update-check.ps1 passes.
$ErrorActionPreference = 'Stop'
# The .NET SDK sends usage data unless told not to; the scripts tell it, on this PC as in the CI.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Write-Hash([string]$file) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($file))" | Set-Content -LiteralPath "$file.sha256" -Encoding ascii
    Write-Output $file
    Write-Output "$file.sha256"
}
if ($Hash) { Write-Hash ([IO.Path]::GetFullPath($Hash)); return }
if ($Publish -and $From) { throw 'Pass -Publish or -From, not both.' }

$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $workspace 'src/Mira.Desktop/Mira.Desktop.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = if ($Version) { $Version } else { [string]$projectXml.Project.PropertyGroup.Version }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version x.y.z expected, got '$version'." }
# The published copies: a folder of this run's own, or the one -Publish writes and -From reads.
$staging = if ($Publish) { [IO.Path]::GetFullPath($Publish) } elseif ($From) { [IO.Path]::GetFullPath($From) } else { Join-Path $workspace ('.artifacts/package-' + [Guid]::NewGuid().ToString('N')) }
$bundle = Join-Path $staging 'Mira'
$single = Join-Path $staging 'Portable'
$output = if ($Output) { [IO.Path]::GetFullPath($Output) } else { Join-Path $workspace 'dist/packages' }

Push-Location $workspace
try {
    if ($From) {
        foreach ($required in @((Join-Path $bundle 'Mira.exe'), (Join-Path $bundle 'Mira.dll'), (Join-Path $bundle 'Mira.Core.dll'), (Join-Path $bundle 'mira-tailscale.exe'), (Join-Path $single 'Mira.exe'))) {
            if (-not (Test-Path -LiteralPath $required)) { throw "$required is missing: -From expects the folder -Publish wrote." }
        }
    } else {
        # Folders of this publication only: a file left by an earlier one would be packaged too.
        foreach ($folder in @($bundle, $single)) { if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force } }
        New-Item -ItemType Directory -Path $bundle, $single -Force | Out-Null
        # The node of Built-in Tailscale (src/Mira.Tailscale), built here for this version: Mira.Desktop.csproj puts
        # it beside Mira.exe and inside the one-file Mira, with its SHA-256, the only copy Mira will run.
        $tailscale = Join-Path $workspace '.artifacts/tailscale/windows-amd64'
        node (Join-Path $workspace 'tools/tailscale/build.mjs') windows/amd64 $tailscale --version $version
        if ($LASTEXITCODE -ne 0) { throw 'Building mira-tailscale.exe failed: it needs Go 1.21 or later and Node.' }
        dotnet restore $project --configfile NuGet.Config -r win-x64
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
        # Without the validation harnesses of Checks/ (Mira.Desktop.csproj): they stay in the builds the checks use.
        dotnet publish $project --no-restore -c $Configuration -r win-x64 --self-contained true -o $bundle -p:DebugType=None -p:DebugSymbols=false "-p:Version=$version" -p:MiraValidation=false
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        if ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $bundle 'Mira.dll'))).Contains('RunRequestedCheckAsync')) {
            throw 'The validation harnesses are still in the published Mira.dll.'
        }
        # The .NET runtime these packages carry, to compare with the latest patch (https://dotnet.microsoft.com/download).
        $runtime = (Get-Item -LiteralPath (Join-Path $bundle 'System.Private.CoreLib.dll')).VersionInfo.ProductVersion -replace '[-+].*$', ''
        Write-Output ".NET runtime included: $runtime"
        foreach ($name in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md', 'SECURITY.md')) {
            Copy-Item -LiteralPath (Join-Path $workspace $name) -Destination $bundle
        }
        Copy-Item -LiteralPath (Join-Path $workspace 'docs/INSTALLATION.md') -Destination (Join-Path $bundle 'INSTALLATION.md')
        Copy-Item -LiteralPath (Join-Path $workspace 'docs/INSTALLATION.md') -Destination (Join-Path $bundle 'README.md')
        Copy-Item -LiteralPath (Join-Path $workspace 'licenses') -Destination $bundle -Recurse
        # The node published is the one just built, a Mira file for SignPath; its Go modules' licences go with it.
        $node = Get-Item -LiteralPath (Join-Path $bundle 'mira-tailscale.exe')
        if ((Get-FileHash -LiteralPath $node.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $tailscale 'mira-tailscale.exe') -Algorithm SHA256).Hash) { throw 'The published mira-tailscale.exe is not the one just built.' }
        if ($node.VersionInfo.ProductName -ne 'Mira' -or $node.VersionInfo.ProductVersion -ne $version) { throw "mira-tailscale.exe: product Mira $version expected, got '$($node.VersionInfo.ProductName)' '$($node.VersionInfo.ProductVersion)'." }
        Copy-Item -LiteralPath (Join-Path $tailscale 'licenses') -Destination (Join-Path $bundle 'licenses/mira-tailscale') -Recurse
        $privateFiles = Get-ChildItem -LiteralPath $bundle -Recurse -Force | Where-Object {
            $_.Name -eq 'data' -or $_.Name -match '\.(protected|db|db-wal|db-shm|sqlite|log|pfx|p12|key|mp4|mkv)$' -or $_.Name -eq 'settings.json'
        }
        if ($privateFiles) { throw 'Unexpected profile or private file in the release staging folder.' }

        # One file: native libraries are extracted by .NET at first launch; mpv's key bindings and the Shell icon are
        # embedded in Mira and written into its profile, the TorLink page is served from its resources.
        dotnet publish $project --no-restore -c $Configuration -r win-x64 --self-contained true -o $single -p:DebugType=None -p:DebugSymbols=false "-p:Version=$version" `
            -p:MiraValidation=false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
        if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }
        # Only Mira.exe is the portable Mira; what else the publication wrote beside it is not packaged.
        Get-ChildItem -LiteralPath $single -Force | Where-Object { $_.Name -ne 'Mira.exe' } | Remove-Item -Recurse -Force
        if ($Publish) { Write-Output "Published in ${staging}: Mira (the application folder) and Portable\Mira.exe."; return }
    }

    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $archive = Join-Path $output "Mira-$version-win-x64.zip"
    Compress-Archive -LiteralPath $bundle -DestinationPath $archive -CompressionLevel Optimal -Force
    Write-Hash $archive

    $portable = Join-Path $output "Mira-$version-win-x64-portable.exe"
    Copy-Item -LiteralPath (Join-Path $single 'Mira.exe') -Destination $portable -Force
    Write-Hash $portable

    if (-not $Iscc) {
        $Iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
            Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    }
    if ($Iscc) {
        & $Iscc /Q "/DAppVersion=$version" "/DSourceDir=$bundle" "/DOutputDir=$output" (Join-Path $workspace 'installer/Mira.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
        Write-Hash (Join-Path $output "Mira-$version-win-x64-setup.exe")
    } elseif ($From) {
        throw 'Inno Setup 6 (ISCC.exe) not found: a release needs the installer, Mira-X.Y.Z-win-x64-setup.exe.'
    } else {
        Write-Warning 'Inno Setup 6 (ISCC.exe) not found: the installer was not built. Install it or pass -Iscc <path>.'
    }

    # The release page says what changes in a few lines; the details stay in CHANGELOG.md. Only for release builds:
    # PowerShell names ignore case, so $Output itself now holds the packages folder and cannot tell a test build.
    if (-not $PSBoundParameters.ContainsKey('Output')) {
        dotnet run --project (Join-Path $workspace 'tools/Mira.Release/Mira.Release.csproj') -c $Configuration -- notes --version $version --out (Join-Path $workspace ".artifacts/release-notes-$version.md")
        if ($LASTEXITCODE -ne 0) { Write-Warning "No release notes for ${version}: add its entry to src/Mira.Core/WhatsNew.json." }
    }

    # A manifest left from an earlier build would describe other files.
    foreach ($stale in @('mira-update.json', 'mira-update.json.sig')) { Remove-Item -LiteralPath (Join-Path $output $stale) -ErrorAction SilentlyContinue }
    if ($SigningKey -and (Test-Path -LiteralPath $SigningKey)) {
        # --test: Mira.Release refuses any key that installed copies do not trust, except this throwaway one.
        dotnet run --project (Join-Path $workspace 'tools/Mira.Release/Mira.Release.csproj') -c $Configuration -- manifest --version $version --packages $output --key $SigningKey --test
        if ($LASTEXITCODE -ne 0) { throw 'Signing the test update manifest failed.' }
    } elseif ($SigningKey) {
        Write-Warning "Test signing key not found ($SigningKey): mira-update.json was not produced."
    }
} finally {
    Pop-Location
    # This run's staging folders (two published copies, about 270 MB) are only inputs of the files above; a folder
    # named by -Publish or -From belongs to the caller.
    if (-not $Publish -and -not $From -and (Test-Path -LiteralPath $staging)) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
}
