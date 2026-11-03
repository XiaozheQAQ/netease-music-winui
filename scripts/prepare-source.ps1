[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$NodeVersion = '24.18.0',
    [switch]$SkipRuntime
)

$ErrorActionPreference = 'Stop'
$sourceDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\winuitest\MusicSource'))
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory 'package-lock.json'))) {
    throw 'MusicSource/package-lock.json is missing. Restore the repository before preparing the music source.'
}
$nodeCommand = Get-Command node -ErrorAction Stop
$major = [int](& $nodeCommand.Source -p 'process.versions.node.split(".")[0]')
if ($major -lt 22) { throw 'Node.js 22 or newer is required to install the music source.' }

Push-Location $sourceDirectory
try {
    & npm ci --omit=dev --ignore-scripts --registry=https://registry.npmjs.org
    if ($LASTEXITCODE -ne 0) { throw 'Music source dependency installation failed.' }
} finally { Pop-Location }

if ($SkipRuntime) {
    Write-Host 'Music source dependencies are ready. Runtime bundling was skipped.'
    return
}

$runtimeDirectory = Join-Path $sourceDirectory 'runtime'
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
$localArchitecture = (& $nodeCommand.Source -p 'process.arch').Trim()
$localVersion = (& $nodeCommand.Source -p 'process.versions.node').Trim()
if ($localArchitecture -eq $Architecture -and $localVersion -eq $NodeVersion) {
    Copy-Item -LiteralPath $nodeCommand.Source -Destination (Join-Path $runtimeDirectory 'node.exe') -Force
    $localLicense = Join-Path (Split-Path $nodeCommand.Source) 'LICENSE'
    if (Test-Path -LiteralPath $localLicense) {
        Copy-Item -LiteralPath $localLicense -Destination (Join-Path $runtimeDirectory 'NODE-LICENSE.txt') -Force
    } else {
        Invoke-WebRequest -Uri "https://raw.githubusercontent.com/nodejs/node/v$NodeVersion/LICENSE" -OutFile (Join-Path $runtimeDirectory 'NODE-LICENSE.txt')
    }
} else {
    $archiveName = "node-v$NodeVersion-win-$Architecture.zip"
    $downloadDirectory = Join-Path $runtimeDirectory 'download'
    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
    $archivePath = Join-Path $downloadDirectory $archiveName
    $checksumPath = Join-Path $downloadDirectory 'SHASUMS256.txt'
    $baseUri = "https://nodejs.org/dist/v$NodeVersion"
    Invoke-WebRequest -Uri "$baseUri/$archiveName" -OutFile $archivePath
    Invoke-WebRequest -Uri "$baseUri/SHASUMS256.txt" -OutFile $checksumPath
    $checksumLine = Get-Content -LiteralPath $checksumPath | Where-Object { $_ -match "\s+$([regex]::Escape($archiveName))$" } | Select-Object -First 1
    if (-not $checksumLine) { throw 'The official Node.js archive checksum was not found.' }
    $expectedHash = ($checksumLine -split '\s+')[0]
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($expectedHash -ne $actualHash) { throw 'Node.js archive checksum verification failed.' }
    Expand-Archive -LiteralPath $archivePath -DestinationPath $downloadDirectory -Force
    $extractedDirectory = Join-Path $downloadDirectory "node-v$NodeVersion-win-$Architecture"
    Copy-Item -LiteralPath (Join-Path $extractedDirectory 'node.exe') -Destination (Join-Path $runtimeDirectory 'node.exe') -Force
    Copy-Item -LiteralPath (Join-Path $extractedDirectory 'LICENSE') -Destination (Join-Path $runtimeDirectory 'NODE-LICENSE.txt') -Force
    # These paths are explicitly anchored below MusicSource/runtime; no computed recursive deletion.
    Write-Host "Verified archive retained at $archivePath (excluded from packaging)."
}

@{ version = $NodeVersion; architecture = $Architecture } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtimeDirectory 'version.json') -Encoding utf8
Write-Host "Music source ready: Node.js $NodeVersion ($Architecture), API 4.41.1."
