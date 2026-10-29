param(
    [Parameter(Mandatory = $true)][string]$Work,
    [Parameter(Mandatory = $true)][string]$ProfileFolder
)
# libmpv as Mira installs it, for the checks of tools/player-check.ps1 and tools/server-check.ps1: the pinned build of
# src/Mira.Core/MpvPackage.cs (its archive kept in $Work, SourceForge being slow), checked by its SHA-256, put where Mira
# looks for the engine it installed (<profile>\mpv) with its version.txt, so that Mira does not prepare another one.
# Windows PowerShell 5.1 and PowerShell 7; 7-Zip on the PATH.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Work = [IO.Path]::GetFullPath($Work); $ProfileFolder = [IO.Path]::GetFullPath($ProfileFolder)
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/Mira.Core/MpvPackage.cs') -Raw
$pattern = 'Current = new\("(?<version>[^"]+)",\s*new\("(?<url>https://[^"]+)"\),\s*(?<size>[\d_]+),\s*"(?<archive>[0-9a-f]{64})",\s*"(?<library>[0-9a-f]{64})"\)'
$match = [regex]::Match($source, $pattern)
if (-not $match.Success) { throw 'MpvPackage.Current not found in src/Mira.Core/MpvPackage.cs' }
$archive = Join-Path $Work 'libmpv.7z'
$hashOk = (Test-Path -LiteralPath $archive) -and ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -eq $match.Groups['archive'].Value)
if (-not $hashOk) {
    $downloaded = $false
    for ($attempt = 1; $attempt -le 4 -and -not $downloaded; $attempt++) {
        try { Invoke-WebRequest -UseBasicParsing -Uri $match.Groups['url'].Value -OutFile $archive -UserAgent 'Wget' -MaximumRedirection 10; $downloaded = $true }
        catch { Write-Output "libmpv download, attempt $attempt : $($_.Exception.Message)"; Start-Sleep -Seconds (2 * $attempt) }
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $match.Groups['archive'].Value) { throw 'libmpv archive: unexpected SHA-256' }
}
$engine = Join-Path $ProfileFolder 'mpv'
New-Item -ItemType Directory -Force -Path $engine | Out-Null
& 7z e $archive 'libmpv-2.dll' "-o$engine" -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw "7-Zip could not extract libmpv-2.dll ($LASTEXITCODE)" }
if ((Get-FileHash -LiteralPath (Join-Path $engine 'libmpv-2.dll') -Algorithm SHA256).Hash -ne $match.Groups['library'].Value) { throw 'libmpv-2.dll: unexpected SHA-256' }
Set-Content -LiteralPath (Join-Path $engine 'version.txt') -Value $match.Groups['version'].Value -Encoding ASCII
