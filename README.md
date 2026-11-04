# Luma Music

从空白 WinUI 3 项目实现的 Windows 原生在线音乐播放器。界面以 Apple Music 的侧栏、编辑推荐卡片和常驻播放栏为灵感，使用 XAML 控件绘制。搜索、歌单、歌曲信息与播放地址来自 [NeteaseCloudMusicApiEnhanced/api-enhanced](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced)。

## 功能

- 品牌图标采用大红底与居中的白色长须小猫；启动时显示该标志的轻量载入动画，预加载首屏内容后平滑进入主界面，网络失败时提供重试状态。
- 发现页：云端推荐歌单、新歌；点击歌单查看并播放曲目。
- 搜索网易云音乐单曲，并将结果加入播放队列；接近列表底部自动分页加载，保留列表虚拟化。
- Windows 原生音频播放、暂停、上一首 / 下一首、进度拖动、音量、随机与循环播放。
- 系统托盘控制播放 / 暂停、上一首与下一首，可选择音频输出设备，并直接进入歌词、歌单、收藏和设置。
- Mica Alt 云母背景，通过透明根容器与半透明界面层显露系统材质。
- 浅色 / 深色主题与默认跟随系统；使用 WinUI 原生导航、按钮、列表选择和交互反馈。
- 默认窗口为 1120 × 740 DIP，最小为 940 × 620 DIP，并按屏幕工作区和 DPI 调整；设置页按可用宽度布局。
- 界面功能图标统一使用本机捆绑的 Microsoft Fluent System Icons 填充样式，窗口标题栏按钮保留 Windows 原生样式。
- 沉浸式同步歌词：左侧大封面与播放控制，右侧大字号歌词；支持真实 YRC 逐字高亮、翻译、音译和 LRC 逐行回退，以及点击歌词跳转、原生全屏。
- 网易云扫码登录；账号歌单分页同步至“我的歌单”，侧边栏提供常用歌单入口。
- 按账号实际权限请求标准、高品质、极高、无损或 Hi-Res 音质；显示实际取得的音质与试听状态。
- 当前播放信息、播放队列。
- 本机收藏、最近播放、主题与播放偏好持久化；保存并恢复上次曲目、队列和进度。
- 加载、空结果、网络或播放失败状态反馈。

未登录时使用游客接口。登录后播放请求携带账号会话，可使用该账号已经拥有的曲目和音质权限；会员、数字专辑购买、地区与单曲授权仍以网易云接口的实际返回为准。不会购买或开通会员，也不会绕过权限或切换到其他音乐平台。

## 网易云账号与音质

点击侧边栏顶部账号入口或“设置 → 网易云音乐账号”，使用网易云音乐 App 扫码，并在手机上确认。二维码支持刷新与过期重试；关闭或取消登录弹窗会丢弃未完成的登录。登录后“我的歌单”同步创建和收藏的歌单，点击“同步歌单”可重新读取云端内容。本机“收藏”和“最近播放”仍保存于当前设备，不会改写网易云账号。

登录凭据使用 Windows DPAPI 按当前 Windows 用户加密，保存于 `%LOCALAPPDATA%\LumaMusic\account.dat`，不会以明文写入本机资料库、日志或命令行。下次启动先恢复账号，再解析上次歌曲；退出登录会清除本机会话。临时网络故障不会被当成凭据过期而删除已保存的登录。

在设置中选择播放音质，默认请求无损；实际没有对应权限或曲目规格时回退到接口允许的音质。播放栏显示实际取得的规格。登录、退出或更改音质会重新解析当前曲目，尽量保留当前进度和播放/暂停状态。账号权限并不保证所有歌曲都可完整播放，试听状态会明确显示。

## 沉浸式歌词与快捷键

播放歌曲后，点击底部播放栏左侧的封面或歌曲信息，沉浸式歌词会从窗口底部滑入。该区域保留原生悬停、按下与键盘焦点反馈。大封面、歌曲信息、进度和播放控制位于左侧，歌词位于右侧，字号与布局随窗口宽高调整。背景使用封面色调营造柔和氛围，并在任意宽高比下铺满页面。自动跟随时，当前句上方只保留上一句的后半段，位置更靠上；长句优先保证可读，首句从顶部开始。翻译与罗马音字号为主歌词的约65%，限制在18–26px。换句上移带有轻微缩放，起势明确、随后自然减速；手动滚动立即打断，下一句开始时恢复追随，暂停时保留浏览位置。网易云提供翻译或音译时，会显示在原文下面；没有对应数据时不生成内容。

逐字高亮采用网易云返回的真实 YRC 字词时间戳，不人为推算逐字时间；未提供逐字数据时，自动回退到 LRC 逐行同步。歌词同步以 Windows 播放器的实际进度为基准，并计入音源返回的试听起点偏移。拖动进度或点击歌词跳转后，字词高亮会按实际播放位置重新定位。

非当前句及其字幕使用原生 GPU 高斯模糊，当前句保持清晰；手动滚动时立即取消全部模糊，下次自动对齐时平滑恢复。模糊资源仅为视口附近的歌词创建，退出歌词或切歌时释放。滚动和模糊动画均遵循系统动画偏好。

在播放选项中使用“歌词校准”的 `−0.5 s` / `+0.5 s`，分别让歌词提前或延后半秒，可重置。校准范围为 ±30 秒，按歌曲保存在本机，下次播放继续生效。校准只调整歌词时钟；点击歌词仍跳至修正后的对应音频位置，不改变保存的音频进度或试听起点。

点击歌词视图的全屏按钮或按 `F11`，切换原生全屏模式。按 `Esc` 先退出全屏；在窗口模式下再按一次，收起沉浸式歌词。

底栏播放控件始终居中，左右区域随窗口宽度收缩。主界面与歌词页音量统一为整数百分比，播放音量为 1–100%，0 为静音；方向键每次调整 1%，大步调整 5%。

歌曲列表的表头、歌曲行和骨架屏共用列布局；封面、歌名、专辑和时长遵循同一边线，空间不足时统一隐藏专辑。资料库、搜索和无封面页面的标题不再保留封面列空隙。待播清单展开后覆盖右侧内容区域，保留顶栏、左侧导航与底栏，按 `Esc` 或点击关闭恢复原页面。

快捷键在应用窗口内生效，不占用其他软件的系统全局热键。按 `F1` 或在“设置”中点击“键盘快捷键”查看帮助；自动快捷键悬浮提示仍隐藏。

| 快捷键 | 操作 |
| --- | --- |
| `Space` / `Ctrl+P` | 播放 / 暂停 |
| `Ctrl+←` / `Ctrl+→` | 上一首 / 下一首 |
| `←` / `→` | 后退 / 前进 5 秒 |
| `Ctrl+↑` / `Ctrl+↓` | 增减音量 |
| `Ctrl+M` | 静音 / 恢复 |
| `Ctrl+F` | 聚焦搜索 |
| `Ctrl+L` | 打开 / 收起歌词 |
| `Ctrl+Q` | 显示 / 收起待播清单 |
| `Ctrl+D` | 喜欢 / 取消喜欢当前歌曲 |
| `Ctrl+Shift+S` | 随机播放 |
| `Ctrl+R` | 切换循环模式 |
| `Ctrl+,` | 打开设置 |
| `F5` | 刷新当前页面 |
| `F11` | 进入 / 退出全屏歌词 |
| `Esc` | 退出全屏或收起歌词 |
| `F1` | 快捷键帮助 |

空格在应用内优先控制播放 / 暂停，即使焦点停在封面、歌曲信息、按钮或滑块上，也不会激活这些控件。输入框保留正常空格输入；方向键保留列表、滑块与文字编辑行为。长按空格不会反复切换播放状态。登录和快捷键对话框打开时，不触发背后的播放器操作。

## 主题与播放恢复

在左侧“设置”中选择应用主题：默认“跟随系统”，也可切换为“浅色”或“深色”。跟随系统时会响应 Windows 主题变化；手动主题与其他播放偏好保存在本机，下次启动继续使用。

播放会话每 5 秒及正常关闭时保存。下次启动恢复上次曲目、播放队列与进度，并保持暂停，点击播放即可继续聆听。

从系统托盘打开控制菜单，可播放 / 暂停、切换上一首 / 下一首。“正在播放”打开沉浸式歌词，“我的歌单”“喜欢的歌曲”和“设置”分别恢复主窗口并进入对应页面。菜单中的“输出设备”可选择系统默认输出或指定音频设备；选择保存在本机，下次启动继续使用。应用内也可用 `Ctrl + Shift + T` 打开菜单，空格始终控制播放 / 暂停。

侧栏、按钮、选项切换、列表和滑块沿用 WinUI 原生的悬停、按下、选中与禁用状态。页面切换、歌词视图及活动歌词行使用轻量原生动画，并遵循系统动画偏好。

发现、搜索、歌单与云端歌单加载时显示与内容布局对应的骨架屏，沿用页面背景与主题配色。加载结束后淡入内容；快速切换页面会取消旧请求，避免旧结果和空状态叠加。侧栏歌单紧接“我的歌单”，与导航共用自然滚动区域，设置固定在底部。歌单卡片使用更大的原生圆角和内边距，列宽按实际可用空间自适应，包括打开待播清单时。

## 架构

```text
WinUI 3 / XAML
  ├─ NeteaseMusicClient → NodeMusicHost
  │                       └─ node.exe MusicSource/service.cjs
  │                          └─ @neteasecloudmusicapienhanced/api
  ├─ PlaybackService → Windows.Media.Playback.MediaPlayer
  ├─ LibraryStore → 本机 JSON 收藏、历史与偏好
  └─ AccountSessionStore → Windows DPAPI 加密登录凭据
```

Node.js 只有一个音源子进程，使用 JSON Lines 标准输入 / 输出 RPC，不承载 UI、不开放 HTTP 端口。应用关闭时结束子进程。C# 负责界面、交互、队列与媒体播放；音频直接交给 Windows 播放器。

## 环境

- Windows 10 19041 或更新版本，推荐 Windows 11。
- Visual Studio 的 WinUI / .NET 桌面开发环境与 Windows SDK；项目目标为 .NET 8。
- 准备依赖时需要 Node.js 22+、npm 和网络访问；正常发布版会捆绑 `node.exe`，使用者无需安装 Node.js。
- NuGet 依赖：Windows App SDK 2.6.4、Windows SDK BuildTools 10.0.28000.2957、Win2D 1.4.0。

Mica Alt 在支持该材质的 Windows 11 环境中呈现壁纸色调。系统不支持、透明效果受系统策略限制或窗口失焦时，WinUI 会自动使用纯色回退，应用仍可正常使用。

## 首次运行

在解决方案目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\prepare-source.ps1

# 将 CLI 工作目录放在仓库内，兼容受限开发环境。
$env:DOTNET_CLI_HOME = Join-Path $PWD '.build\dotnet'
dotnet build .\winuitest\winuitest.csproj -p:Platform=x64 -p:MSBuildEnableWorkloadResolver=false

& .\winuitest\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\LumaMusic.exe
```

也可以用 Visual Studio 打开 `winuitest.slnx`，选择 x64 与 **Luma Music (Unpackaged)** 启动。项目默认按非打包原生桌面应用构建，并将 Windows App SDK 随应用复制。

准备脚本生成的 `MusicSource/node_modules/`、`MusicSource/runtime/` 和 `.build/` 不进入版本控制。`package-lock.json` 锁定音源依赖。脚本优先复制架构匹配的本机 Node.js，否则从官方站点下载 Node.js 24.18.0 并校验 SHA-256。构建会把音源脚本、依赖和运行时复制到输出目录，因此修改这些文件后应重新构建。

## 发布

### MSI 安装包（推荐，无需导入证书）

```powershell
pwsh -File .\scripts\package-msi.ps1
```

脚本通过项目工具清单恢复固定版本的官方 WiX 4.0.6，复用非打包、自包含的 WinUI 发布载荷，生成 `artifacts/msi/LumaMusic_1.0.2_x64.msi`。已经发布和准备工具时可添加 `-SkipPublish -SkipToolRestore` 仅重新打包。

双击 MSI 即可安装到当前用户的 `%LOCALAPPDATA%\Programs\Luma Music`，不要求导入签名证书。安装向导支持选择目录；开始菜单提供启动与卸载入口，也可从 Windows“设置 → 应用”卸载。安装包包含 .NET、Windows App SDK、Node.js 与音源依赖；卸载保留独立存放的收藏、历史和设置。

当前 MSI 未使用商业代码签名，Windows 或 SmartScreen 仍可能显示“未知发布者”等安全提示。`artifacts/msi/` 同时提供 `INSTALL.txt`、`SHA256SUMS.txt` 和包信息；安装或卸载过程不会导入任何证书。

### 文件夹版本

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Architecture x64
```

产物位于 `artifacts/win-x64/publish/`。请分发整个文件夹，运行 `LumaMusic.exe`。它包含 .NET、Windows App SDK 和 Node.js 运行时，不依赖 Electron 或 WebView。ARM64 使用 `-Architecture arm64`，准备脚本会选择匹配架构的 Node.js 运行时。

### MSIX 安装包（可选，需要信任签名证书）

使用 PowerShell 7.2+，在解决方案目录执行：

```powershell
pwsh -File .\scripts\package-msix.ps1 -Architecture x64
```

脚本以 `WindowsPackageType=MSIX` 构建同一 WinUI 项目的独立载荷，保留正确的包资源索引；随后使用 Windows SDK MakeAppx 的默认语义校验打包，并通过 SignTool 签名。包内自包含 .NET、Windows App SDK、Node.js、音源依赖与许可证，无需另外安装这些运行时。

产物位于 `artifacts/msix/`：

- `LumaMusic_1.0.0.0_x64.msix`：签名的 x64 安装包。
- `LumaMusic-Signing.cer`：公开签名证书。
- `INSTALL.txt`、`SHA256SUMS.txt`、`verification.json`：安装说明、校验值与验证结果。

包身份为 `XiaozheQAQ.LumaMusic`，发布者为 `CN=XiaozheQAQ`。默认创建专用自签开发证书；私钥和受当前用户 DPAPI 保护的密码仅留在被忽略的 `.build/signing/`，不提交、不分发。后续开发更新应保留同一签名私钥。

首次安装自签版本时，先核对来源和 `INSTALL.txt` 中的证书指纹，再手动将公开 CER 安装到“本地计算机 → 受信任的人”，然后打开 MSIX 安装。打包脚本不会导入证书、修改系统信任或安装应用。公开分发可通过 `-CertificatePath` 和 `-CertificatePassword` 使用与发布者匹配的正式代码签名 PFX，或使用 Microsoft Store 签名。

可独立复核签名和包内全部文件，不修改系统信任：

```powershell
pwsh -File .\scripts\verify-msix.ps1 `
  -PackagePath .\artifacts\msix\LumaMusic_1.0.0.0_x64.msix `
  -CertificatePath .\artifacts\msix\LumaMusic-Signing.cer -AsJson
```

验证涵盖 CMS 签名、签名绑定的区块映射、全部载荷区块、发布者和签名证书；离线完整性通过与 Windows 已信任证书是不同结果。开发签名不请求时间戳服务器，有效期受证书有效期限制。已经准备依赖时可使用 `-SkipPrepare`，离线构建可同时添加 `-NoRestore`。

## 验证

以下命令从解决方案目录执行。C# 检查项目直接链接产品代码，不依赖 WinUI 窗口或额外测试框架；默认使用本地 Node.js 假音源进程，不访问网络。检查覆盖并发协议、取消、崩溃重启、进程清理、收藏与偏好持久化、音频输出选择的保存与默认值恢复、账号协议与重启恢复，以及隔离临时目录中 DPAPI 凭据加密、损坏恢复和退出清理。Node.js 测试覆盖扫码状态、会话隔离、取消验证竞态、权限音质回退及歌词翻译对齐。

```powershell
# 离线 C# 进程集成与本地曲库检查（需 .NET 8 SDK/运行时和 Node.js 22+）
dotnet run --project .\tests\LumaMusic.Checks -p:MSBuildEnableWorkloadResolver=false

# 可选：增加 C# API 客户端真实搜索与逐字歌词检查，先完成 prepare-source.ps1
dotnet run --project .\tests\LumaMusic.Checks -p:MSBuildEnableWorkloadResolver=false -- --live

# Node.js 数据映射、歌词与错误提示测试，不访问网络
npm --prefix .\winuitest\MusicSource test

# 实际连接网易云：发现、搜索、歌单、歌词、播放地址与音频响应
npm --prefix .\winuitest\MusicSource run smoke
```

原生托盘检查使用单独编译的检查入口、独立资料库和本机回环静音 WAV，验证设备选择保持进度、播放前后曲、页面跳转、WinUI 菜单创建和托盘清理；检查入口不包含在正常发布版中。需要可用音频输出设备。运行中的 1.0.2 或更新版会使脚本停止，避免替换其托盘图标。

```powershell
pwsh -File .\scripts\test-native-tray.ps1 -Theme Dark
pwsh -File .\scripts\test-native-tray.ps1 -Theme Light
```

运行应用后可检查：发现页加载 → 搜索歌曲 → 双击 / 点击播放 → 拖动进度 → 下一首 → 收藏 → 重启后查看收藏。云端接口的可用性及曲目权限会影响实际返回内容。

逐字歌词检查：选择提供 YRC 的歌曲 → 打开沉浸式歌词并进入全屏 → 暂停 / 继续，确认高亮随播放停走 → 拖动进度或点击歌词，确认高亮按目标时间重新定位 → 切换下一首，确认歌词与字词状态更新。试听曲目应计入试听起点偏移；无 YRC 的歌曲应回退到 LRC 逐行高亮。

账号检查需使用自己的网易云账号在手机上确认：扫码登录 → 同步歌单 → 播放有权限的曲目 → 更改音质 → 关闭并重启，确认恢复账号及暂停进度 → 退出登录。游客接口与协议测试不能证明某个真实会员账号的完整曲库和音质权限。

## 代码位置

- `winuitest/MainWindow.xaml`、`MainWindow.xaml.cs`、`MainWindow.Account.cs`：原生界面、页面交互和账号流程。
- `winuitest/Models/`：跨界面和音源的简单数据模型。
- `winuitest/Services/`：音源进程、API 客户端、原生播放与本机曲库。
- `winuitest/MusicSource/`：最小 JavaScript 音源适配层与锁定的 npm 依赖。
- `scripts/prepare-source.ps1`、`scripts/publish.ps1`：依赖准备与分发构建。

音源项目采用 MIT 许可证；分发时保留 npm 包内的许可证及 Node.js 运行时附带的许可证。网络内容与播放权限由网易云音乐服务决定。

填充图标字体来自 [Microsoft Fluent System Icons](https://github.com/microsoft/fluentui-system-icons)，采用 MIT 许可证，按提交 `9eb184e96268d3293462f9d9d19225b4c7b07774` 固定。字体在应用内随包加载，不要求安装系统字体；原始许可证、字体族名和 SHA-256 见 `winuitest/Assets/Fonts/`。

当前锁定版本的 `npm audit` 包含 PAC 代理依赖链中 `basic-ftp` 的高危公告，上游暂未提供修复。本适配层未开放代理参数；升级音源依赖后应重新运行上述测试并复核审计结果。

沉浸歌词圆体采用 [Resource Han Rounded CN 0.990](https://github.com/CyanoHao/Resource-Han-Rounded/releases/tag/v0.990)，由 Cyano Hao 基于思源黑体制作，遵循 SIL Open Font License 1.1。应用仅打包未修改的静态 Heavy 与 Bold 两个 TTF（合计约 26.4 MiB），主歌词及逐字高亮使用真正的 Heavy 字重，翻译和音译使用 Bold。字体覆盖简繁中文、日文汉字与假名、拉丁字母；未覆盖文字由 Windows 字体回退处理。无需安装系统字体或运行时下载。原始授权、实际字体族名、字形覆盖检查及 SHA-256 见 `winuitest/Assets/Fonts/LICENSE-ResourceHanRounded.txt` 和 `PROVENANCE-ResourceHanRounded.txt`。
