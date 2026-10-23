# 构建、运行和模型准备

## Windows 桌面应用

### 必备软件

1. Windows 10/11 x64。
2. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)：构建目标为 `net8.0-windows`。SDK 含编译器和 Windows Desktop framework targeting pack。
3. Git 与可访问 `api.nuget.org` 的网络；NuGet 根据项目文件恢复包。
4. 运行时所需 Microsoft Edge WebView2 Runtime：[官方 WebView2 下载页](https://developer.microsoft.com/microsoft-edge/webview2/)。SDK 本身通过 NuGet 引入；Windows 上的 Edge WebView2 Runtime 是独立安装组件。

Visual Studio 不是必需项。若使用 Visual Studio 2022，选择“.NET 桌面开发”工作负载。首次 clone 后：

```powershell
dotnet --info
dotnet restore .\NTE-GUITAR.csproj
dotnet build .\NTE-GUITAR.csproj -c Release
```

源码版运行：`dotnet run --project .\NTE-GUITAR.csproj -c Release`。命令行验收：`dotnet run --project .\NTE-GUITAR.csproj -c Release -- --test`。

### 生成 Release 包

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Publish-Lite.ps1
```

输出写入 `dist/release/`。脚本使用 self-contained `win-x64` 单文件发布选项和压缩 ZIP；.NET runtime 随程序打包，WebView2 Evergreen Runtime 仍由 Windows 提供。发布包 SHA-256 输出在同目录的 `SHA256SUMS.txt`。发布后建议在干净 Windows 账户/虚拟机中解压启动，并实际验证至少一种输入方式，再创建公开 Release。

如需命令行手工发布，可参考：

```powershell
dotnet publish .\NTE-GUITAR.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EmbedAudioRuntime=false `
  -o .\dist\publish-win-x64
```

网页、配置与原生内容资源仍可能作为单独文件输出；打包时请保留整个 publish 目录。Lite 的打包步骤排除媒体视频、歌曲示例、模型、Python 运行时、FFmpeg 和系统驱动。

## 可选 ViGEmBus 虚拟手柄

只在启用虚拟手柄输入时需要内核驱动；键盘/鼠标模式不需要。项目不在源码库和 Release 包中重新分发该驱动。

```powershell
.\scripts\Download-ViGEmBus.ps1
```

脚本向 `dist/dependencies/` 下载上游 1.22.0 setup，核对固定 SHA-256 后停止；安装需要用户自行阅读上游说明后手动执行。ViGEmBus 的上游仓库已归档，详见[上游发行说明](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0)。安装驱动可能需要管理员权限和系统重启。

## 音频转谱：Python 与 FFmpeg

音频转谱是可选功能，编辑/播放已有 MIDI 或 Guitar Pro 文件时不需要 Python、FFmpeg 或任何模型。

1. 安装 64 位 Python 3.12：从 [python.org Windows 下载页](https://www.python.org/downloads/windows/)选择 3.12 版本，或按组织的软件管理政策部署。准备程序寻找 Python Launcher (`py -3.12`) 或 `PATH` 上的 `python.exe`。
2. 安装 FFmpeg，并确保 `ffmpeg.exe` 位于 `PATH`；项目不会自动安装 FFmpeg。可从 [FFmpeg 官方下载指引](https://ffmpeg.org/download.html)选择合适构建，再按构建提供者条款安装。
3. 至少预留约 6 GiB 空间用于首次 runtime 准备；两项模型文件合计约 2.8 GB，另需临时下载空间。
4. 先检查并阅读 `AudioTranscription/README.md` 里的模型卡、固定 revision、许可证和运行说明，再在源码根目录执行：

```powershell
$data = Join-Path $env:LOCALAPPDATA 'NTE-GUITAR'
py -3.12 .\AudioTranscription\bootstrap.py --data-root $data --install-runtime
py -3.12 .\AudioTranscription\bootstrap.py --data-root $data --prepare-models
```

首次准备将 PyTorch 2.8.0 与 torchaudio 2.8.0 安装至 `%LocalAppData%\NTE-GUITAR\audio-transcription\runtime\python312`，其他 Python 固定依赖见 `AudioTranscription/requirements.txt`。模型快照按 `AudioTranscription/downloads.json` 中的 immutable revision 和文件哈希下载，优先 ModelScope，按清单文档规则回退 Hugging Face Hub；只下载列出的 SheetSage2 与 MERT 文件，不下载 Qwen 或 ASR/对齐模型。

模型权重与自定义代码受 **CC BY-NC 4.0** 限制。此许可证包含非商业限制。不得将模型权重打进本项目 Release；商用前应直接向权利人确认许可。`trust_remote_code` 使 pinned Python 模型代码在本机进程执行。使用者应阅读固定源文件并自行决定是否信任。

如果 CUDA 不可用，worker 会请求 CPU 推理确认，用户确认后才启动；CPU 推理速度和资源消耗取决于硬件。CUDA 版本的 PyTorch 请从 [PyTorch 官方安装选择器](https://pytorch.org/get-started/locally/)为实际驱动选择适配的命令；代码注释中的 CUDA 12.8 示例不是所有机器的通用值。

## FFmpeg 资源嵌入（维护者可选）

Lite 默认不嵌入 FFmpeg。需要制作包含 FFmpeg 的本地私有包时，可将合法取得的二进制传给发布属性，例如 `-p:EmbeddedFfmpegPath=C:\tools\ffmpeg\bin\ffmpeg.exe`。先确认该具体构建允许再分发，并将对应许可证和来源随包提供。不得把用户下载的任意二进制未经审查重新发布。

## 路径和数据保留

本地用户数据通常位于 `%LocalAppData%\NTE-GUITAR\`，包含 config、键位预设、解析缓存、曲库、音频任务和下载的模型。安装或升级程序不应覆盖/清除此用户目录；迁移设备前由用户主动备份。
