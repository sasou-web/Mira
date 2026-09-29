param([string]$Configuration = 'Release', [string]$Iscc = '')
# Release files in dist/packages, each with its .sha256:
#   Mira-<version>-win-x64.zip            the application folder (portable), with licences and notices
#   Mira-<version>-win-x64-portable.exe   the same application as one self-contained file (it creates data beside it)
#   Mira-<version>-win-x64-setup.exe      per-user installer (Inno Setup 6), when ISCC.exe is available
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $workspace 'src/Mira.Desktop/Mira.Desktop.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.Version
$buildRoot = Join-Path $workspace ('.artifacts/package-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $buildRoot 'Mira'
$single = Join-Path $buildRoot 'single'
$output = Join-Path $workspace 'dist/packages'
New-Item -ItemType Directory -Path $bundle, $single, $output -Force | Out-Null

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
    dotnet publish $project --no-restore -c $Configuration -r win-x64 --self-contained true -o $bundle -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
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
    dotnet publish $project --no-restore -c $Configuration -r win-x64 --self-contained true -o $single -p:DebugType=None -p:DebugSymbols=false `
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
} finally { Pop-Location }
