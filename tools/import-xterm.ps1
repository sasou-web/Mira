param([string]$XtermVersion = '6.0.0', [string]$FitVersion = '0.11.0')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$workspace = Split-Path -Parent $PSScriptRoot
$target = Join-Path $workspace 'src/Mira.Desktop/Assets/TorLink/xterm'
$staging = Join-Path $env:TEMP ('mira-xterm-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $target, $staging -Force | Out-Null

# Downloads one npm tarball and refuses it unless its SHA-512 matches the registry's integrity field.
function Get-NpmPackage([string]$name, [string]$version) {
    $metadata = Invoke-RestMethod -Uri ("https://registry.npmjs.org/{0}/{1}" -f $name.Replace('/', '%2f'), $version)
    $integrity = [string]$metadata.dist.integrity
    if (-not $integrity.StartsWith('sha512-')) { throw "Unexpected integrity format for $name@$version." }
    $archive = Join-Path $staging (($name -replace '[@/]', '_') + '.tgz')
    Invoke-WebRequest -Uri $metadata.dist.tarball -OutFile $archive -UseBasicParsing
    $sha = [Security.Cryptography.SHA512]::Create()
    try { $actual = 'sha512-' + [Convert]::ToBase64String($sha.ComputeHash([IO.File]::ReadAllBytes($archive))) } finally { $sha.Dispose() }
    if ($actual -ne $integrity) { throw "Integrity mismatch for $name@$version." }
    $folder = Join-Path $staging ($name -replace '[@/]', '_')
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    tar -xzf $archive -C $folder
    if ($LASTEXITCODE -ne 0) { throw "Extraction failed for $name@$version." }
    return [pscustomobject]@{ Root = Join-Path $folder 'package'; Integrity = $integrity; License = [string]$metadata.license }
}

try {
    $xterm = Get-NpmPackage '@xterm/xterm' $XtermVersion
    $fit = Get-NpmPackage '@xterm/addon-fit' $FitVersion
    if ($xterm.License -ne 'MIT' -or $fit.License -ne 'MIT') { throw 'Unexpected licence for xterm.js packages.' }
    Copy-Item -LiteralPath (Join-Path $xterm.Root 'lib/xterm.js') -Destination (Join-Path $target 'xterm.js')
    Copy-Item -LiteralPath (Join-Path $xterm.Root 'css/xterm.css') -Destination (Join-Path $target 'xterm.css')
    Copy-Item -LiteralPath (Join-Path $fit.Root 'lib/addon-fit.js') -Destination (Join-Path $target 'addon-fit.js')
    Copy-Item -LiteralPath (Join-Path $xterm.Root 'LICENSE') -Destination (Join-Path $target 'LICENSE.txt')
    $source = "xterm.js`nhttps://github.com/xtermjs/xterm.js`n@xterm/xterm $XtermVersion ($($xterm.Integrity))`n@xterm/addon-fit $FitVersion ($($fit.Integrity))`nUnmodified npm build files: lib/xterm.js, css/xterm.css, lib/addon-fit.js. MIT licence.`n"
    [IO.File]::WriteAllText((Join-Path $target 'SOURCE.txt'), $source)
    Write-Output "Imported xterm.js $XtermVersion and addon-fit $FitVersion."
} finally { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
