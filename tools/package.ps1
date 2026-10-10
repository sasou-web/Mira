param([string]$Configuration = 'Release', [string]$Iscc = '', [string]$SigningKey = '', [string]$Version = '', [string]$Output = '')
# Release files in dist/packages, each with its .sha256:
#   Mira-<version>-win-x64.zip            the application folder (portable), with licences and notices
#   Mira-<version>-win-x64-portable.exe   the same application as one self-contained file (it creates data beside it)
#   Mira-<version>-win-x64-setup.exe      per-user installer (Inno Setup 6), when ISCC.exe is available
#   mira-update.json + .sig               signed manifest of those files, read by the automatic updates, when the
#                                         signing key is on this PC (tools/Mira.Release; default %APPDATA%\Mira Release)
# and .artifacts/release-notes-<version>.md, the release page's short text (the version's entry in WhatsNew.json).
# -Version and -Output build test packages under another version number and folder (update checks), never in dist.
$ErrorActionPreference = 'Stop'
# The .NET SDK sends usage data unless told not to; the scripts tell it, on this PC as in the CI.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $workspace 'src/Mira.Desktop/Mira.Desktop.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = if ($Version) { $Version } else { [string]$projectXml.Project.PropertyGroup.Version }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version x.y.z expected, got '$version'." }
$buildRoot = Join-Path $workspace ('.artifacts/package-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $buildRoot 'Mira'
$single = Join-Path $buildRoot 'single'
$output = if ($Output) { [IO.Path]::GetFullPath($Output) } else { Join-Path $workspace 'dist/packages' }
New-Item -ItemType Directory -Path $bundle, $single, $output -Force | Out-Null
# tools/release.ps1 publishes only packages it built itself from the published commit, recorded in this file.
Remove-Item -LiteralPath (Join-Path $output 'source-commit.txt') -ErrorAction SilentlyContinue

function Write-Hash([string]$file) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($file))" | Set-Content -LiteralPath "$file.sha256" -Encoding ascii
    Write-Output $file
    Write-Output "$file.sha256"
}

Push-Location $workspace
try {
    dotnet restore $project --configfile NuGet.Config -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet publish $project --no-restore -c $Configuration -r win-x64 --self-contained true -o $bundle -p:DebugType=None -p:DebugSymbols=false "-p:Version=$version"
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    # The .NET runtime these packages carry, to compare with the latest patch (https://dotnet.microsoft.com/download).
    $runtime = (Get-Item -LiteralPath (Join-Path $bundle 'System.Private.CoreLib.dll')).VersionInfo.ProductVersion -replace '[-+].*$', ''
    Write-Output ".NET runtime included: $runtime"
    foreach ($name in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'CHANGELOG.md', 'SECURITY.md')) {
        Copy-Item -LiteralPath (Join-Path $workspace $name) -Destination $bundle
    }
    Copy-Item -LiteralPath (Join-Path $workspace 'docs/INSTALLATION.md') -Destination (Join-Path $bundle 'INSTALLATION.md')
    Copy-Item -LiteralPath (Join-Path $workspace 'docs/INSTALLATION.md') -Destination (Join-Path $bundle 'README.md')
    Copy-Item -LiteralPath (Join-Path $workspace 'licenses') -Destination $bundle -Recurse
    $privateFiles = Get-ChildItem -LiteralPath $bundle -Recurse -Force | Where-Object {
        $_.Name -eq 'data' -or $_.Name -match '\.(protected|db|db-wal|db-shm|sqlite|log|pfx|p12|key|mp4|mkv)$' -or $_.Name -eq 'settings.json'
    }
    if ($privateFiles) { throw 'Unexpected profile or private file in the release staging folder.' }
    $archive = Join-Path $output "Mira-$version-win-x64.zip"
    Compress-Archive -LiteralPath $bundle -DestinationPath $archive -CompressionLevel Optimal -Force
    Write-Hash $archive

    # One file: native libraries are extracted by .NET at first launch; mpv's key bindings and the Shell icon are
    # embedded in Mira and written into its profile, the TorLink page is served from its resources.
    dotnet publish $project --no-restore -c $Configuration -r win-x64 --self-contained true -o $single -p:DebugType=None -p:DebugSymbols=false "-p:Version=$version" `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
    if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }
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
    } else {
        Write-Warning 'Inno Setup 6 (ISCC.exe) not found: the installer was not built. Install it or pass -Iscc <path>.'
    }

    # The release page says what changes in a few lines; the details stay in CHANGELOG.md. Only for release builds:
    # PowerShell names ignore case, so $Output itself now holds the packages folder and cannot tell a test build.
    if (-not $PSBoundParameters.ContainsKey('Output')) {
        dotnet run --project (Join-Path $workspace 'tools/Mira.Release/Mira.Release.csproj') -c $Configuration -- notes --version $version --out (Join-Path $workspace ".artifacts/release-notes-$version.md")
        if ($LASTEXITCODE -ne 0) { Write-Warning "No release notes for ${version}: add its entry to src/Mira.Core/WhatsNew.json." }
    }

    # Installed copies update themselves only from a release whose mira-update.json is signed by a key they trust.
    if (-not $SigningKey) { $SigningKey = Join-Path $env:APPDATA 'Mira Release\update-signing.key' }
    foreach ($stale in @('mira-update.json', 'mira-update.json.sig')) { Remove-Item -LiteralPath (Join-Path $output $stale) -ErrorAction SilentlyContinue }
    if ($env:APPDATA -and (Test-Path -LiteralPath $SigningKey)) {
        dotnet run --project (Join-Path $workspace 'tools/Mira.Release/Mira.Release.csproj') -c $Configuration -- manifest --version $version --packages $output --key $SigningKey
        if ($LASTEXITCODE -ne 0) { throw 'Signing the update manifest failed.' }
    } else {
        Write-Warning "Signing key not found ($SigningKey): mira-update.json was not produced. Copies already installed will not update to this build automatically."
    }
} finally {
    Pop-Location
    # The staging folders (two published copies, about 270 MB) are only inputs of the files above.
    if (Test-Path -LiteralPath $buildRoot) { Remove-Item -LiteralPath $buildRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
