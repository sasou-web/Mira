param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $workspace 'src/Mira.Desktop/Mira.Desktop.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.Version
$buildRoot = Join-Path $workspace ('.artifacts/package-' + [Guid]::NewGuid().ToString('N'))
$bundle = Join-Path $buildRoot 'Mira'
$output = Join-Path $workspace 'dist/packages'
New-Item -ItemType Directory -Path $bundle, $output -Force | Out-Null
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
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Output $archive
    Write-Output "$archive.sha256"
} finally { Pop-Location }
