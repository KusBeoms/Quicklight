# Creates the release signing key pair (ECDSA P-256).
#   Private key: %USERPROFILE%\.quicklight\update-signing-key.pem  (never commit it; back it up)
#   Public key:  printed; it goes into src\Quicklight.Core\Update\UpdateSignature.cs (PublicKeyPem)
# Losing the private key means installed copies can no longer accept updates.
param([string]$KeyPath = (Join-Path $env:USERPROFILE '.quicklight\update-signing-key.pem'))

$ErrorActionPreference = 'Stop'
if (Test-Path $KeyPath) { throw "A key already exists at $KeyPath. Delete it first if you really want a new one." }
$dir = Split-Path $KeyPath
New-Item -ItemType Directory -Force $dir | Out-Null
# Lock the folder to the current user (by SID) before the key is written into it.
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
icacls $dir /inheritance:r /grant:r "*${sid}:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not restrict access to $dir." }

$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
Set-Content -Path $KeyPath -Value $ecdsa.ExportPkcs8PrivateKeyPem() -Encoding ascii -NoNewline

Write-Host "Private key: $KeyPath"
Write-Host "Public key (put into UpdateSignature.PublicKeyPem):"
Write-Host $ecdsa.ExportSubjectPublicKeyInfoPem()
