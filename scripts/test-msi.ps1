#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$PackagePath,
    [switch]$RunInstallation,
    [switch]$RecoverPreviousTest
)

$ErrorActionPreference = 'Stop'
if (-not ('LumaMsiChecks.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace LumaMsiChecks {
    public static class Native {
        [DllImport("msi.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        private static extern uint MsiGetProductInfoExW(string product, string sid, uint context, string property, StringBuilder value, ref uint size);
        [DllImport("msi.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        private static extern uint MsiEnumRelatedProductsW(string upgrade, uint reserved, uint index, StringBuilder product);
        public static string ProductInfo(string product, string property) {
            var value = new StringBuilder(4096);
            uint size = 4096;
            uint result = MsiGetProductInfoExW(product, null, 2, property, value, ref size);
            if (result == 1605) return null;
            if (result != 0) throw new Win32Exception((int)result, "MsiGetProductInfoEx: " + property);
            return value.ToString();
        }
        public static string[] RelatedProducts(string upgrade) {
            var products = new List<string>();
            for (uint index = 0; ; index++) {
                var product = new StringBuilder(39);
                uint result = MsiEnumRelatedProductsW(upgrade, 0, index, product);
                if (result == 259) return products.ToArray();
                if (result != 0) throw new Win32Exception((int)result, "MsiEnumRelatedProducts");
                products.Add(product.ToString());
            }
        }
    }
}
'@
}
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $PackagePath) { $PackagePath = Join-Path $repoRoot 'artifacts\msi\LumaMusic_1.0.8_x64.msi' }
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$workPath = Join-Path $repoRoot '.build\msi'
$targetPath = [IO.Path]::GetFullPath((Join-Path $repoRoot '.build\msi-smoke-target'))
$startMenuPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Luma Music'
$reportPath = Join-Path $repoRoot 'artifacts\msi\installation-test.json'
New-Item -ItemType Directory -Path $workPath -Force | Out-Null

function Read-MsiRows($Database, [string]$Sql, [int]$Columns) {
    $view = $Database.OpenView($Sql)
    try {
        [void]$view.Execute()
        while ($record = $view.Fetch()) {
            $row = for ($column = 1; $column -le $Columns; $column++) { $record.StringData($column) }
            Write-Output -NoEnumerate $row
        }
    } finally { [void]$view.Close() }
}

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($PackagePath, 0)
$properties = @{}
foreach ($row in (Read-MsiRows $database 'SELECT `Property`, `Value` FROM `Property`' 2)) { $properties[$row[0]] = $row[1] }
$fileRows = @(Read-MsiRows $database 'SELECT `File`, `Component_`, `FileName`, `FileSize` FROM `File`' 4)
$componentRows = @(Read-MsiRows $database 'SELECT `Component`, `Directory_`, `Attributes`, `KeyPath` FROM `Component`' 4)
$shortcuts = @(Read-MsiRows $database 'SELECT `Shortcut`, `Target`, `Arguments` FROM `Shortcut`' 3)
$launchConditions = @(Read-MsiRows $database 'SELECT `Condition`, `Description` FROM `LaunchCondition`' 2)
if ($properties.ProductName -ne 'Luma Music' -or $properties.UpgradeCode -ne '{7EFC0CA9-A4B3-45CB-AB11-C7F1B35E22AF}') { throw 'MSI 产品身份不正确。' }
if ($properties.ALLUSERS) { throw 'MSI 不应请求 per-machine 安装。' }
if ($properties.ProductLanguage -ne '2052') { throw 'MSI 应使用中文安装向导。' }
if (-not ($launchConditions | Where-Object { $_[0] -match 'WINDOWSBUILDNUMBER' })) { throw 'Windows 版本条件未使用实际注册表构建号。' }
if ($componentRows | Where-Object { ([int]$_[2] -band 4) -eq 0 }) { throw '每个 per-user 组件应使用注册表 keypath。' }
if ($shortcuts.Count -lt 2) { throw 'MSI 缺少启动或卸载快捷方式。' }
$summary = [ordered]@{
    package=$PackagePath; productCode=$properties.ProductCode; upgradeCode=$properties.UpgradeCode
    version=$properties.ProductVersion; language=$properties.ProductLanguage
    files=$fileRows.Count; components=$componentRows.Count; scope='perUser'
    metadataValidation='Passed'; installation='Not requested'; appLaunched=$false; certificateStoreChanged=$false
}
if (-not $RunInstallation) { [pscustomobject]$summary; return }

$registryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($properties.ProductCode)"
$registered = @([LumaMsiChecks.Native]::RelatedProducts($properties.UpgradeCode))
if ($registered.Count) { throw '此用户已安装 Luma Music MSI；停止隔离测试，以免触发升级或改变现有安装。' }
if (Test-Path -LiteralPath $targetPath) {
    if ((Get-Item -LiteralPath $targetPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '隔离目标不能是重解析点。' }
    if (Get-ChildItem -LiteralPath $targetPath -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) { throw '隔离目标内含重解析点，停止测试。' }
}
if ((Test-Path -LiteralPath $targetPath) -and @(Get-ChildItem -LiteralPath $targetPath -Force).Count) {
    if (-not $RecoverPreviousTest) { throw '隔离目标目录不为空，已停止；不会删除既有文件。' }
    foreach ($existingFile in (Get-ChildItem -LiteralPath $targetPath -File -Recurse -Force)) {
        $relative = [IO.Path]::GetRelativePath($targetPath, $existingFile.FullName)
        $expected = if ($relative -eq 'LICENSE-WiX.txt') { Join-Path $repoRoot 'installer\THIRD_PARTY_WIX.txt' } else { Join-Path $repoRoot "artifacts\win-x64\publish\$relative" }
        if (-not (Test-Path -LiteralPath $expected) -or
            (Get-FileHash -LiteralPath $existingFile.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $expected -Algorithm SHA256).Hash) {
            throw "隔离目录含有非预期或已修改文件：$relative，停止恢复测试。"
        }
    }
}
if ((Test-Path -LiteralPath $startMenuPath) -and @(Get-ChildItem -LiteralPath $startMenuPath -Force).Count) { throw '开始菜单已有 Luma Music 文件，已停止；不会覆盖既有快捷方式。' }
if (-not $targetPath.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '隔离测试目标越出工作区。' }
$installLog = Join-Path $workPath 'install-smoke.log'
$uninstallLog = Join-Path $workPath 'uninstall-smoke.log'
$installed = $false
$summary.installation = 'In progress'
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding utf8
try {
    $arguments = '/i "{0}" /qn /norestart INSTALLFOLDER="{1}" /L*V "{2}"' -f $PackagePath, $targetPath, $installLog
    $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    $summary.installExitCode = $process.ExitCode
    if ($process.ExitCode -notin @(0, 3010)) { throw "MSI 隔离安装失败：$($process.ExitCode)，详见 $installLog。" }
    $installed = $true
    # MSI per-user ARP registration can live under Installer/UserData rather
    # than HKCU/Uninstall. Query the official current-user MSI context API.
    if ([LumaMsiChecks.Native]::ProductInfo($properties.ProductCode, 'State') -ne '5') { throw '当前用户的 MSI 产品未注册为已安装。' }
    if ([LumaMsiChecks.Native]::ProductInfo($properties.ProductCode, 'InstalledProductName') -ne 'Luma Music' -or
        [LumaMsiChecks.Native]::ProductInfo($properties.ProductCode, 'VersionString') -ne $properties.ProductVersion) { throw '卸载注册信息不匹配。' }
    $installedLocation = [LumaMsiChecks.Native]::ProductInfo($properties.ProductCode, 'InstallLocation')
    if ($installedLocation.TrimEnd([char]92) -ne $targetPath.TrimEnd([char]92)) { throw '卸载项未记住实际安装目录。' }
    $summary.installationContext = 'Current user, MSIINSTALLCONTEXT_USERUNMANAGED'
    if (-not (Test-Path -LiteralPath (Join-Path $targetPath 'LumaMusic.exe'))) { throw '未安装主程序。' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $startMenuPath 'Luma Music.lnk'))
    if ([IO.Path]::GetFullPath($shortcut.TargetPath) -ne (Join-Path $targetPath 'LumaMusic.exe')) { throw '开始菜单启动目标不匹配。' }
    $uninstallShortcut = $shell.CreateShortcut((Join-Path $startMenuPath '卸载 Luma Music.lnk'))
    if ($uninstallShortcut.Arguments -ne "/x $($properties.ProductCode)") { throw '卸载快捷方式 ProductCode 不匹配。' }
    $publishPath = Join-Path $repoRoot 'artifacts\win-x64\publish'
    $hashChecked = 0
    foreach ($file in (Get-ChildItem -LiteralPath $targetPath -File -Recurse)) {
        $relative = [IO.Path]::GetRelativePath($targetPath, $file.FullName)
        $sourceFile = if ($relative -eq 'LICENSE-WiX.txt') { Join-Path $repoRoot 'installer\THIRD_PARTY_WIX.txt' } else { Join-Path $publishPath $relative }
        if (-not (Test-Path -LiteralPath $sourceFile)) { throw "出现发布载荷外的安装文件：$relative。" }
        if ((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash) { throw "安装文件校验不匹配：$relative。" }
        $hashChecked++
    }
    if ($hashChecked -ne $fileRows.Count) { throw "安装文件数量不匹配：$hashChecked / $($fileRows.Count)。" }
    $summary.installedFilesVerified = $hashChecked
    $summary.shortcutValidation = 'Passed'
    $summary.uninstallEntryValidation = 'Passed'
    $summary.installation = 'Passed'
} catch {
    $summary.installation = 'Failed'
    $summary.failure = $_.Exception.Message
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding utf8
    throw
} finally {
    if ($installed -or [LumaMsiChecks.Native]::ProductInfo($properties.ProductCode, 'State')) {
        $arguments = '/x "{0}" /qn /norestart /L*V "{1}"' -f $properties.ProductCode, $uninstallLog
        $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\msiexec.exe') -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        $summary.uninstallExitCode = $process.ExitCode
        if ($process.ExitCode -notin @(0, 3010)) { throw "MSI 测试卸载失败：$($process.ExitCode)，详见 $uninstallLog。" }
        if ([LumaMsiChecks.Native]::ProductInfo($properties.ProductCode, 'State') -or (Test-Path -LiteralPath $registryPath) -or (Test-Path -LiteralPath $targetPath) -or (Test-Path -LiteralPath $startMenuPath)) {
            $summary.uninstallation = 'Failed: installation directory, shortcut directory or uninstall registration remains'
            $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding utf8
            throw '测试卸载后仍有安装目录、开始菜单目录或卸载注册项残留。'
        }
        $summary.uninstallation = 'Passed'
    }
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportPath -Encoding utf8
}
[pscustomobject]$summary
