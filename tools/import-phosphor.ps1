param([string]$Revision = '2b75f3ad12b420c9504ef05df8d2564a28f8500e')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskAssets = Join-Path $taskRoot 'src/Mira.Desktop/Assets/Phosphor'
New-Item -ItemType Directory -Path $taskAssets -Force | Out-Null
$taskRevision = $Revision
$taskMap = [ordered]@{ home='house-simple'; search='magnifying-glass'; film='film-strip'; series='television-simple'; library='squares-four'; heart='heart'; settings='gear-six'; user='user-circle'; play='play-fill'; pause='pause-fill'; info='info'; back='caret-left'; next='caret-right'; down='caret-down'; close='x'; refresh='arrows-clockwise'; check='check'; volume='speaker-high'; subtitles='subtitles'; fullscreen='corners-out'; clock='clock'; server='hard-drives'; plus='plus'; exit='sign-out'; sliders='sliders-horizontal'; bookmark='bookmark-simple'; minus='minus'; maximize='square'; restore='copy-simple'; mini='picture-in-picture'; expand='arrows-out-simple'; collapse='arrows-in-simple'; rewind='arrow-counter-clockwise'; forward='arrow-clockwise'; skip='skip-forward-fill'; mute='speaker-slash' }
$taskEntries = [Collections.Generic.List[string]]::new()
foreach ($taskEntry in $taskMap.GetEnumerator()) {
    $taskWeight = if ($taskEntry.Value.EndsWith('-fill')) { 'fill' } else { 'regular' }
    $taskUrl = "https://raw.githubusercontent.com/phosphor-icons/core/$taskRevision/assets/$taskWeight/$($taskEntry.Value).svg"
    $taskFile = Join-Path $taskAssets ($taskEntry.Value + '.svg')
    Invoke-WebRequest $taskUrl -OutFile $taskFile
    [xml]$taskSvg = [IO.File]::ReadAllText($taskFile)
    $taskPaths = @($taskSvg.svg.ChildNodes | Where-Object { $_.LocalName -eq 'path' })
    if ($taskPaths.Count -ne 1) { throw "Unexpected SVG geometry: $($taskEntry.Value)" }
    $taskData = [Security.SecurityElement]::Escape($taskPaths[0].GetAttribute('d'))
    $taskEntries.Add(('  <Geometry x:Key="Phosphor.{0}">F1 {1}</Geometry>' -f $taskEntry.Key,$taskData))
}
$taskXaml = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + "`n" + ($taskEntries -join "`n") + "`n</ResourceDictionary>"
[IO.File]::WriteAllText((Join-Path $taskAssets 'Icons.xaml'), $taskXaml)
Invoke-WebRequest "https://raw.githubusercontent.com/phosphor-icons/core/$taskRevision/LICENSE" -OutFile (Join-Path $taskAssets 'LICENSE.txt')
[IO.File]::WriteAllText((Join-Path $taskAssets 'SOURCE.txt'), "Phosphor Icons / core`nhttps://github.com/phosphor-icons/core`nRevision: $taskRevision`nRegular icons, with filled playback controls. Original SVG geometry retained.`n")
Write-Output "Imported $($taskMap.Count) Phosphor icons at $taskRevision."
