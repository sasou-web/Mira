param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$profile = Join-Path $workspace ('.artifacts/gallery-profile-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $workspace ('.artifacts/gallery-captures-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $workspace 'docs/screenshots'
Push-Location $workspace
try {
    dotnet build src/Mira.Desktop/Mira.Desktop.csproj -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $exe = Join-Path $workspace "src/Mira.Desktop/bin/$Configuration/net8.0-windows10.0.19041.0/Mira.exe"
    $capture = Start-Process -FilePath $exe -ArgumentList @('--demo', '--data', ('"' + $profile + '"'), '--public-gallery', ('"' + $output + '"')) -WindowStyle Hidden -PassThru -Wait
    $result = Get-Content -LiteralPath (Join-Path $output 'gallery-result.txt') -Raw
    if ($capture.ExitCode -ne 0 -or $result -notlike 'PASS:*') { throw 'Gallery capture failed.' }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $output -Filter '*.png' | Where-Object Name -Match '^\d{2}-' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Force }
    Write-Output $result
} finally { Pop-Location }
