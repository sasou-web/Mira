param(
    [string]$Executable = 'src/Mira.Desktop/bin/Release/net10.0-windows10.0.19041.0/Mira.exe',
    [string]$Output = '.artifacts/player',
    [string]$Work = '.artifacts/player-work'
)
# The real player of Mira for Windows (Mira.exe --player-check, see docs/TESTS.md): libmpv as Mira installs it (the
# pinned build of src/Mira.Core/MpvPackage.cs, checked by its SHA-256), an 18 s synthetic clip with named chapters
# made by ffmpeg (imageio-ffmpeg's build from PyPI, pinned by its hash), a demo profile of its own. Run by ci.yml on
# the runner; works on a PC too (Python 3 and 7-Zip on the PATH). Windows PowerShell 5.1 and PowerShell 7.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$Work = [IO.Path]::GetFullPath($Work); $Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $Work, $Output | Out-Null

# libmpv, into the profile where Mira looks for the engine it installed (data\mpv), with its version.txt so that Mira
# does not prepare another one.
$source = Get-Content -LiteralPath 'src/Mira.Core/MpvPackage.cs' -Raw
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
$profilePath = Join-Path $Work 'profile'
if (Test-Path -LiteralPath $profilePath) { Remove-Item -LiteralPath $profilePath -Recurse -Force }
$engine = Join-Path $profilePath 'mpv'
New-Item -ItemType Directory -Force -Path $engine | Out-Null
& 7z e $archive 'libmpv-2.dll' "-o$engine" -y | Out-Null
if ($LASTEXITCODE -ne 0) { throw "7-Zip could not extract libmpv-2.dll ($LASTEXITCODE)" }
if ((Get-FileHash -LiteralPath (Join-Path $engine 'libmpv-2.dll') -Algorithm SHA256).Hash -ne $match.Groups['library'].Value) { throw 'libmpv-2.dll: unexpected SHA-256' }
Set-Content -LiteralPath (Join-Path $engine 'version.txt') -Value $match.Groups['version'].Value -Encoding ASCII

# ffmpeg: a pinned wheel, installed into the work folder only.
$requirements = Join-Path $Work 'requirements.txt'
Set-Content -LiteralPath $requirements -Encoding ASCII -Value 'imageio-ffmpeg==0.6.0 --hash=sha256:02fa47c83703c37df6bfe4896aab339013f62bf02c5ebf2dce6da56af04ffc0a'
$packages = Join-Path $Work 'python'
& python -m pip install --disable-pip-version-check --quiet --no-deps --only-binary ':all:' --require-hashes --target $packages -r $requirements
if ($LASTEXITCODE -ne 0) { throw "pip could not install imageio-ffmpeg ($LASTEXITCODE)" }
$ffmpeg = Get-ChildItem -LiteralPath (Join-Path $packages 'imageio_ffmpeg/binaries') -Filter 'ffmpeg*.exe' | Select-Object -First 1
if (-not $ffmpeg) { throw 'ffmpeg not found in imageio-ffmpeg' }

# 18 s: « Opening » (0-5 s), « Partie A », « Ending » (13-18 s), a colour test pattern and a tone.
$chapters = Join-Path $Work 'chapters.txt'
$lines = @(';FFMETADATA1')
foreach ($chapter in @(@(0, 5000, 'Opening'), @(5000, 13000, 'Partie A'), @(13000, 18000, 'Ending'))) {
    $lines += '[CHAPTER]', 'TIMEBASE=1/1000', "START=$($chapter[0])", "END=$($chapter[1])", "title=$($chapter[2])"
}
Set-Content -LiteralPath $chapters -Encoding ASCII -Value $lines
$clip = Join-Path $Work 'lecture-chapitres.mkv'
& $ffmpeg.FullName -loglevel error -y -f lavfi -i 'testsrc2=size=1280x720:rate=24:duration=18' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=18' -i $chapters `
    -map 0:v -map 1:a -map_metadata 2 -map_chapters 2 -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -b:a 96k $clip
if ($LASTEXITCODE -ne 0) { throw "ffmpeg could not make the clip ($LASTEXITCODE)" }

# The check itself: the window is placed off screen, the result written to result.txt.
$result = Join-Path $Output 'result.txt'
if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result -Force }
$run = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -PassThru -ArgumentList @('--demo', '--data', "`"$profilePath`"", '--test-media', "`"$clip`"", '--player-check', "`"$Output`"", '--perf-probe')
if (-not $run.WaitForExit(300000)) { $run.Kill(); throw 'The player check did not finish within 5 minutes.' }
if (-not (Test-Path -LiteralPath $result)) { throw "The player check wrote no result (exit code $($run.ExitCode))." }
$lines = Get-Content -LiteralPath $result
$lines | Write-Output
$failed = @($lines | Where-Object { $_ -like 'FAIL*' }).Count
$passed = @($lines | Where-Object { $_ -like 'PASS*' }).Count
if ($failed -gt 0 -or $passed -eq 0) { throw "Player check: $passed passed, $failed failed." }
Write-Output "Player check: $passed passed."
