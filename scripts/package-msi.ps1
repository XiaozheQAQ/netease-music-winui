#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.7',
    [switch]$SkipPublish,
    [switch]$SkipPrepare,
    [switch]$NoRestore,
    [switch]$SkipToolRestore
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$publishPath = Join-Path $repoRoot 'artifacts\win-x64\publish'
$outputPath = Join-Path $repoRoot 'artifacts\msi'
$workPath = Join-Path $repoRoot '.build\msi'
$toolManifest = Join-Path $repoRoot '.config\dotnet-tools.json'
New-Item -ItemType Directory -Path $outputPath, $workPath -Force | Out-Null
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.build\dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$productVersion = [Version]$Version
if ($productVersion.Major -gt 255 -or $productVersion.Minor -gt 255 -or $productVersion.Build -gt 65535) {
    throw 'MSI 版本必须为 Major.Minor.Build，前两项不超过255，第三项不超过65535。'
}
if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Architecture x64 -SkipPrepare:$SkipPrepare -NoRestore:$NoRestore
}
foreach ($relativePath in @('LumaMusic.exe', 'LumaMusic.dll', 'LumaMusic.pri', 'LumaMusic.runtimeconfig.json',
    'coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll', 'Assets\Luma.ico', 'LICENSE.txt',
    'MusicSource\runtime\node.exe', 'MusicSource\runtime\NODE-LICENSE.txt',
    'MusicSource\service.cjs', 'MusicSource\bridge.cjs')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $relativePath) -PathType Leaf)) {
        throw "文件夹版发布载荷缺失：$relativePath。请先执行 scripts/publish.ps1。"
    }
}
$nodeMetadata = Get-Content -LiteralPath (Join-Path $publishPath 'MusicSource\runtime\version.json') -Raw | ConvertFrom-Json
if ($nodeMetadata.architecture -ne 'x64') { throw '此 MSI 需要 x64 Node.js 运行时。' }
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishPath 'LumaMusic.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtimeConfig.runtimeOptions.includedFrameworks) { throw 'MSI 载荷必须包含自包含 .NET 运行时。' }

function Get-StableId([string]$Prefix, [string]$Path) {
    $pathBytes = [Text.Encoding]::UTF8.GetBytes($Path.Replace('\', '/').ToLowerInvariant())
    return $Prefix + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pathBytes)).Substring(0, 28)
}
function Get-StableGuid([string]$Path) {
    $name = '7EFC0CA9-A4B3-45CB-AB11-C7F1B35E22AF:' + $Path.Replace('\', '/').ToLowerInvariant()
    $bytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($name))
    # Name-derived UUID. The fixed namespace and path make this independent of
    # the source folder, build machine, product version and build ordering.
    $bytes[6] = ($bytes[6] -band 15) -bor 128
    $bytes[8] = ($bytes[8] -band 63) -bor 128
    $hex = [Convert]::ToHexString($bytes).Substring(0, 32)
    return [Guid]::ParseExact($hex, 'N').ToString('D')
}

$payload = [Collections.Generic.List[object]]::new()
foreach ($file in (Get-ChildItem -LiteralPath $publishPath -File -Recurse | Sort-Object FullName)) {
    $relativePath = [IO.Path]::GetRelativePath($publishPath, $file.FullName)
    $normalized = $relativePath.Replace('\', '/')
    if ($normalized -eq 'MusicSource/node_modules/@unblockneteasemusic/server/server.key') { continue }
    if ($normalized.StartsWith('MusicSource/node_modules/', [StringComparison]::OrdinalIgnoreCase) -and $file.Name -eq '.env.example') { continue }
    $isPrivateAppData = -not $normalized.StartsWith('MusicSource/node_modules/', [StringComparison]::OrdinalIgnoreCase) -and
        ($file.Name -match '^(account\.dat|library\.json|cookies?\.(json|dat|txt)|session\.(json|dat))$' -or $file.Extension -eq '.key')
    if ($isPrivateAppData -or $file.Name -like '.env*' -or $file.Extension -in @('.pfx', '.dpapi') -or
        $normalized -match '(^|/)\.(git|build)(/|$)') { throw "载荷中含有不应分发的私有文件：$relativePath。" }
    $payload.Add([pscustomobject]@{ Source = $file.FullName; RelativePath = $relativePath; Component = Get-StableId 'C' $relativePath })
}
$wixLicensePath = Join-Path $repoRoot 'installer\THIRD_PARTY_WIX.txt'
if (-not (Test-Path -LiteralPath $wixLicensePath)) { throw '缺少 WiX 安装器第三方许可声明。' }
$payload.Add([pscustomobject]@{ Source = $wixLicensePath; RelativePath = 'LICENSE-WiX.txt'; Component = Get-StableId 'C' 'LICENSE-WiX.txt' })

# Explicit harvesting works with the pinned open-source WiX 4 tool. Every file
# has a stable component identifier and an HKCU registry key path for per-user
# installation. Uninstall removes package-owned files and empty directories only.
$wixNamespace = 'http://wixtoolset.org/schemas/v4/wxs'
$document = [xml]'<?xml version="1.0" encoding="utf-8"?><Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Fragment><DirectoryRef Id="INSTALLFOLDER" /></Fragment><Fragment><ComponentGroup Id="PublishedPayload" /></Fragment></Wix>'
$directoryNodes = @{ '' = $document.Wix.Fragment[0].DirectoryRef }
$componentGroup = $document.Wix.Fragment[1].ComponentGroup
foreach ($item in $payload) {
    $directory = [IO.Path]::GetDirectoryName($item.RelativePath)
    $segments = if ($directory) { $directory -split '[\\/]' } else { @() }
    $parent = $directoryNodes['']
    $currentPath = ''
    foreach ($segment in $segments) {
        $currentPath = if ($currentPath) { "$currentPath\$segment" } else { $segment }
        if (-not $directoryNodes.ContainsKey($currentPath)) {
            $node = $document.CreateElement('Directory', $wixNamespace)
            $node.SetAttribute('Id', (Get-StableId 'D' $currentPath))
            $node.SetAttribute('Name', $segment)
            [void]$parent.AppendChild($node)
            $directoryNodes[$currentPath] = $node
        }
        $parent = $directoryNodes[$currentPath]
    }
    $component = $document.CreateElement('Component', $wixNamespace)
    $component.SetAttribute('Id', $item.Component)
    $component.SetAttribute('Guid', (Get-StableGuid ('file:' + $item.RelativePath)))
    $fileNode = $document.CreateElement('File', $wixNamespace)
    $fileNode.SetAttribute('Id', (Get-StableId 'F' $item.RelativePath))
    $fileNode.SetAttribute('Source', $item.Source)
    $fileNode.SetAttribute('Name', [IO.Path]::GetFileName($item.RelativePath))
    $fileNode.SetAttribute('KeyPath', 'no')
    [void]$component.AppendChild($fileNode)
    $registry = $document.CreateElement('RegistryValue', $wixNamespace)
    $registry.SetAttribute('Root', 'HKCU')
    $registry.SetAttribute('Key', 'Software\XiaozheQAQ\LumaMusic\Installer\Files')
    $registry.SetAttribute('Name', $item.Component)
    $registry.SetAttribute('Type', 'integer')
    $registry.SetAttribute('Value', '1')
    $registry.SetAttribute('KeyPath', 'yes')
    [void]$component.AppendChild($registry)
    [void]$parent.AppendChild($component)
    $reference = $document.CreateElement('ComponentRef', $wixNamespace)
    $reference.SetAttribute('Id', $item.Component)
    [void]$componentGroup.AppendChild($reference)
}
foreach ($directory in @($directoryNodes.Keys | Where-Object {$_} | Sort-Object)) {
    $component = $document.CreateElement('Component', $wixNamespace)
    $cleanupId = Get-StableId 'R' $directory
    $component.SetAttribute('Id', $cleanupId)
    $component.SetAttribute('Guid', (Get-StableGuid ('folder:' + $directory)))
    $remove = $document.CreateElement('RemoveFolder', $wixNamespace)
    $remove.SetAttribute('Id', (Get-StableId 'RF' $directory))
    $remove.SetAttribute('On', 'uninstall')
    [void]$component.AppendChild($remove)
    $registry = $document.CreateElement('RegistryValue', $wixNamespace)
    foreach ($pair in @{ Root='HKCU'; Key='Software\XiaozheQAQ\LumaMusic\Installer\Folders'; Name=$cleanupId; Type='integer'; Value='1'; KeyPath='yes' }.GetEnumerator()) { $registry.SetAttribute($pair.Key, $pair.Value) }
    [void]$component.AppendChild($registry)
    [void]$directoryNodes[$directory].AppendChild($component)
    $reference = $document.CreateElement('ComponentRef', $wixNamespace)
    $reference.SetAttribute('Id', $cleanupId)
    [void]$componentGroup.AppendChild($reference)
}
$payloadSource = Join-Path $workPath 'Payload.wxs'
$document.Save($payloadSource)

$licenseText = Get-Content -LiteralPath (Join-Path $repoRoot 'LICENSE.txt') -Raw
$escapedLicense = $licenseText.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}').Replace("`r", '').Replace("`n", '\par ')
$licensePath = Join-Path $workPath 'License.rtf'
('{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs20 ' + $escapedLicense + '}') |
    Set-Content -LiteralPath $licensePath -Encoding ascii

$previousRollForward = $env:DOTNET_ROLL_FORWARD
Push-Location $repoRoot
try {
    # WiX 4 targets .NET 6. Permit the build tool to use a newer installed
    # runtime on SDK 8 machines without installing an extra framework.
    $env:DOTNET_ROLL_FORWARD = 'Major'
    if (-not $SkipToolRestore) {
        & dotnet tool restore --tool-manifest $toolManifest --add-source https://api.nuget.org/v3/index.json --ignore-failed-sources
        if ($LASTEXITCODE -ne 0) { throw 'WiX 工具恢复失败。' }
        & dotnet tool run wix -- extension add WixToolset.UI.wixext/4.0.6
        if ($LASTEXITCODE -ne 0) { throw 'WiX 标准安装界面扩展恢复失败。' }
    }
    $msiPath = Join-Path $outputPath "LumaMusic_$Version`_x64.msi"
    $buildArguments = @('tool', 'run', 'wix', '--', 'build',
        (Join-Path $repoRoot 'installer\Package.wxs'), $payloadSource,
        '-arch', 'x64', '-culture', 'zh-CN', '-ext', 'WixToolset.UI.wixext/4.0.6',
        '-d', "ProductVersion=$Version", '-d', "PublishDir=$publishPath", '-d', "LicenseRtf=$licensePath",
        '-intermediateFolder', (Join-Path $workPath 'obj'), '-o', $msiPath)
    $buildLog = & dotnet @buildArguments 2>&1
    $buildExit = $LASTEXITCODE
    $buildLogText = ($buildLog | Out-String).TrimEnd()
    if (-not $buildLogText -and $buildExit -eq 0) { $buildLogText = 'WiX build succeeded. Native Windows Installer validation completed without warnings.' }
    Set-Content -LiteralPath (Join-Path $outputPath 'wix-build.log') -Value $buildLogText -Encoding utf8
    if ($buildExit -ne 0) { $buildLog | Select-Object -First 20 | Write-Host; throw "MSI 构建失败（$buildExit），详见 artifacts/msi/wix-build.log。" }
} finally {
    [Environment]::SetEnvironmentVariable('DOTNET_ROLL_FORWARD', $previousRollForward, [EnvironmentVariableTarget]::Process)
    Pop-Location
}

$hash = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($msiPath))" | Set-Content -LiteralPath (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding ascii
@{
    name='Luma Music'; version=$Version; architecture='x64'; installer='Windows Installer MSI'
    scope='perUser'; defaultDirectory='%LOCALAPPDATA%\Programs\Luma Music'
    upgradeCode='7EFC0CA9-A4B3-45CB-AB11-C7F1B35E22AF'; payloadFiles=$payload.Count
    sha256=$hash; signed=$false; certificateImportRequired=$false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'package-info.json') -Encoding utf8
@"
Luma Music $Version (x64) - MSI

双击 $([IO.Path]::GetFileName($msiPath)) 安装。此版本不要求导入任何证书。
默认安装到当前用户的 %LOCALAPPDATA%\Programs\Luma Music，开始菜单中提供启动和卸载入口。
也可在 Windows“设置 → 应用”卸载。卸载保留本机收藏、历史与设置。

这是未签名的 MSI；Windows/SmartScreen 仍可能显示“未知发布者”等安全提示。
包内包含 .NET、Windows App SDK、Node.js 与音源依赖，无需额外安装这些运行时。

SHA-256：$hash

静默安装（不自动启动）：msiexec /i "$([IO.Path]::GetFileName($msiPath))" /qn /norestart
安装日志：msiexec /i "$([IO.Path]::GetFileName($msiPath))" /L*V install.log
"@ | Set-Content -LiteralPath (Join-Path $outputPath 'INSTALL.txt') -Encoding utf8
Write-Host "MSI：$msiPath"
Write-Host "SHA-256：$hash"
Write-Host '无需导入证书；MSI 未签名，Windows 可能显示未知发布者提示。'
