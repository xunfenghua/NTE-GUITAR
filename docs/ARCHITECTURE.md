# 架构与功能地图

本文面向读者和贡献者，说明 Lite 源码中各层职责。当前可运行代码是行为事实来源；旧版设计方案或快照文档只作背景材料。

## 运行时结构

```text
WinForms 主窗口 (Program.cs, UI/MainForm*)
 ├─ WebView2 工作台: wwwroot/ HTML + CSS + ES modules
 │    └─ JSON 命令/事件桥: Bridge/WebMessageHandler, WebEventPublisher
 ├─ 吉他半自动覆盖层: SemiAuto/WinForms + Web/SemiAutoOverlay
 └─ 原生服务和算法
      ├─ MIDI/Guitar Pro 导入与转换: Midi/, Recognition/, Gtp/, Piano/, Guitar/
      ├─ 持久化和设置: Library/, Services/, Models/
      ├─ 输出和定时: Input/, Core/, Guitar/GuitarScheduler
      └─ 可选本地音频推理: AudioTranscription/ (Python worker + user-downloaded models)

可选 companion 服务: AnnouncementServer/ (ASP.NET Core; 与桌面端独立部署)
```

主应用目标为 `net8.0-windows`，WinForms 拥有窗口、系统输入与进程生命周期；WebView2 前端负责工作台呈现。原生服务拥有文件、MIDI 解析、转换、定时器和输入设备。桥接层以命令/事件 JSON 交互，不将业务状态存进浏览器本地存储作为权威数据。

## 用户流程和组件

### 曲库与导入

`wwwroot/js/library.js` 和 `Services/LibraryService.cs` 负责曲库交互。导入弹窗从 MIDI 服务取得轨道概要与音符信息，可以试听、选择轨道、做旋律抽取、移调及目标乐器映射；Guitar Pro 由 `Services/GtpConversionService.cs` 与 `Gtp/` 处理。转换完成的曲目按 UUID 独立存放，元数据和规范化谱面一起保存，因此源文件重命名或删除后，已转换曲目仍可读取。

### MIDI 编辑器

`wwwroot/js/midi-editor.js` 是卷帘编辑交互，`Midi/MidiWriter.cs` 和桥接服务负责转换回 MIDI/保存。浏览器侧维护正在编辑的音符视图；保存时把明确的谱面数据交回原生端进行写入。编辑区域包含音高、位置、时值、量化、试听、缩放和全屏操作。数据持久化仍归本地曲库服务。

### 乐器编译

- **36 键**：`Midi/MidiConverter.cs` 将原始音符映射到 36 键索引，`Input/PresetManager.cs` 将索引与用户自定义扫描码解耦。
- **游戏钢琴**：`Piano/Mapping/PianoPitchMapper.cs` 使用钢琴 profile 映射音高；`Piano/Compiler/PianoMidiCompiler.cs` 校验可演奏范围并生成有序动作，`Piano/Services/PianoSessionService.cs` 管理倒计时、暂停、恢复和停止。
- **游戏吉他**：`Guitar/FretConverter.cs`、`ChordAnalyzer*`、`MidiPolyphonyReducer.cs`、`MidiSpeedOptimizer.cs` 和 `HybridGuitarCompiler.cs` 逐层处理品格可行性、和弦、复音、速度建议及混合规划。
- **半自动模式**：`SemiAuto/Chart/` 定义谱面与位置数据，`SemiAuto/Modes/` 实现练习/判定/锁屏等状态逻辑，`SemiAuto/WinForms/` 管理会话和 UI 生命周期。

### 输入、安全与播放控制

`Input/KeyboardSender.cs`、`MouseSender.cs` 和 `VirtualGamepadController.cs` 封装平台输入；`Input/InputScheduler.cs`、`Guitar/GuitarScheduler.cs` 依据编译后的时间戳调度动作；`Core/PlaybackSpeedEngine.cs` 在播放层处理节奏变换。播放会绑定游戏窗口，并在开始前或调度期间检查目标窗口状态。结束、异常或取消时走集中释放路径，避免按键/手柄状态残留。F8 是默认全局热键；失焦停止由设置控制。

### 在线 companion 服务

`AnnouncementServer/` 可独立发布，负责公告、推荐 MIDI 目录、投稿审核和在线客户端统计端点。桌面端无需此服务也能使用本地功能。服务的管理员令牌、持久数据目录、速率限制和可信代理配置见 `AnnouncementServer/README.md`；部署者必须自行保护生产令牌并配置 HTTPS。

## 数据边界

- 用户配置、预设、原始 MIDI 缓存和本地曲库通常位于 `%LocalAppData%\NTE-GUITAR\`。
- 曲库中的规范化曲目与源文件路径分离；播放所需的音符数据从曲库读取，不依赖原始 MIDI 文件仍存在。
- 音频推理运行在独立 Python worker 进程；模型权重只在用户明确执行准备流程时下载到本机 AppData。模型内容由上游许可证约束。
- 推荐谱数据与在线状态位于独立 companion 服务，服务端不会成为本地曲库的唯一存储。

## 线程和消息边界

UI 命令经 WebView2 JSON bridge 转成原生服务操作；耗时的 MIDI 分析、模型准备和网络请求应由服务异步处理，不能阻塞 WinForms UI。播放控制通过可取消的会话/调度器组织，停止和关闭必须汇合到释放操作。Python 子进程使用 JSON Lines：每个请求具备递增 request ID，进度记录可穿插，终结记录是 result/error；协议细节在 `AudioTranscription/README.md`。

## 目录的主要职责

| 目录 | 职责 |
|---|---|
| `Bridge/` | 前端/native 命令与事件桥。 |
| `Core/` | 速度和跨乐器共用的时序逻辑。 |
| `Midi/`, `Recognition/` | MIDI 文件、音符、Tempo Map、旋律分析。 |
| `Gtp/` | Guitar Pro 输入数据模型。 |
| `Guitar/`, `Piano/` | 乐器规则、编译器和控制规划。 |
| `Input/` | 键鼠、全局快捷键、虚拟手柄和定时动作。 |
| `SemiAuto/` | overlay chart、模式规则、命中判定和窗口会话。 |
| `Library/`, `Services/` | 曲库/设置/下载/校准/服务抽象。 |
| `Testing/` | 应用内验收 runner 及专项测试。 |
| `AudioTranscription/` | 用户本机的 Python 推理 worker 和模型引导。 |
| `AnnouncementServer/` | 独立公告与推荐谱 API 服务。 |

算法细节见[算法说明](ALGORITHMS.md)。
