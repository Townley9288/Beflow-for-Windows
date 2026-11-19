param(
    [Parameter(Mandatory)]
    [string]$Package,
    [Parameter(Mandatory)]
    [string]$PrivateKey
)

$ErrorActionPreference = 'Stop'
$Zip = (Resolve-Path -LiteralPath $Package).Path
$Key = (Resolve-Path -LiteralPath $PrivateKey).Path
$Name = [IO.Path]::GetFileName($Zip)
if ($Name -cnotmatch '^Beflow-for-Windows-v[0-9]+(\.[0-9]+){2,3}-win-x64-portable\.zip$') {
    throw 'Select a Beflow portable release ZIP.'
}

$Checksum = "$Zip.sha256"
$Signature = "$Checksum.sig"
$Temporary = "$Signature.$([Guid]::NewGuid().ToString('N')).tmp"
$Fields = (Get-Content -Raw -LiteralPath $Checksum).Trim() -split '\s+'
if ($Fields.Count -ne 2 -or $Fields[1] -cne $Name) { throw 'Checksum must identify this release ZIP.' }
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $Zip).Hash -ne $Fields[0]) {
    throw 'The portable ZIP does not match its SHA-256 file.'
}
$OpenSsl = (Get-Command openssl -CommandType Application).Source
$PublicKey = Join-Path $PSScriptRoot 'portable-update-signing.pub.pem'
try {
    & $OpenSsl pkeyutl -sign -rawin -inkey $Key -in $Checksum -out $Temporary
    if ($LASTEXITCODE -ne 0) { throw 'Ed25519 signing failed.' }
    & $OpenSsl pkeyutl -verify -rawin -pubin -inkey $PublicKey -in $Checksum -sigfile $Temporary
    if ($LASTEXITCODE -ne 0) { throw 'The signature does not match the public key embedded in Beflow.' }
    Move-Item -LiteralPath $Temporary -Destination $Signature -Force
    Get-Item -LiteralPath $Signature
}
finally {
    if (Test-Path -LiteralPath $Temporary) { Remove-Item -LiteralPath $Temporary -Force }
}
