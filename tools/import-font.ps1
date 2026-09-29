$ErrorActionPreference = 'Stop'
$fontDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Mira.Desktop/Assets/Fonts'
New-Item -ItemType Directory -Path $fontDirectory -Force | Out-Null
$fontRevision = '058bd7a2f33d6ad5ef1df985b3db403622016a8c'
$fontBase = "https://raw.githubusercontent.com/googlefonts/NunitoSans/$fontRevision"
foreach ($weight in @('Regular', 'SemiBold', 'Bold', 'ExtraBold')) {
    Invoke-WebRequest "$fontBase/fonts/ttf/NunitoSans-$weight.ttf" -OutFile (Join-Path $fontDirectory "NunitoSans-$weight.ttf")
}
Invoke-WebRequest "$fontBase/OFL.txt" -OutFile (Join-Path $fontDirectory 'OFL.txt')
[IO.File]::WriteAllText((Join-Path $fontDirectory 'SOURCE.txt'), "Nunito Sans`nhttps://github.com/googlefonts/NunitoSans`nRevision: $fontRevision`nUnmodified official static font files. SIL Open Font License 1.1.`n")
