# NTE-GUITAR Lite

**Windows 桌面音乐编排与游戏乐器演奏工具** · C# / .NET 8 / WinForms / WebView2

[English](README.en.md) · [算法与数据流](docs/ALGORITHMS.md) · [构建、运行与模型](docs/BUILD_AND_MODELS.md) · [第三方组件与许可证](docs/THIRD_PARTY.md) · [架构地图](docs/ARCHITECTURE.md)

> Lite 是本项目可公开的源码发行版，提供应用程序源码、必要界面资源、算法实现和可复现的 Windows x64 发布脚本。它不会捆绑模型权重、Python 环境、驱动安装程序、歌曲 MIDI、工作区构建缓存或大型媒体文件。版本以 [GitHub Releases](https://github.com/xunfenghua/-/releases) 页面为准。

## 项目简介

NTE-GUITAR 是面向 Windows 的桌面音乐工具：导入 MIDI 或 Guitar Pro 乐谱，选择曲目并转换到 36 键脚本、游戏钢琴或游戏吉他；在本地曲库管理可重复演奏的作品；预览 MIDI 时间线；在绑定的游戏窗口中，以键盘、鼠标或可选虚拟手柄发送带时间戳的输入。独立的半自动吉他覆盖层支持跟谱、练习和节奏判定。可选音频转谱流程通过本地 SheetSage2 模型生成 MIDI，不会把音频上传到应用服务器。

这是一个客户端项目，不附带在线服务实例、游戏内容或歌曲素材。公告及推荐谱服务器是可单独构建的 companion project。源码中定义的代码许可证为 Apache-2.0；独立模型、驱动、NuGet 包和媒体的许可证各自适用，详见[第三方声明](docs/THIRD_PARTY.md)。

## 功能一览

### 导入、编排与乐谱编辑

- 导入标准 MIDI（`.mid` / `.midi`）；浏览轨道、音符数和音域，预听轨道，并选择全部、指定轨道或智能主旋律。
- 导入 Guitar Pro 3–7 文件（`.gp`, `.gp3`, `.gp4`, `.gp5`, `.gpx`），选择完整谱或旋律、轨道、目标乐器和移调。
- MIDI 钢琴卷帘编辑器：编辑音符位置、音高和时值，移动、拉伸、量化、试听、缩放和全屏查看；保存回本地曲库。
- 支持独立的本地音频转谱入口；推理在本机 Python 环境和本地模型上运行，输出可检查和编辑的 MIDI。
- 曲目可预览、试听、改名、删除、重转目标乐器或生成可复用的吉他谱面。

### 目标乐器和演奏模式

| 目标 | 实现内容 |
|---|---|
| 36 键脚本键盘 | MIDI 音高映射为 E3–D#6（52–87）内的 36 个键位索引；移调、八度折叠或丢弃越界音、轨道去重、可编辑键位预设。 |
| 游戏钢琴 | 独立钢琴映射配置；36 键半音布局或 21 键七声音阶布局；校准配置、可演奏性检查、编译预览和会话控制。 |
| 游戏吉他 | 单音品格模式、24 和弦伴奏模式、完整混合模式。混合编译器对整曲联合选择品格/和弦动作，并编排翻页、选槽和扫弦。 |
| 吉他模拟器 | 交互展示页面、弦、品格、槽位和扫弦，供熟悉映射与校准使用。 |
| 半自动覆盖层 | 练习、音游、困难节奏、锁屏展示和 MIDI 跟谱等会话模式；支持手动或半自动位置切换、节奏判定和谱面指引。 |

### 播放、校准和辅助功能

- MIDI Tempo Map 按绝对微秒时间线播放，包含曲目自动配速与播放速度设置。
- 键盘按键、鼠标拨弦和可选 ViGEmBus 虚拟手柄输出；校准游戏窗口、吉他轮盘和按键映射。
- 默认 F8 全局播放/停止热键；绑定窗口检查和失焦停止保护；结束、异常和退出路径释放已按下的输入。
- 曲库、预设、校准和设置写入本机用户数据目录，与原始 MIDI 文件路径独立。
- 可浏览远程推荐谱目录、下载 MIDI、提交待审核谱面；这些网络功能依赖部署中的公告/推荐谱服务。
- 启动公告与客户端版本在线心跳为可选联网功能；本地导入、编曲、曲库和播放不要求服务器在线。

完整模块和算法说明见[架构地图](docs/ARCHITECTURE.md)及[算法实现](docs/ALGORITHMS.md)。

## 获取程序

从 [Releases](https://github.com/xunfenghua/-/releases) 下载带 `Lite` 标记的 Windows x64 ZIP，解压到有写入权限的目录后运行 `NTE-GUITAR.exe`。首次启动需要 Windows 10/11 x64 和 Microsoft Edge WebView2 Runtime；多数现代 Windows 已经安装。若缺失，请通过[微软 WebView2 官方页面](https://developer.microsoft.com/microsoft-edge/webview2/)安装 Evergreen Runtime。

Release 的 ZIP 附带 `SHA256SUMS.txt`。下载后可在 PowerShell 中核验：

```powershell
Get-FileHash .\NTE-GUITAR-Lite-3.4.0-win-x64.zip -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

这是便携式桌面程序，不含安装器、模型权重或虚拟手柄驱动。不要对不需要的驱动授予安装权限。

## 从源码构建

### 环境要求

- Windows 10/11 x64；
- .NET 8 SDK（Windows x64），从 [.NET 8 下载页](https://dotnet.microsoft.com/download/dotnet/8.0)安装；
- Git 和可访问 NuGet 的网络连接；
- （可选）Visual Studio 2022 的“.NET 桌面开发”工作负载；
- （可选）系统 WebView2 Runtime，运行程序时需要；
- （可选）ViGEmBus 驱动，仅使用虚拟手柄输出时需要；
- （可选）音频转谱所需的 64 位 Python 3.12、FFmpeg 和磁盘空间。模型本身约 2.8 GB，运行环境和临时下载还需额外空间。

```powershell
git clone https://github.com/xunfenghua/-.git
Set-Location -LiteralPath .\-
dotnet restore .\NTE-GUITAR.csproj
dotnet build .\NTE-GUITAR.csproj -c Release
```

如果从本 README 的克隆链接目录进入失败，请将上面 `Set-Location` 中的目录名替换为你自己的克隆目录。构建不需要下载音频模型，也不会安装系统驱动。首次 `dotnet restore` 会按 `.csproj` 中固定版本从 NuGet 获取依赖。

运行源码版：

```powershell
dotnet run --project .\NTE-GUITAR.csproj -c Release
```

运行内置验收测试：

```powershell
dotnet run --project .\NTE-GUITAR.csproj -c Release -- --test
```

### 生成 Lite ZIP 和校验和

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Publish-Lite.ps1
```

脚本针对 `win-x64` 发布自包含应用，归档到 `dist/release/`，并生成 SHA-256 清单。自包含包内含 .NET 运行时；Windows WebView2 Runtime 仍由系统提供。发布脚本不会嵌入 Python 环境、FFmpeg 或模型权重。

### 可选虚拟手柄驱动

虚拟手柄需要 ViGEmBus 内核驱动。Lite 不携带驱动安装程序。若确实要用此输入方式，可先阅读上游[停更说明和发行页面](https://github.com/nefarius/ViGEmBus/releases)，然后运行：

```powershell
.\scripts\Download-ViGEmBus.ps1
```

脚本从上游固定的 `v1.22.0` Release 下载文件，校验 SHA-256 后只把安装器保存到 `dist/dependencies/`，**不会自动执行安装**。ViGEmBus 上游仓库已归档；驱动是否兼容由设备、系统和游戏决定。键盘/鼠标模式不要求安装该驱动。

## 音频转谱模型与下载

普通 MIDI / Guitar Pro 导入、编辑、转换和演奏不使用机器学习模型。音频转谱是可选功能，使用 SheetSage2 和 MERT-v2-FullSong；发布包不包含模型参数，也不会在未明确触发准备流程时下载它们。

完整步骤、固定模型 revision、依赖安装命令、GPU/CPU 行为、哈希校验和模型许可限制见[构建、运行与模型指南](docs/BUILD_AND_MODELS.md)及[音频模块说明](AudioTranscription/README.md)。权重来自 [SheetSage2 模型卡](https://huggingface.co/m-a-p/SheetSage2) 和 [MERT-v2-FullSong 模型卡](https://huggingface.co/m-a-p/MERT-v2-FullSong)，采用 CC BY-NC 4.0；这两项许可限制商业用途。下载前请阅读模型卡和许可证。程序的下载器只读取 `AudioTranscription/downloads.json` 中固定 revision 的文件，并核验预先固定的 SHA-256；源码本身不等同于权重再分发许可。

## 数据、网络和隐私

- 本地设置、键位预设、缓存和曲库通常保存在 `%LocalAppData%\NTE-GUITAR\`。卸载或删除源码不会自动清除该目录；用户数据备份可自行处理。
- 音频转谱输入在本机处理，临时任务文件保存于用户数据目录。不要把私有音频或个人日志提交到公开 Issue。
- 在线公告、推荐谱和版本心跳会发出 HTTP(S) 请求；心跳报告应用版本和随机进程会话 ID，不保存持久设备标识。完整行为可在 `Services/AnnouncementCheckService.cs`、`Services/RecommendedScoreService.cs` 与 `Services/ClientPresenceService.cs` 查看。
- 播放功能会向用户选定的前台游戏窗口发送输入。请先校准和测试；游戏规则、反作弊机制及第三方服务条款由其各自权利人决定。
- Lite 不包含个人日志、崩溃转储、打包的歌曲 MIDI、公开推荐目录数据或机器学习权重。

## 代码地图

| 路径 | 内容 |
|---|---|
| `Program.cs`, `UI/` | WinForms 窗口、启动、设置、校准和对话框。 |
| `wwwroot/`, `Web/SemiAutoOverlay/` | WebView2 前端、编辑器、播放器和覆盖层界面。 |
| `Bridge/` | WebView 前后端 JSON 消息协议和事件发布。 |
| `Midi/`, `Recognition/`, `Models/` | MIDI 解析/转换、旋律提取和共享数据模型。 |
| `Guitar/` | 吉他品格、和弦、混合编译、行动计划和调度器。 |
| `Piano/` | 游戏钢琴音高映射、乐谱编译和会话。 |
| `SemiAuto/` | 吉他谱面、位置规划、练习/音游判定和覆盖层控制器。 |
| `Gtp/`, `Services/`, `Library/` | Guitar Pro 转换、设置与校准服务、本地/推荐曲库。 |
| `Input/`, `Core/` | Win32/虚拟手柄输入、安全释放、调度和速度变换。 |
| `AudioTranscription/` | 本地 Python worker、环境准备和固定模型下载清单。 |
| `AnnouncementServer/` | 可独立构建的公告及推荐谱 API 服务。 |
| `Testing/` | 应用内算法和集成验收测试。 |

## 版本、限制与贡献

- 项目版本来自 `NTE-GUITAR.csproj` 的 `<Version>`；发布 tag 和资产文件名应与之匹配。
- 当前仓库是 Lite 发行版。大型运行缓存、模型权重、第三方驱动安装程序、示例歌曲、媒体视频和内部计划/草稿不纳入 Git 历史。
- 不要把含个人数据、未经授权的乐谱/录音或模型快照提交到仓库。贡献前请阅读[贡献指南](CONTRIBUTING.md)和[第三方声明](docs/THIRD_PARTY.md)。
- 代码按 Apache-2.0 授权；第三方依赖和可下载模型各自受其许可证约束。除非你持有权利或许可，不要把本项目用于违反游戏规则或侵害他人权利的用途。

## 鸣谢

感谢 .NET、WebView2、AlphaTab、DryWetMIDI、ViGEm 和 SheetSage2/MERT 上游维护者。逐项版本与来源列于[第三方组件声明](docs/THIRD_PARTY.md)。
