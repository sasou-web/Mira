param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    dotnet restore src/Mira.Desktop/Mira.Desktop.csproj --configfile NuGet.Config -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'La restauration a échoué.' }
    dotnet publish src/Mira.Desktop/Mira.Desktop.csproj --no-restore -c $Configuration -r win-x64 --self-contained true -o dist/Mira -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'La publication a échoué.' }
    Copy-Item -LiteralPath README.md -Destination dist/Mira/LISEZ-MOI.md
    $shortcutPath = Join-Path $workspace 'Mira.lnk'
    $registration = Start-Process -FilePath (Join-Path $workspace 'dist\Mira\Mira.exe') -ArgumentList @('--register-windows', '--shortcut', ('"' + $shortcutPath + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($registration.ExitCode -ne 0) { throw 'La création des raccourcis Windows a échoué.' }
    Write-Host 'Mira est disponible dans dist/Mira/Mira.exe'
} finally { Pop-Location }
