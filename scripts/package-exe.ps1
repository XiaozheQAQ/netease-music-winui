#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$MsiPath,
    [string]$OutputDirectory,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $MsiPath) { $MsiPath = Join-Path $repoRoot "artifacts\msi\LumaMusic_$Version`_x64.msi" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\setup' }
$MsiPath = (Resolve-Path -LiteralPath $MsiPath).ProviderPath
$msi = & (Join-Path $PSScriptRoot 'test-msi.ps1') -PackagePath $MsiPath
if ($msi.version -ne $Version) { throw "MSI 版本为 $($msi.version)，与目标版本 $Version 不一致。" }
$msiHash = (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash

$workDirectory = Join-Path $repoRoot ('.build\setup\' + [Guid]::NewGuid().ToString('N'))
$stageDirectory = Join-Path $workDirectory 'publish'
$verifyDirectory = Join-Path $workDirectory 'verify-temp'
New-Item -ItemType Directory -Path $stageDirectory, $verifyDirectory, $OutputDirectory -Force | Out-Null
$env:DOTNET_CLI_HOME = $env:DOTNET_CLI_HOME ?? (Join-Path $repoRoot '.build\dotnet')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$publishArguments = @(
    'publish', (Join-Path $repoRoot 'installer\Setup\LumaMusic.Setup.csproj'),
    '-c', 'Release', '-o', $stageDirectory, '-p:MSBuildEnableWorkloadResolver=false',
    "-p:Version=$Version", "-p:FileVersion=$Version.0", "-p:LumaSetupMsiPath=$MsiPath"
)
if ($NoRestore) { $publishArguments += '--no-restore' }
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw "EXE 安装包构建失败（$LASTEXITCODE）。" }
$stagedExe = Join-Path $stageDirectory 'LumaMusicSetup.exe'
if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) { throw '发布目录中缺少安装程序。' }
if (@(Get-ChildItem -LiteralPath $stageDirectory -Force).Count -ne 1) { throw '安装程序发布必须只包含一个 EXE。' }

$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$previousExtractDirectory = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
try {
    $env:TEMP = $verifyDirectory
    $env:TMP = $verifyDirectory
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $workDirectory 'runtime'
    $verification = Start-Process -FilePath $stagedExe -ArgumentList @('--verify-msi', $msiHash) -WindowStyle Hidden -PassThru -Wait
    if ($verification.ExitCode -ne 0) { throw "内嵌 MSI 校验失败（$($verification.ExitCode)）。" }
    if (Get-ChildItem -LiteralPath $verifyDirectory -Force | Select-Object -First 1) { throw '内嵌 MSI 校验后仍有临时文件。' }
} finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $previousExtractDirectory
}

$name = "LumaMusic_$Version`_x64_Setup.exe"
$setupPath = Join-Path $OutputDirectory $name
Move-Item -LiteralPath $stagedExe -Destination $setupPath -Force
$setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
"$setupHash  $name" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
[pscustomobject]@{ path=(Get-Item -LiteralPath $setupPath).FullName; sha256=$setupHash; embeddedMsiSha256=$msiHash; payloadVerification='Passed' }
