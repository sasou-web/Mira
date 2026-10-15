param([Parameter(Mandatory = $true)][string[]]$Files, [string]$Publisher = 'SignPath Foundation')
# Each file carries a valid Authenticode signature whose certificate names $Publisher, with a timestamp (the
# signature outlives the certificate). windows.yml runs it after each signing request, tools/release.ps1 on the
# Windows files it publishes. Works in Windows PowerShell 5.1 and PowerShell 7 (saved with a BOM: accents below).
$ErrorActionPreference = 'Stop'
$problems = New-Object System.Collections.Generic.List[string]
foreach ($file in $Files) {
    $name = [IO.Path]::GetFileName($file)
    if (-not (Test-Path -LiteralPath $file)) { $problems.Add("$name : introuvable ($file)"); continue }
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '' }
    if ($signature.Status -ne 'Valid') { $problems.Add("$name : signature $($signature.Status) ($($signature.StatusMessage))") }
    elseif ($subject -notlike "*$Publisher*") { $problems.Add("$name : signé par « $subject », pas par $Publisher") }
    elseif (-not $signature.TimeStamperCertificate) { $problems.Add("$name : signature sans horodatage") }
    else { Write-Output "$name : signé par $Publisher, horodaté ; certificat valable jusqu’au $($signature.SignerCertificate.NotAfter.ToString('yyyy-MM-dd'))." }
}
if ($problems.Count) { throw "Signature Authenticode attendue de $Publisher :`n" + ($problems -join "`n") }
