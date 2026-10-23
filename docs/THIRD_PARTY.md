# 第三方组件、模型和素材声明

本文根据当前项目文件和对应上游页面整理，不替代每个包、资产或模型附带的许可证。NuGet 恢复得到的传递依赖也可能有自己的条款；发布或修改二进制分发前，请检查 NuGet package license/notice 内容，并按各自条款附带版权声明。

## 直接 .NET 依赖

| 组件 | 固定版本 | 用途 | 来源 / 许可证 |
|---|---:|---|---|
| AlphaTab | 1.8.4 | Guitar Pro 文件/乐谱信息解析支持 | [NuGet](https://www.nuget.org/packages/AlphaTab/1.8.4) · [上游许可证 MPL-2.0](https://github.com/CoderLine/alphaTab/blob/develop/LICENSE) |
| Melanchall.DryWetMidi | 8.0.3 | Standard MIDI 文件和音符/tempo 数据 | [NuGet](https://www.nuget.org/packages/Melanchall.DryWetMidi/8.0.3) · MIT |
| Microsoft.Web.WebView2 | 1.0.4258.31 | WinForms 中的 Chromium WebView2 控件 | [NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4258.31) · Microsoft package terms；另需 Windows WebView2 Runtime |
| Nefarius.ViGEm.Client | 1.21.256 | 可选虚拟手柄驱动的 managed client | [NuGet](https://www.nuget.org/packages/Nefarius.ViGEm.Client/1.21.256) · MIT；需要单独安装 ViGEmBus driver |
| .NET 8 / Windows Forms | 8.x | 运行时、WinForms、系统 API | [Microsoft .NET 下载与许可信息](https://dotnet.microsoft.com/download/dotnet/8.0) |

上表是直接引用。发布 ZIP 还可能包含 Microsoft .NET runtime、WebView2 loader/native files 和 NuGet 传递依赖。发布构建的完整依赖清单可通过对应 `*.deps.json` 和 NuGet package metadata 审核。对任何第三方源码文件的修改都应遵循其原有许可证；alphaTab 使用 MPL-2.0，请特别遵守 MPL 对被修改文件的要求。

## 外部运行时和驱动

| 组件 | 何时需要 | 获取方式 | 项目如何处理 |
|---|---|---|---|
| Microsoft Edge WebView2 Evergreen Runtime | 运行桌面 UI | [Microsoft 官方下载](https://developer.microsoft.com/microsoft-edge/webview2/) | 单独安装；Lite 包不内嵌运行时安装器。 |
| ViGEmBus driver 1.22.0 | 选择虚拟手柄输入 | [上游 GitHub Release](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0) | 不进入 Git/Release ZIP；`scripts/Download-ViGEmBus.ps1` 下载并校验，停止在磁盘文件，不自动安装。上游 repository 已归档。 |
| Python 3.12 x64 | 用户启用音频转谱 | [Python 官方下载](https://www.python.org/downloads/release/python-3120/) | 用户自行安装；worker 在 AppData 中准备专属环境。 |
| FFmpeg | 用户启用音频转谱 | [FFmpeg 官方下载指引](https://ffmpeg.org/download.html) | 用户自行选取并安装可信构建；不随 Lite 发布。不同下载站构建可能有独立许可证。 |

## 可选 AI 模型与 Python 包

| 项目 | 来源 / 固定 revision | 用途 / 许可 |
|---|---|---|
| SheetSage2 | [模型卡](https://huggingface.co/m-a-p/SheetSage2)，revision `398b22834dac7dd05e09b9c4e40a39fc479ec502` | 音频到乐谱推理。模型权重和定制代码标记 **CC BY-NC 4.0**。 |
| MERT-v2-FullSong | [模型卡](https://huggingface.co/m-a-p/MERT-v2-FullSong)，revision `d8ba1c745e733b3908ce6ad16ebeb17ac7600a42` | SheetSage2 使用的音乐音频编码器。模型和代码标记 **CC BY-NC 4.0**。 |
| PyTorch / torchaudio | [PyTorch 安装指引](https://pytorch.org/get-started/locally/)；worker pins 2.8.0 | 本地推理 runtime。使用者按安装来源的许可和依赖声明使用。 |
| Transformers 及 `AudioTranscription/requirements.txt` 中的包 | [PyPI](https://pypi.org/) / 各项目主页 | worker 的 Python 推理依赖；每个依赖保留各自许可证。 |

模型下载清单 `AudioTranscription/downloads.json` pin 住 revision 和若干代码/权重 SHA-256，下载器不把模型写进 GitHub 发布资产。CC BY-NC 不允许未获额外授权的商业使用；如要商用或重新分发，直接向模型权利人确认许可。当前 worker 通过 Transformers 的 `trust_remote_code=True` 加载上游模型代码，因此下载器 pin revision/hash 并以离线本地快照加载；使用者仍应审查源码并接受本地执行模型代码的安全边界。

## 排除的第三方内容

Lite 仓库和二进制不包含随原始工作区附带的热门歌曲 MIDI、用户打包的预设/目录数据、ViGEmBus setup EXE、FFmpeg、Python venv、约 2.8 GB AI 模型权重或大型隐藏视频。它们不是该项目源码许可可涵盖的内容。用户导入的歌曲/录音仍归各自权利人所有。

## 本项目许可证

桌面端和 companion 服务自有源代码按仓库根目录 [`LICENSE`](../LICENSE) 中的 Apache License 2.0 授权。Apache-2.0 不覆盖上表第三方依赖、模型、驱动、运行时、字体/媒体或用户导入的作品。
