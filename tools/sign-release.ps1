# Signs dist\Quicklight.exe and packs the release asset dist\Quicklight_v<version>.zip (Quicklight.exe + Quicklight.exe.sig).
# The signature is ECDSA P-256 over SHA-256 of the exe, base64 text; installed copies verify it with the public key
# built into them before installing an update.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\Quicklight.exe'),
    # Defaults to the version written into the exe.
    [string]$Version = '',
    [string]$KeyPath = $(if ($env:QUICKLIGHT_SIGNING_KEY) { $env:QUICKLIGHT_SIGNING_KEY } else { Join-Path $env:USERPROFILE '.quicklight\update-signing-key.pem' })
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
if (-not (Test-Path $KeyPath)) { throw "Signing key not found at $KeyPath (create one with tools\new-signing-key.ps1)." }

$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
$ecdsa.ImportFromPem((Get-Content $KeyPath -Raw))
$bytes = [System.IO.File]::ReadAllBytes($Exe)
$signature = $ecdsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)

# Check against the public key built into Quicklight: a release signed with the wrong key would be refused by
# every installed copy, so stop here instead of publishing it.
$source = Get-Content (Join-Path $PSScriptRoot '..\src\Quicklight.Core\Update\UpdateSignature.cs') -Raw
if ($source -notmatch '(?s)(-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----)') { throw 'Public key not found in UpdateSignature.cs.' }
$pem = ($Matches[1] -split "`n" | ForEach-Object { $_.Trim() }) -join "`n"
$verifier = [System.Security.Cryptography.ECDsa]::Create()
$verifier.ImportFromPem($pem)
if (-not $verifier.VerifyData($bytes, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) {
    throw "The signing key at $KeyPath does not match the public key in UpdateSignature.cs."
}

$sig = "$Exe.sig"
Set-Content -Path $sig -Value ([Convert]::ToBase64String($signature)) -Encoding ascii -NoNewline

if (-not $Version) {
    $v = (Get-Item $Exe).VersionInfo
    $Version = "$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart)"
}
$Version = $Version.TrimStart('v', 'V')
$zip = Join-Path (Split-Path $Exe) "Quicklight_v$Version.zip"
Get-ChildItem (Split-Path $Exe) -Filter 'Quicklight*.zip' | Remove-Item   # one release asset in dist at a time
Compress-Archive -Path $Exe, $sig -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Signed and packed: $zip"
