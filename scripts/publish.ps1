[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = 'x64',
    [switch]$SkipPrepare,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'winuitest\winuitest.csproj'
$sourcePath = Join-Path $repoRoot 'winuitest\MusicSource'
$publishPath = Join-Path $repoRoot "artifacts\win-$Architecture\publish"
$stagingPath = Join-Path $repoRoot ".build\publish\$Architecture-$([Guid]::NewGuid().ToString('N'))"

if (-not $SkipPrepare) {
    & (Join-Path $PSScriptRoot 'prepare-source.ps1') -Architecture $Architecture
}
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath 'runtime\node.exe'))) {
    throw '发布需要捆绑 Node.js。请先执行 scripts\prepare-source.ps1。'
}
if (-not (Test-Path -LiteralPath (Join-Path $sourcePath 'node_modules\@neteasecloudmusicapienhanced\api\package.json'))) {
    throw '音源依赖缺失。请先执行 scripts\prepare-source.ps1。'
}

if (-not $env:DOTNET_CLI_HOME) {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.build\dotnet'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$publishArguments = @(
    'publish', $projectPath, '-c', 'Release',
    "-p:Platform=$Architecture", '-r', "win-$Architecture",
    '-p:WindowsPackageType=None', '-p:WindowsAppSDKSelfContained=true',
    '-p:MSBuildEnableWorkloadResolver=false', '-p:PublishReadyToRun=false',
    '--self-contained', 'true', '-o', $stagingPath
)
if ($NoRestore) { $publishArguments += '--no-restore' }
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出代码 $LASTEXITCODE。" }
$resolvedPublishPath = [IO.Path]::GetFullPath($publishPath)
$expectedPublishPath = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\win-$Architecture\publish"))
if ($resolvedPublishPath -ne $expectedPublishPath -or -not $resolvedPublishPath.StartsWith([IO.Path]::GetFullPath($repoRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '发布目录越出工作区，已停止替换。'
}
if (Test-Path -LiteralPath $resolvedPublishPath) {
    if ((Get-Item -LiteralPath $resolvedPublishPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '发布目录不能是重解析点，已停止替换。' }
    foreach ($executable in @('LumaMusic.exe', 'winuitest.exe')) {
        $executablePath = Join-Path $resolvedPublishPath $executable
        if (Test-Path -LiteralPath $executablePath) {
            try {
                $exclusiveFile = [IO.File]::Open($executablePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $exclusiveFile.Dispose()
            } catch { throw "发布程序仍被占用，请先关闭从该目录运行的 $executable，再重新发布。新产物保留在 $stagingPath。" }
        }
    }
    Remove-Item -LiteralPath $resolvedPublishPath -Recurse -Force
}
New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedPublishPath) -Force | Out-Null
Move-Item -LiteralPath $stagingPath -Destination $resolvedPublishPath
Write-Host "发布目录：$publishPath"
Write-Host '请分发整个 publish 文件夹，运行其中的 LumaMusic.exe。'
