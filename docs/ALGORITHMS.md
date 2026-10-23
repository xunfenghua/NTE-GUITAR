# 算法与数据流说明

本文说明源码中采用的算法与职责边界，便于定位实现。它不是对所有边界参数的逐行形式化证明；精确行为与默认值以当前 C# / JS / Python 源码为准。

## 1. MIDI 读取与统一时间轴

`Midi/MidiLoader.cs` 使用 DryWetMIDI 读取 MIDI 轨道、音符、通道、tempo map 及来源信息。MIDI 位置以 tick 表示；tempo map 把不同片段的 tick 换算成实际经过时间，转换阶段将事件规范化为微秒时间轴。源音符的起始和时值会保留，以供编辑、变速、编译、预览或写回 MIDI。轨道选择、鼓轨过滤、去重在转换入口按配置执行。播放使用单调时钟对齐绝对目标时点，不靠逐音符累计 `Thread.Sleep`。

实现：`Midi/MidiLoader.cs`, `Midi/MidiConverter.cs`, `Models/RawMidiNote.cs`, `Core/PlaybackSpeedEngine.cs`, `Input/InputScheduler.cs`。

## 2. 旋律识别与轨道选择

`Recognition/` 提供依赖音符密度、音域、连续性和 onset 邻域的轨道优先级/旋律候选策略。复调轨道先按 onset 聚类，再按最高声部、连续线条或融合候选筛出旋律。导入 UI 支持试听和切换结果，因此自动提取是建议，不会覆盖用户的轨道选择。

主要策略包括单声部化、最高音、连续旋律和主旋律融合；它们接收 `RawMidiNote` 集并返回有序候选。

实现：`Recognition/TrackPriorityMode.cs`, `Recognition/MonophonicMode.cs`, `Recognition/HighestNoteMode.cs`, `Recognition/ContinuousMelodyMode.cs`, `Recognition/AutoLeadFusionMode.cs`, `Recognition/PolyphonicMelodyExtractor.cs`。

## 3. 36 键脚本映射

目标音域是 E3–D#6（MIDI 52–87），对应从 0 起的 36 个键位索引。移调在映射前应用；越界音按选项丢弃或做八度折叠。折叠以八度变换寻找目标范围内的等名音，并对折叠后的重复音去重。曲库保存逻辑音符索引而非扫描码；`KeyPreset` 将逻辑索引映射到用户当前的物理键位，因此改键不必重新转换歌曲。同起点的音符按和弦批次保留。

实现：`Midi/MidiConverter.cs`, `Midi/PhraseOctaveFolder.cs`, `Models/GameNoteEvent.cs`, `Models/KeyPreset.cs`, `Input/PresetManager.cs`。

## 4. 游戏钢琴映射与可达性

游戏钢琴使用独立校准 profile，不复用 36 键脚本键盘。音高映射器投影到 36 键半音或 21 键七声音阶布局，并生成物理键/修饰键组合。编译前生成可达性报告；无法按配置映射的音符会被报告为不可达，不会静默映射到任意按键。钢琴会话负责倒计时、窗口复核、暂停、恢复、停止及活动音符事件。

实现：`Piano/Mapping/PianoPitchMapper.cs`, `Piano/Compiler/PianoMidiCompiler.cs`, `Piano/Models/PianoProfile.cs`, `Piano/Services/PianoSessionService.cs`。

## 5. 吉他品格音符的整体规划

吉他映射器为输入音高生成六弦/品格候选。候选代表游戏乐器可奏位置，不是把超界音夹到最近音。对连续音符，规划结合各候选成本与相邻位置切换成本，减少频繁翻页或跨度过大的局部贪心结果。超出可奏范围或无合法候选的音符会记录跳过原因。动作计划再加入页面/槽位操作、控制键时序和扫弦。

实现：`Guitar/FretConverter.cs`, `Guitar/GamepadActionPlanner.cs`, `Guitar/CircularPageNavigator.cs`, `Guitar/GuitarCompiler.cs`。

## 6. 和弦识别、伴奏和扫弦

和弦分析器从时间聚集的音高集合估计和弦候选，并用跨 onset 的证据和状态提高伴奏连贯性。转位、漏音、旋律音和节奏错位会让单个时间点判断不稳，因此算法包括证据聚合、事件过滤、状态转换和游戏和弦 ground-truth 对照。编排器把和弦区段、单音/伴奏事件转成游戏可用和弦、页面/槽位及拨弦/扫弦动作；扫弦方向和弦集合按带时间戳的成组操作规划。

实现：`Guitar/ChordAnalyzer.cs`, `Guitar/ChordAnalyzerV2.cs`, `Guitar/GameChordGroundTruth.cs`, `Guitar/StrumAnalyzer.cs`, `Guitar/ArrangedAccompanimentCompiler.cs`, `Guitar/AccompanimentEventFilter.cs`, `Guitar/AttackClusterPolicy.cs`。

## 7. 复音压缩、自动速度与混合演奏

游戏输入受并发音符、槽位和重复触发频率限制。吉他流水线先分析同 onset 复音和密度，按规则压缩无法同时实现的音符；速度优化器估计最密区域能否满足最低按压/释放间隔并给出建议速度。自动配速是编译建议，手动速度是否优先由 UI/服务显式选择。

完整混合演奏在品格和固定和弦候选之间对全曲联合决策。候选评分涵盖音高误差、旋律保留、控制动作成本、切换次数和时间可行性；跨 onset 的代价减少局部贪心决策。长音起始和高音主旋律有保护策略。固定和弦无法准确表达主旋律时回退到品格；不可奏音则跳过并记录。

实现：`Guitar/MidiPolyphonyReducer.cs`, `Guitar/MidiSpeedOptimizer.cs`, `Guitar/HybridGuitarCompiler.cs`, `Guitar/ArrangedAccompanimentCompiler.cs`, `Guitar/PlaybackStartAdvisor.cs`。

## 8. 时间调度、重复音和释放安全

编译结果是有序动作时点；调度器用单调高分辨率时钟对齐绝对 deadline，避免普通睡眠调用的累计漂移。相同时点的和弦合并为批次；同物理键快速重触发需先释放再按下，并遵守最小按压和释放间隔。翻页、选槽、扫弦可以采用独立物理时序保护，避免加速播放把控制间隔缩得过短。结束、停止、失焦、异常和资源释放均会尝试释放已持有的键/弦。

应用内部微秒时间戳不代表 Windows 或游戏本身有微秒级响应精度；设备、窗口焦点、OS 调度和目标程序的输入轮询都会影响实际结果。

实现：`Input/ActionCompiler.cs`, `Input/InputScheduler.cs`, `Input/KeyboardSender.cs`, `Input/KeyboardHoldGuard.cs`, `Guitar/GuitarScheduler.cs`, `Guitar/GuitarControlTimingGuard.cs`, `Input/VirtualGamepadController.cs`。

## 9. Guitar Pro 与谱面转换

AlphaTab 解析 Guitar Pro 文件和轨道信息；转换入口将音符统一到 MIDI/源音符模型，再调用共享的乐器编译层。用户可选择完整谱或旋律、轨道、移调和目标乐器，避免为每个输入格式复制一套游戏调度器。

实现：`Services/GtpConversionService.cs`, `Gtp/GtpModels.cs`, `NTE-GUITAR.csproj` 的 AlphaTab 依赖。

## 10. 半自动覆盖层与命中判定

谱面数据记录目标页面/槽位、音符到来时间和输入通道。练习、节奏、困难节奏、展示和 MIDI 跟谱模式各自选择是否要求玩家命中、是否自动换位以及是否发送游戏输入。节奏判定按实际按键与目标 onset 的时间差评估，并维护组合、分数和进度；练习模式还跟踪长按完成与错按反馈。

全手动只显示下一目标，半自动按位置路线发送翻页/选槽动作。模拟 MIDI 跟谱使用 MIDI 输出路径，不把“听到音符”混为游戏输入。

实现：`SemiAuto/Chart/`, `SemiAuto/Modes/`, `SemiAuto/WinForms/`, `Web/SemiAutoOverlay/`。

## 11. 编辑、曲库和写入一致性

曲库用 UUID 隔离曲目，分开存元数据、规范化曲目和可选源音符缓存；原始文件通过内容哈希缓存解析结果。源路径失效时曲库项目仍可读取。序列化服务使用安全写入和索引修复逻辑，避免半写文件破坏整库加载。卷帘编辑器将明确的音符数据交给原生写入器生成 MIDI，而不是把 DOM 当成长期存储。

实现：`Library/SongLibrary.cs`, `Library/SongSerializer.cs`, `Midi/MidiRawCache.cs`, `Midi/MidiWriter.cs`, `Services/LibraryService.cs`。

## 12. 可选音频转谱

客户端、Python worker 和模型快照相互分离。模型 downloader 读取固定 revision 清单并逐文件核验 SHA-256；下载模型和准备 runtime 都由用户明确触发。worker 使用带 request ID 的 JSON Lines 协议处理准备、转录、取消和清理。CUDA 不可用时会先要求明确 CPU 确认。音频和任务文件存于本机 AppData。

SheetSage2 和 MERT 的代码/权重许可为 CC BY-NC 4.0；商业使用或再分发须遵守上游许可。GitHub Release 不含模型文件。

实现：`AudioTranscription/README.md`, `AudioTranscription/bootstrap.py`, `AudioTranscription/worker.py`, `AudioTranscription/downloads.json`, `Services/AudioTranscriptionService.cs`。

## 运行验收

```powershell
dotnet run --project .\NTE-GUITAR.csproj -c Release -- --test
```

测试入口 `Testing/TestRunner.cs` 根据当前构建组合转换、钢琴/吉他编译、时序、序列化和推荐谱用例。runner 的测试集随源码变化；旧版 README 中的测试总数不是固定接口。
