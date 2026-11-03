#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [string]$CertificatePath,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
$resolvedCertificate = (Resolve-Path -LiteralPath $CertificatePath -ErrorAction Stop).Path

if (-not ('LumaMusic.Packaging.MsixVerifier' -as [type])) {
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    Add-Type -AssemblyName System.Formats.Asn1
    $referenceDirectory = Join-Path $PSHOME 'ref'
    if (-not (Test-Path -LiteralPath $referenceDirectory -PathType Container)) {
        throw 'PowerShell reference assemblies are missing. Use a complete PowerShell 7.2+ installation.'
    }
    $references = @(Get-ChildItem -LiteralPath $referenceDirectory -Filter '*.dll' | Select-Object -ExpandProperty FullName)
    # Pkcs is shipped with PowerShell, outside the standard .NET reference set.
    if (-not (Test-Path -LiteralPath (Join-Path $referenceDirectory 'System.Security.Cryptography.Pkcs.dll'))) {
        $references += [System.Security.Cryptography.Pkcs.SignedCms].Assembly.Location
    }
    Add-Type -Path (Join-Path $PSScriptRoot 'MsixVerifier.cs') -ReferencedAssemblies $references
}

$verification = [LumaMusic.Packaging.MsixVerifier]::Verify($resolvedPackage, $resolvedCertificate)
if ($AsJson) { $verification | ConvertTo-Json -Depth 5 }
else { $verification }
