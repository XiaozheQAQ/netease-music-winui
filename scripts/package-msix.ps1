#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version,
    [string]$CertificatePath,
    [SecureString]$CertificatePassword,
    [string]$SdkToolsPath,
    [switch]$SkipPrepare,
    [switch]$NoRestore,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$outputPath = Join-Path $repoRoot 'artifacts\msix'
$workPath = Join-Path $repoRoot ".build\msix\$Architecture"
$payloadRecord = Join-Path $workPath 'payload-path.txt'
New-Item -ItemType Directory -Path $outputPath, $workPath -Force | Out-Null

function Find-SdkTools {
    if ($SdkToolsPath) {
        $specified = [IO.Path]::GetFullPath($SdkToolsPath)
        if ((Test-Path -LiteralPath (Join-Path $specified 'makeappx.exe')) -and
            (Test-Path -LiteralPath (Join-Path $specified 'signtool.exe'))) { return $specified }
        throw 'SdkToolsPath 必须同时包含 makeappx.exe 与 signtool.exe。'
    }
    $hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $candidates = [Collections.Generic.List[string]]::new()
    $installedSdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $installedSdk) {
        foreach ($sdkVersion in (Get-ChildItem -LiteralPath $installedSdk -Directory | Sort-Object Name -Descending)) {
            $candidates.Add((Join-Path $sdkVersion.FullName $hostArchitecture))
        }
    }
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $sdkPackage = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools'
    if (Test-Path -LiteralPath $sdkPackage) {
        foreach ($sdkVersion in (Get-ChildItem -LiteralPath $sdkPackage -Directory | Sort-Object Name -Descending)) {
            $binPath = Join-Path $sdkVersion.FullName 'bin'
            if (Test-Path -LiteralPath $binPath) {
                foreach ($toolVersion in (Get-ChildItem -LiteralPath $binPath -Directory | Sort-Object Name -Descending)) {
                    $candidates.Add((Join-Path $toolVersion.FullName $hostArchitecture))
                }
            }
        }
    }
    foreach ($candidate in $candidates) {
        if ((Test-Path -LiteralPath (Join-Path $candidate 'makeappx.exe')) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'signtool.exe'))) { return $candidate }
    }
    throw '未找到 Windows SDK MakeAppx / SignTool。请恢复项目 NuGet 依赖或指定 -SdkToolsPath。'
}

if (-not $SkipPublish) {
    if (-not $SkipPrepare) { & (Join-Path $PSScriptRoot 'prepare-source.ps1') -Architecture $Architecture }
    if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.build\dotnet' }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $publishPath = Join-Path $workPath "payload-$([Guid]::NewGuid().ToString('N'))"
    # Build the same WinUI project in packaged mode so resources.pri has the
    # package identity as its resource-map root, rather than an unpackaged index.
    $publishOptions = @(
        'publish', (Join-Path $repoRoot 'winuitest\winuitest.csproj'), '-c', 'Release',
        "-p:Platform=$Architecture", '-r', "win-$Architecture", '-p:WindowsPackageType=MSIX',
        '-p:WindowsAppSDKSelfContained=true', '-p:PublishReadyToRun=false',
        '-p:GenerateAppxPackageOnBuild=false', '-p:PublishAppxPackage=false',
        '-p:AppxPackageSigningEnabled=false', '-p:MSBuildEnableWorkloadResolver=false',
        '--self-contained', 'true', '-o', $publishPath
    )
    if ($NoRestore) { $publishOptions += '--no-restore' }
    & dotnet @publishOptions
    if ($LASTEXITCODE -ne 0) { throw "MSIX 载荷发布失败（$LASTEXITCODE）。" }
    $publishPath | Set-Content -LiteralPath $payloadRecord -Encoding utf8
} else {
    if (-not (Test-Path -LiteralPath $payloadRecord)) { throw '没有已发布的 MSIX 载荷，请先运行脚本而不加 -SkipPublish。' }
    $publishPath = [IO.Path]::GetFullPath((Get-Content -LiteralPath $payloadRecord -Raw).Trim())
    $payloadRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.build\msix')) + [IO.Path]::DirectorySeparatorChar
    if (-not $publishPath.StartsWith($payloadRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '缓存的 MSIX 载荷路径不在专用构建目录。' }
}

$requiredPayload = @(
    'LumaMusic.exe', 'LumaMusic.dll', 'LumaMusic.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll',
    'Microsoft.UI.Xaml.dll', 'resources.pri', 'Assets\Luma.ico', 'LICENSE.txt',
    'Assets\Fonts\FluentSystemIcons-Filled.ttf', 'Assets\Fonts\LICENSE-FluentSystemIcons.txt',
    'Assets\Fonts\ResourceHanRoundedCN-Heavy.ttf', 'Assets\Fonts\ResourceHanRoundedCN-Bold.ttf',
    'Assets\Fonts\LICENSE-ResourceHanRounded.txt',
    'MusicSource\service.cjs', 'MusicSource\bridge.cjs',
    'MusicSource\runtime\node.exe', 'MusicSource\runtime\NODE-LICENSE.txt', 'MusicSource\runtime\version.json',
    'MusicSource\node_modules\@neteasecloudmusicapienhanced\api\package.json'
)
foreach ($relativePath in $requiredPayload) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $relativePath) -PathType Leaf)) {
        throw "发布文件缺失：$relativePath。请重新执行脚本，不使用 -SkipPublish。"
    }
}
$nodeVersion = Get-Content -LiteralPath (Join-Path $publishPath 'MusicSource\runtime\version.json') -Raw | ConvertFrom-Json
if ($nodeVersion.architecture -ne $Architecture) { throw 'Node.js 与 MSIX 目标架构不一致，请重新准备对应架构运行时。' }

$manifest = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'winuitest\Package.appxmanifest') -Raw)
$identity = $manifest.Package.Identity
if ($Version) { $identity.SetAttribute('Version', $Version) }
$packageVersion = [Version]$identity.Version
foreach ($component in @($packageVersion.Major, $packageVersion.Minor, $packageVersion.Build, $packageVersion.Revision)) {
    if ($component -lt 0 -or $component -gt 65535) { throw 'MSIX 版本的四个分量必须在 0 到 65535 之间。' }
}
if ($packageVersion.Major -eq 0) { throw 'MSIX 主版本必须大于 0。' }
$identity.SetAttribute('ProcessorArchitecture', $Architecture)
$application = $manifest.Package.Applications.Application
$application.SetAttribute('Executable', 'LumaMusic.exe')
$application.SetAttribute('EntryPoint', 'Windows.FullTrustApplication')
$manifestPath = Join-Path $workPath 'AppxManifest.xml'
$manifest.Save($manifestPath)
$publisher = $identity.Publisher

# Create a development signing certificate entirely in memory. No certificate
# store is opened and no trust setting is changed by this script.
$developmentSigning = -not $CertificatePath
if ($developmentSigning) {
    $signingPath = Join-Path $repoRoot '.build\signing'
    New-Item -ItemType Directory -Path $signingPath -Force | Out-Null
    $CertificatePath = Join-Path $signingPath 'LumaMusic-Development.pfx'
    $passwordPath = Join-Path $signingPath 'LumaMusic-Development.password.dpapi'
    if (-not (Test-Path -LiteralPath $CertificatePath)) {
        $passwordBytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        $plainPassword = [Convert]::ToBase64String($passwordBytes)
        $CertificatePassword = ConvertTo-SecureString $plainPassword -AsPlainText -Force
        $privateKey = [Security.Cryptography.RSA]::Create(3072)
        try {
            $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
                $publisher, $privateKey, [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
            $usages = [Security.Cryptography.OidCollection]::new()
            [void]$usages.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $false))
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))
            $newCertificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(3))
            try {
                [IO.File]::WriteAllBytes($CertificatePath, $newCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $plainPassword))
                $CertificatePassword | ConvertFrom-SecureString | Set-Content -LiteralPath $passwordPath -Encoding ascii
            } finally { $newCertificate.Dispose() }
        } finally { $privateKey.Dispose(); $plainPassword = $null }
    } else {
        if (-not (Test-Path -LiteralPath $passwordPath)) { throw '开发签名密码文件缺失；请恢复 .build/signing 备份，或显式指定签名证书。' }
        $CertificatePassword = (Get-Content -LiteralPath $passwordPath -Raw).Trim() | ConvertTo-SecureString
    }
}
$CertificatePath = [IO.Path]::GetFullPath($CertificatePath)
$plainPassword = if ($CertificatePassword) { [Net.NetworkCredential]::new('', $CertificatePassword).Password } else { '' }
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    [IO.File]::ReadAllBytes($CertificatePath), $plainPassword,
    [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    if (-not $certificate.HasPrivateKey) { throw '签名证书没有私钥。' }
    if ($certificate.Subject -ne $publisher) { throw "签名证书 Subject 必须与 manifest Publisher 一致：$publisher。" }
    if ([DateTime]::UtcNow -lt $certificate.NotBefore.ToUniversalTime() -or [DateTime]::UtcNow -gt $certificate.NotAfter.ToUniversalTime()) { throw '签名证书已过期或尚未生效。' }
    $publicCertificatePath = Join-Path $outputPath 'LumaMusic-Signing.cer'
    [IO.File]::WriteAllBytes($publicCertificatePath, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    $certificateThumbprint = $certificate.Thumbprint
    $certificateExpiry = $certificate.NotAfter.ToUniversalTime().ToString('O')
} finally { $certificate.Dispose() }

$mapping = [Collections.Generic.List[string]]::new()
$mapping.Add('[Files]')
foreach ($file in (Get-ChildItem -LiteralPath $publishPath -File -Recurse | Sort-Object FullName)) {
    $relativePath = [IO.Path]::GetRelativePath($publishPath, $file.FullName)
    if ($relativePath -in @('AppxManifest.xml', 'AppxBlockMap.xml', 'AppxSignature.p7x', '[Content_Types].xml')) { continue }
    $normalizedPath = $relativePath.Replace('\', '/')
    # An unused upstream HTTPS-server sample key is public npm fixture data,
    # unrelated to this stdio adapter. Do not redistribute it as an app secret.
    if ($normalizedPath -eq 'MusicSource/node_modules/@unblockneteasemusic/server/server.key') { continue }
    if ($normalizedPath.StartsWith('MusicSource/node_modules/', [StringComparison]::OrdinalIgnoreCase) -and $file.Name -eq '.env.example') { continue }
    $isPrivateAppData = -not $normalizedPath.StartsWith('MusicSource/node_modules/', [StringComparison]::OrdinalIgnoreCase) -and
        ($file.Name -match '^(account\.dat|library\.json|cookies?\.(json|dat|txt)|session\.(json|dat))$' -or $file.Extension -eq '.key')
    if ($isPrivateAppData -or $file.Name -like '.env*' -or $relativePath -match '(^|[\\/])\.(git|build)([\\/]|$)' -or $file.Extension -in @('.pfx', '.dpapi')) {
        throw "发布目录中发现不应分发的文件：$relativePath。"
    }
    $mapping.Add(('"{0}" "{1}"' -f $file.FullName, $relativePath))
}
$mapping.Add(('"{0}" "AppxManifest.xml"' -f $manifestPath))
$mappingPath = Join-Path $workPath 'mapping.txt'
$mapping | Set-Content -LiteralPath $mappingPath -Encoding utf8
$toolsPath = Find-SdkTools
$makeAppx = Join-Path $toolsPath 'makeappx.exe'
$signTool = Join-Path $toolsPath 'signtool.exe'
$packagePath = Join-Path $outputPath "LumaMusic_$packageVersion`_$Architecture.msix"

# Default MakeAppx semantic/file validation stays enabled. Do not add /nv or /nfv.
$packLog = & $makeAppx pack /o /h SHA256 /f $mappingPath /p $packagePath 2>&1
$packExitCode = $LASTEXITCODE
$packLog | Set-Content -LiteralPath (Join-Path $outputPath 'makeappx.log') -Encoding utf8
if ($packExitCode -ne 0) { $packLog | Write-Host; throw "MakeAppx 失败（$packExitCode）。详见 artifacts/msix/makeappx.log。" }
$signOptions = @('sign', '/fd', 'SHA256', '/a', '/f', $CertificatePath)
if ($plainPassword) { $signOptions += @('/p', $plainPassword) }
$signOptions += $packagePath
$signLog = & $signTool @signOptions 2>&1
$signExitCode = $LASTEXITCODE
$plainPassword = $null
$signOptions = $null
$signLog | Set-Content -LiteralPath (Join-Path $outputPath 'signtool-sign.log') -Encoding utf8
if ($signExitCode -ne 0) { $signLog | Write-Host; throw "SignTool 签名失败（$signExitCode）。" }

$verification = & (Join-Path $PSScriptRoot 'verify-msix.ps1') -PackagePath $packagePath -CertificatePath $publicCertificatePath
$verification | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'verification.json') -Encoding utf8
$trustLog = & $signTool verify /pa /all /v $packagePath 2>&1
$trustExitCode = $LASTEXITCODE
$trustLog | Set-Content -LiteralPath (Join-Path $outputPath 'signtool-verify.log') -Encoding utf8
$trustState = if ($verification.WindowsTrustError -eq '0x00000000' -and $trustExitCode -eq 0) { 'TrustedByWindows' }
    elseif ($verification.WindowsTrustError -eq '0x800B0109' -and $verification.IsSelfSigned) { 'RequiresCertificateTrust' }
    else { 'VerificationFailed' }
if ($trustState -eq 'VerificationFailed') {
    $trustLog | Write-Host
    throw "Windows 签名验证失败（$($verification.WindowsTrustError)），且原因不是预期的未信任开发证书；已停止发布。"
}

$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
"$hash  $([IO.Path]::GetFileName($packagePath))" | Set-Content -LiteralPath (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding ascii
@{
    name = 'Luma Music'; identity = $identity.Name; version = $packageVersion.ToString(); architecture = $Architecture
    publisher = $publisher; certificateThumbprint = $certificateThumbprint; certificateExpiresUtc = $certificateExpiry
    developmentCertificate = $developmentSigning; windowsTrust = $trustState; windowsTrustError = $verification.WindowsTrustError; sha256 = $hash
    payloadFiles = $mapping.Count - 1; semanticValidation = 'MakeAppx default validation enabled'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'package-info.json') -Encoding utf8

@"
Luma Music $packageVersion ($Architecture)

1. 安装包：$([IO.Path]::GetFileName($packagePath))
2. 公开签名证书：LumaMusic-Signing.cer
3. SHA-256：$hash
4. 签名者：$publisher
5. 证书指纹：$certificateThumbprint

此包已签名并通过离线签名、签名保护的区块映射与载荷完整性检查。
Windows 信任状态：$trustState。

本次脚本没有安装应用、导入证书或更改系统信任。首次使用自签开发证书时，
请先核对发布来源与上方指纹，再手动打开 LumaMusic-Signing.cer，将公开证书安装到
“本地计算机 → 受信任的人”，然后双击 MSIX 安装。此步骤可能要求管理员权限。
仅信任公开 CER；私钥 PFX 不应分发或提交。公开商用分发可改用受信任代码签名证书或 Microsoft Store 签名。

包内包含 .NET、Windows App SDK、Node.js 与音源依赖。无需另外安装这些运行时。
没有时间戳服务器请求；签名有效期受证书有效期限制。发布开发更新时应保留同一签名私钥。
"@ | Set-Content -LiteralPath (Join-Path $outputPath 'INSTALL.txt') -Encoding utf8

Write-Host "MSIX：$packagePath"
Write-Host "证书：$publicCertificatePath"
Write-Host "SHA-256：$hash"
Write-Host "Windows 信任：$trustState（未修改系统信任）。"
