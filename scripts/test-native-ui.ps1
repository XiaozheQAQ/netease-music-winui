#requires -Version 7.2
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$testRoot = Join-Path $repoRoot ('.build\native-ui-' + [Guid]::NewGuid().ToString('N'))
$appPath = Join-Path $testRoot 'app'
$profilePath = Join-Path $testRoot 'profile'
$reportPath = Join-Path $testRoot 'result.json'
New-Item -ItemType Directory -Path $profilePath -Force | Out-Null
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.build\dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$buildArguments = @('build', (Join-Path $repoRoot 'winuitest\winuitest.csproj'), '-c', 'Release',
    '-p:Platform=x64', '-p:WindowsPackageType=None', '-p:MSBuildEnableWorkloadResolver=false',
    '-p:PublishReadyToRun=false', '-p:LumaNativeChecks=true', '--no-restore', '-o', ($appPath + [IO.Path]::DirectorySeparatorChar), '-v:minimal')
& dotnet @buildArguments *> (Join-Path $testRoot 'build.log')
if ($LASTEXITCODE -ne 0) { throw "原生界面检查构建失败，详见 $testRoot\build.log。" }
Copy-Item -LiteralPath (Join-Path $repoRoot 'tests\Fixtures\tray-source.cjs') -Destination (Join-Path $appPath 'MusicSource\service.cjs')
$first = @{ Id=12345; Title='界面测试 · 静音音轨'; Artist='Luma Music'; Album='UI'; DurationMs=180000; ArtworkUrl='' }
$second = @{ Id=12346; Title='下一首 · 静音音轨'; Artist='Luma Music'; Album='UI'; DurationMs=180000; ArtworkUrl='' }
@{ Theme=2; Volume=0.01; LastSession=@{CurrentTrack=$first; Queue=@($first,$second); PositionMs=15000} } |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $profilePath 'library.json') -Encoding utf8

# This check path skips notification-icon registration entirely. It never takes
# over a running installation's tray GUID, library, account or audio preferences.
$keys = @('LUMA_DATA_DIRECTORY','LUMA_STARTUP_TRACE','LUMA_STARTUP_DIAGNOSTIC','LUMA_NATIVE_CHECK_REPORT','LUMA_UI_CHECK_REPORT')
$previous = @{}
foreach ($key in $keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
$process = $null
try {
    $env:LUMA_DATA_DIRECTORY = $profilePath
    $env:LUMA_STARTUP_TRACE = Join-Path $testRoot 'startup.trace'
    $env:LUMA_STARTUP_DIAGNOSTIC = Join-Path $testRoot 'startup.error'
    $env:LUMA_NATIVE_CHECK_REPORT = $null
    $env:LUMA_UI_CHECK_REPORT = $reportPath
    $process = Start-Process -FilePath (Join-Path $appPath 'LumaMusic.exe') -WindowStyle Hidden -PassThru
} finally {
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
}
try {
    if (-not $process.WaitForExit(60000)) { throw "原生界面检查超时，日志位于 $testRoot。" }
    if ($process.ExitCode -ne 0) { throw "原生检查进程异常退出：$($process.ExitCode)，详见 $testRoot。" }
    if (-not (Test-Path -LiteralPath $reportPath)) { throw "检查未生成报告，详见 $testRoot。" }
    $result = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($result.result -ne 'Passed') { throw "原生界面检查失败：$reportPath" }
    if (Test-Path -LiteralPath (Join-Path $testRoot 'startup.error')) { throw "启动出现未处理异常：$testRoot\startup.error。" }
    [pscustomobject]@{ Result='Passed'; Report=$reportPath; Checks=$result.checks; Screenshots=$result.screenshots }
} finally {
    if ($process -and -not $process.HasExited) {
        $child = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($child -and $child.Path -eq (Join-Path $appPath 'LumaMusic.exe')) { Stop-Process -Id $child.Id }
    }
}
