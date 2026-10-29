param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
# The .NET SDK sends usage data unless told not to; the scripts tell it, on this PC as in the CI.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    dotnet restore src/Mira.Desktop/Mira.Desktop.csproj --configfile NuGet.Config -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'La restauration a échoué.' }
    # Le nœud de Tailscale intégré (src/Mira.Tailscale), à côté de Mira.exe : sans Go ni Node, Mira est publié sans lui.
    if ((Get-Command go -ErrorAction SilentlyContinue) -and (Get-Command node -ErrorAction SilentlyContinue)) {
        node tools/tailscale/build.mjs windows/amd64 .artifacts/tailscale/windows-amd64
        if ($LASTEXITCODE -ne 0) { throw 'La compilation de mira-tailscale.exe a échoué.' }
    } else { Write-Warning 'Go ou Node manque : Mira est publié sans Tailscale intégré.' }
    dotnet publish src/Mira.Desktop/Mira.Desktop.csproj --no-restore -c $Configuration -r win-x64 --self-contained true -o dist/Mira -p:DebugType=None -p:DebugSymbols=false -p:MiraValidation=false
    if ($LASTEXITCODE -ne 0) { throw 'La publication a échoué.' }
    Copy-Item -LiteralPath README.md -Destination dist/Mira/LISEZ-MOI.md
    $shortcutPath = Join-Path $workspace 'Mira.lnk'
    $registration = Start-Process -FilePath (Join-Path $workspace 'dist\Mira\Mira.exe') -ArgumentList @('--register-windows', '--shortcut', ('"' + $shortcutPath + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($registration.ExitCode -ne 0) { throw 'La création des raccourcis Windows a échoué.' }
    Write-Host 'Mira est disponible dans dist/Mira/Mira.exe'
} finally { Pop-Location }
