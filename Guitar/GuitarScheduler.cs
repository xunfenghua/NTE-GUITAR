using System.Diagnostics;
using System.Drawing;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

public sealed record GuitarPreparedStart(
    int Mode,
    byte Page,
    byte Slot,
    int FirstSoundIndex,
    Point InitialMousePos);

/// <summary>
/// 吉他高精度绝对时间轴调度器: 统一调度键盘琴弦与鼠标轮盘/扫弦动作
/// </summary>
public sealed class GuitarScheduler
{
    private readonly object _stateLock = new();
    private Thread? _workerThread;
    private volatile bool _stopRequested;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private readonly VirtualGamepadController _gamepad = new();
    private readonly HashSet<ushort> _skippedGamepadPairIds = [];

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public IntPtr TargetWindow { get; set; } = IntPtr.Zero;
    public bool EnforceForegroundCheck { get; set; } = true;
    public bool UseVirtualGamepad { get; set; }
    public string? GamepadError => _gamepad.LastError;
    public PlaybackDiagnosticReport? LastDiagnosticReport { get; private set; }

    public byte CurrentPage { get; private set; } = 1;
    public byte CurrentSlot { get; private set; } = 1;
    public int CurrentMode { get; private set; } = 1;

    public event Action<long, long>? OnProgressChanged;
    public event Action<PlaybackStopReason, string>? OnPlaybackStopped;
    public event Action<PlaybackDiagnosticReport>? OnPlaybackDiagnosticsCompleted;
    public event Action<Bridge.GuitarSimulationActionDto>? OnSimulationAction;

    public static string ResolveSlotLabel(int playMode, byte page, byte slot)
    {
        if (slot < 1 || slot > 6) return "-";
        if (playMode == 1) // Fret
        {
            if (page == 5) return slot == 1 ? "Zero" : "-";
            int baseFret = (page - 1) * 6;
            return $"{baseFret + slot}品";
        }
        else // Chord
        {
            return page switch
            {
                1 => (new[] { "G", "Am", "Bm", "C", "D", "Em" })[slot - 1],
                2 => (new[] { "Cmaj7", "C7", "Cm7", "G7", "C9", "Cm9" })[slot - 1],
                3 => (new[] { "C5", "D5", "G5", "A5", "Cmaj9", "Cadd9" })[slot - 1],
                4 => (new[] { "G/B", "Am7/G", "D/F#", "F/A", "A", "Dm" })[slot - 1],
                _ => $"Slot {slot}"
            };
        }
    }


    public void Start(
        IReadOnlyList<GuitarAction> actions,
        WheelController wheelController,
        long totalDurationUs,
        long startOffsetUs = 0,
        GuitarCompilerOptions? compilerOptions = null,
        int playMode = 1,
        GuitarPreparedStart? preparedStart = null)
    {
        Stop();

        lock (_stateLock)
        {
            _stopRequested = false;
            _pauseEvent.Set();
            IsPlaying = true;
            IsPaused = false;

            var options = compilerOptions ?? new GuitarCompilerOptions();
            _workerThread = new Thread(() => PlaybackLoop(
                actions, wheelController, totalDurationUs, startOffsetUs, options, playMode, preparedStart))
            {
                IsBackground = true,
                // Keep input timing responsive without starving the UI, game, or audio threads
                // when a dense MIDI produces many closely spaced actions.
                Priority = ThreadPriority.AboveNormal,
                Name = "NTE-GUITAR_GuitarScheduler"
            };
            _workerThread.Start();
        }
    }

    public GuitarPreparedStart? PrepareInitialState(
        IReadOnlyList<GuitarAction> actions,
        WheelController wheel,
        int playMode,
        GuitarCompilerOptions options,
        long startOffsetUs = 0)
    {
        if (actions.Count == 0 || TargetWindow == IntPtr.Zero ||
            (EnforceForegroundCheck && KeyboardSender.GetForegroundWindow() != TargetWindow))
            return null;
        // Do not prime the gamepad by tapping LB/RB here. Those are real page
        // navigation inputs, and if the game misses either short edge its page
        // silently diverges from the scheduler's assumed P1. The first actual
        // calibration action (RT for a mode change or LS for slot selection)
        // is enough to make the game recognize the virtual controller.
        if (UseVirtualGamepad && !_gamepad.EnsureConnected())
            return null;

        int firstSoundIndex = -1;
        for (int i = 0; i < actions.Count; i++)
        {
            if (actions[i].TimeUs < startOffsetUs) continue;
            if (actions[i].Type is GuitarActionType.StringDown or GuitarActionType.StrumClick)
            {
                firstSoundIndex = i;
                break;
            }
        }
        if (firstSoundIndex < 0) return null;

        int targetMode = playMode == 2 ? 2 : 1;
        int targetPageCount = targetMode == 1 ? 5 : 4;
        byte targetPage = options.InitialPage >= 1 && options.InitialPage <= targetPageCount
            ? options.InitialPage
            : (byte)1;
        byte targetSlot = 1;
        for (int i = 0; i <= firstSoundIndex; i++)
        {
            var action = actions[i];
            if (action.Type == GuitarActionType.ModeSwitchDown)
            {
                targetMode = action.Value;
                targetPage = 1;
                targetSlot = 0;
            }
            else if (action.Type == GuitarActionType.PageNav)
            {
                targetPage = action.Value;
                targetSlot = 0;
            }
            else if (action.Type == GuitarActionType.WheelSelect)
            {
                targetSlot = action.Value;
            }
            else if (i == firstSoundIndex)
            {
                if (action.RequiredPage > 0) targetPage = action.RequiredPage;
                if (action.RequiredSlot > 0) targetSlot = action.RequiredSlot;
            }
        }

        Point initialMousePos = MouseSender.GetCurrentCursorPos();
        int holdMs = Math.Clamp((int)Math.Ceiling(options.PageKeyHoldUs / 1000.0), 25, 100);
        int gapMs = Math.Clamp((int)Math.Ceiling(options.PageInterTapGapUs / 1000.0), 15, 60);

        // A reset guitar starts in fret mode on P1. Mode 2 needs one Z tap;
        // hybrid mode starts in fret mode and follows its compiled first transition.
        if (targetMode != 1)
        {
            if (!SendModeSwitchTap(holdMs)) return null;
            CurrentMode = targetMode;
            CurrentPage = 1;
            CurrentSlot = 0;
            OnSimulationAction?.Invoke(new Bridge.GuitarSimulationActionDto(
                Mode: targetMode == 1 ? "fret" : "chord",
                CurrentPage: "P1", CurrentSlot: 1, ActionType: "mode",
                StringNo: 0, Fret: 0, ChordName: null, GameKey: "Z",
                Message: targetMode == 2 ? "校准：切换到和弦模式" : "校准：切换到品格模式",
                TimestampMs: startOffsetUs / 1000));
        }
        else
        {
            CurrentMode = 1;
            CurrentPage = 1;
            CurrentSlot = 1;
        }

        wheel.InvalidateCurrentSlot();
        int pageCount = targetMode == 1 ? 5 : 4;
        var (pageSteps, pageDirection) = CircularPageNavigator.GetShortestPath(1, targetPage, pageCount);
        for (int step = 0; step < pageSteps; step++)
        {
            if (!SendPageNavTap(pageDirection, holdMs, wheel.Preset)) return null;
            if (step + 1 < pageSteps) Thread.Sleep(gapMs);
        }
        CurrentPage = targetPage;

        if (targetPage > 1)
        {
            OnSimulationAction?.Invoke(new Bridge.GuitarSimulationActionDto(
                Mode: targetMode == 1 ? "fret" : "chord",
                CurrentPage: $"P{targetPage}", CurrentSlot: 1, ActionType: "page",
                StringNo: 0, Fret: 0, ChordName: null,
                GameKey: UseVirtualGamepad
                    ? (pageDirection >= 0 ? "RB" : "LB")
                    : pageDirection >= 0 ? "T" : "R",
                Message: $"校准：预先切换至 P{targetPage}",
                TimestampMs: startOffsetUs / 1000));
        }

        if (targetSlot is < 1 or > 6) targetSlot = 1;
        if (!SelectSlot(wheel, targetSlot)) return null;
        CurrentSlot = targetSlot;
        int settleMinMs = UseVirtualGamepad ? 12 : 55;
        int settleMaxMs = UseVirtualGamepad ? 40 : 150;
        Thread.Sleep(Math.Clamp((int)Math.Ceiling(PostSelectSettleUs(options) / 1000.0), settleMinMs, settleMaxMs));
        OnSimulationAction?.Invoke(new Bridge.GuitarSimulationActionDto(
            Mode: targetMode == 1 ? "fret" : "chord",
            CurrentPage: $"P{targetPage}", CurrentSlot: targetSlot, ActionType: "recovery",
            StringNo: 0,
            Fret: targetMode == 1 ? (targetPage == 5 ? 0 : (targetPage - 1) * 6 + targetSlot) : 0,
            ChordName: targetMode == 2 ? ResolveSlotLabel(2, targetPage, targetSlot) : null,
            GameKey: $"P{targetPage} / S{targetSlot}",
            Message: "吉他校准完成，准备开始",
            TimestampMs: startOffsetUs / 1000));

        return new GuitarPreparedStart(targetMode, targetPage, targetSlot, firstSoundIndex, initialMousePos);
    }

    private bool SendModeSwitchTap(int holdMs)
    {
        if (!UseVirtualGamepad) return SendTap(GameScanCodes.KeyZ, holdMs);
        if (!_gamepad.SendModeSwitch(true)) return false;
        Thread.Sleep(holdMs);
        return _gamepad.SendModeSwitch(false);
    }

    private bool SendPageNavTap(int direction, int holdMs, WheelPreset? preset = null)
    {
        if (!UseVirtualGamepad)
            return SendTap(direction >= 0
                ? preset?.PageNextScanCode ?? 0x14
                : preset?.PagePrevScanCode ?? 0x13, holdMs);
        if (!_gamepad.SendPageNav(direction, true)) return false;
        Thread.Sleep(holdMs);
        return _gamepad.SendPageNav(direction, false);
    }

    private bool SelectSlot(WheelController wheel, int slot, ushort generation = 0) => UseVirtualGamepad
        ? _gamepad.SelectSlot(slot, generation)
        : wheel.SelectSlot(TargetWindow, slot);

    private long PostSelectSettleUs(GuitarCompilerOptions options) => UseVirtualGamepad
        ? GamepadInputTiming.SlotSelectLeadUs
        : options.WheelMouseSettleUs;

    private static bool SendTap(ushort scanCode, int holdMs)
    {
        if (!KeyboardSender.TrySendBatch([new KeyAction(0, scanCode, isDown: true)])) return false;
        Thread.Sleep(holdMs);
        return KeyboardSender.TrySendBatch([new KeyAction(0, scanCode, isDown: false)]);
    }

    private WheelPreset? _currentWheelPreset;

    public void Pause()
    {
        lock (_stateLock)
        {
            if (!IsPlaying || IsPaused) return;
            IsPaused = true;
            _pauseEvent.Reset();
        }

        // 状态恢复：暂停时立即释放所有琴弦按键，杜绝游戏内物理卡键和吞音
        if (_currentWheelPreset != null)
        {
            ReleaseGuitarKeys(_currentWheelPreset);
        }
    }

    public void Resume()
    {
        lock (_stateLock)
        {
            if (!IsPlaying || !IsPaused) return;
            IsPaused = false;
            _pauseEvent.Set();
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (!IsPlaying && (_workerThread == null || !_workerThread.IsAlive))
            {
                // A completed worker is not an active playback. Start() runs
                // after PrepareInitialState(), so resetting here would erase
                // the slot already selected on the real virtual controller.
                _workerThread = null;
                return;
            }
            _stopRequested = true;
            _pauseEvent.Set();
        }

        if (_workerThread != null && _workerThread.IsAlive)
        {
            _workerThread.Join(500);
            _workerThread = null;
        }

        IsPlaying = false;
        IsPaused = false;
        if (UseVirtualGamepad) _gamepad.ResetAll();
    }

    public void Dispose() => _gamepad.Dispose();

    /// <summary>
    /// 状态快照检测：查询在 targetUs 时间戳下乐曲期望的吉他页码与轮盘槽位
    /// </summary>
    public static (byte Page, byte Slot) InspectExpectedStateAt(IReadOnlyList<GuitarAction>? actions, long targetUs, byte initialPage = 1)
    {
        byte page = initialPage;
        byte slot = 1;
        if (actions == null || actions.Count == 0) return (page, slot);

        for (int i = 0; i < actions.Count && actions[i].TimeUs <= targetUs; i++)
        {
            var a = actions[i];
            if (a.Type == GuitarActionType.ModeSwitchDown) { page = 1; slot = 0; }
            else if (a.Type == GuitarActionType.PageNav) page = a.Value;
            else if (a.Type == GuitarActionType.WheelSelect) slot = a.Value;
        }
        return (page, slot);
    }

    public static (int Mode, byte Page, byte Slot) InspectExpectedHybridStateAt(
        IReadOnlyList<GuitarAction> actions, long targetUs, int initialMode = 1, byte initialPage = 1)
    {
        int mode = initialMode;
        byte page = initialPage;
        byte slot = 0;
        foreach (var action in actions)
        {
            if (action.TimeUs > targetUs) break;
            if (action.Type == GuitarActionType.ModeSwitchDown)
            {
                mode = action.Value;
                page = 1;
                slot = 0;
            }
            else if (action.Type == GuitarActionType.PageNav) page = action.Value;
            else if (action.Type == GuitarActionType.WheelSelect) slot = action.Value;
        }
        return (mode, page, slot);
    }

    private void PlaybackLoop(
        IReadOnlyList<GuitarAction> actions,
        WheelController wheelController,
        long totalDurationUs,
        long startOffsetUs,
        GuitarCompilerOptions options,
        int playMode,
        GuitarPreparedStart? preparedStart)
    {
        _currentWheelPreset = wheelController.Preset;
        PlaybackStopReason stopReason = PlaybackStopReason.Completed;
        string stopMessage = "吉他模式演奏完成";
        Point initialMousePos = Point.Empty;
        var report = new PlaybackDiagnosticReport();
        var state = new GameStateMachine();
        state.Reset(preparedStart?.Mode ?? (playMode == 3 ? 1 : playMode),
            preparedStart?.Page ?? options.InitialPage);
        // Countdown calibration has already selected this slot on the real
        // controller. Keep the scheduler's state in sync with that selection.
        if (preparedStart is not null) state.CurrentSlot = preparedStart.Slot;
        CurrentPage = preparedStart?.Page ?? options.InitialPage;
        CurrentSlot = preparedStart?.Slot ?? 1;
        CurrentMode = state.Mode;
        long totalLateAccumUs = 0;
        GuitarControlTimingGuard? timingGuard = UseVirtualGamepad ? null : new GuitarControlTimingGuard(
            Math.Max(UseVirtualGamepad ? GamepadInputTiming.StringHoldUs : 20_000, options.MinHoldUs),
            Math.Max(UseVirtualGamepad ? GamepadInputTiming.PageHoldUs : 25_000, options.PageKeyHoldUs),
            Math.Max(UseVirtualGamepad ? GamepadInputTiming.PageReleaseGapUs : 15_000, options.PageInterTapGapUs),
            Math.Max(UseVirtualGamepad ? GamepadInputTiming.SlotHoldUs : 55_000, options.WheelMouseSettleUs),
            UseVirtualGamepad ? GamepadInputTiming.SameStringReleaseUs : options.ReleaseGapUs,
            UseVirtualGamepad ? GamepadInputTiming.SameStringReleaseUs : 22_000);

        try
        {
            if (TargetWindow == IntPtr.Zero)
            {
                stopReason = PlaybackStopReason.FocusLost;
                stopMessage = "尚未绑定游戏窗口，已安全停止演奏";
                return;
            }
            if (UseVirtualGamepad && !_gamepad.EnsureConnected())
            {
                stopReason = PlaybackStopReason.Error;
                stopMessage = $"虚拟手柄不可用：{_gamepad.LastError ?? "请确认 ViGEmBus 已安装。"}";
                return;
            }

            // 点击主界面的开始按钮可能暂时让游戏失去焦点。先给用户时间切回游戏，
            // 并在真正获得焦点后才启动时间轴，避免首个轮盘点击被焦点保护直接丢弃。
                if (EnforceForegroundCheck &&
                    KeyboardSender.GetForegroundWindow() != TargetWindow)
            {
                long focusWaitStarted = Stopwatch.GetTimestamp();
                long focusWaitLimitTicks = Stopwatch.Frequency * 8;
                while (!_stopRequested &&
                       KeyboardSender.GetForegroundWindow() != TargetWindow &&
                       Stopwatch.GetTimestamp() - focusWaitStarted < focusWaitLimitTicks)
                {
                    Thread.Sleep(50);
                }

                if (_stopRequested || KeyboardSender.GetForegroundWindow() != TargetWindow)
                {
                    stopReason = _stopRequested ? PlaybackStopReason.UserStopped : PlaybackStopReason.FocusLost;
                    stopMessage = _stopRequested
                        ? "用户手动停止"
                        : "启动等待超时：请先绑定游戏窗口，并在开始后切回游戏窗口";
                    return;
                }
            }

            initialMousePos = preparedStart?.InitialMousePos ?? MouseSender.GetCurrentCursorPos();
            long frequency = Stopwatch.Frequency;
            long startTimestamp = Stopwatch.GetTimestamp();

            // PreRoll 预热支持 (方案 7.5): 若有开局提前量 (负时间戳动作)，平滑提前基准时间，完成初始翻页与第一音槽位点击
            long preRollUs = 0;
            if (preparedStart == null && actions.Count > 0 && actions[0].TimeUs < 0)
            {
                preRollUs = -actions[0].TimeUs + 50_000;
            }

            long offsetTicks = (startOffsetUs * frequency) / 1_000_000;
            long preRollTicks = (preRollUs * frequency) / 1_000_000;
            long baseTimestamp = startTimestamp + preRollTicks - offsetTicks;

            int startIndex = 0;
            while (startIndex < actions.Count && actions[startIndex].TimeUs < (startOffsetUs > 0 ? startOffsetUs : -preRollUs))
            {
                startIndex++;
            }
            if (preparedStart != null)
                startIndex = Math.Max(startIndex, preparedStart.FirstSoundIndex);

            // ==================== 状态恢复序列 (State Recovery Sequence) ====================
            // 当从中间某一秒启动（如 Seek 或跳过开头）时，回溯在 startOffsetUs 之前生效的最新页码与槽位，
            // 并安全向游戏补发翻页按键与选槽，确保实机物理状态与乐曲时间轴绝对一致。
            if (startOffsetUs > 0 && startIndex > 0 && preparedStart == null)
            {
                var (expectedMode, expectedPage, expectedSlot) = playMode is 2 or 3
                    ? InspectExpectedHybridStateAt(actions, startOffsetUs, playMode == 2 ? 2 : 1, options.InitialPage)
                    : (playMode, InspectExpectedStateAt(actions, startOffsetUs, options.InitialPage).Page,
                        InspectExpectedStateAt(actions, startOffsetUs, options.InitialPage).Slot);

                if (expectedMode != state.Mode)
                {
                    if (!SendModeSwitchTap(Math.Max(20, (int)(options.PageKeyHoldUs / 1000))))
                    {
                        stopReason = PlaybackStopReason.Error;
                        stopMessage = "切换吉他模式失败，已停止以避免页面状态错位。";
                        return;
                    }
                    state.Mode = expectedMode;
                    CurrentMode = expectedMode;
                    state.CurrentPage = 1;
                    CurrentPage = 1;
                    state.CurrentSlot = 0;
                    CurrentSlot = 0;
                    wheelController.InvalidateCurrentSlot();
                }

                // 1. 实机翻页状态恢复
                if (expectedPage != state.CurrentPage)
                {
                    int pageCount = state.Mode == 1 ? 5 : 4;
                    var (steps, direction) = CircularPageNavigator.GetShortestPath(state.CurrentPage, expectedPage, pageCount);
                    int holdMs = Math.Clamp((int)(options.PageKeyHoldUs / 1000), 20, 100);
                    int gapMs = Math.Clamp((int)(options.PageInterTapGapUs / 1000), 10, 40);

                    for (int s = 0; s < steps; s++)
                    {
                        if (_stopRequested) break;
                        if (!SendPageNavTap(direction, holdMs, wheelController.Preset))
                        {
                            stopReason = PlaybackStopReason.Error;
                            stopMessage = "恢复吉他页面失败，已停止以避免演奏错位。";
                            return;
                        }
                        if (s < steps - 1) Thread.Sleep(gapMs);
                    }
                    state.CurrentPage = expectedPage;
                    CurrentPage = expectedPage;
                }

                // 2. 实机轮盘槽位对齐恢复
                if (expectedSlot > 0)
                {
                    if (SelectSlot(wheelController, expectedSlot))
                    {
                        state.CurrentSlot = expectedSlot;
                        CurrentSlot = (byte)expectedSlot;
                        Thread.Sleep(Math.Clamp((int)(PostSelectSettleUs(options) / 1000), 15, 60));
                    }
                    else
                    {
                        state.CurrentSlot = 0;
                        report.Records.Add(new PlaybackDiagnosticRecord
                        {
                            PlannedTimeUs = startOffsetUs,
                            ActualTimeUs = startOffsetUs,
                            ActionType = "WheelSelect",
                            ExpectedPage = expectedPage,
                            StatePage = state.CurrentPage,
                            ExpectedSlot = (byte)expectedSlot,
                            StateSlot = 0,
                            IsSkipped = true,
                            SkipReason = "WheelInputFailed"
                        });
                    }
                }

                // 3. 派发状态恢复通知与模拟器对齐快照
                int activeMode = state.Mode;
                string recoveryLabel = ResolveSlotLabel(activeMode, state.CurrentPage, state.CurrentSlot);
                int fretNum = activeMode == 1 ? (state.CurrentPage == 5 ? 0 : (state.CurrentPage - 1) * 6 + state.CurrentSlot) : 0;
                OnSimulationAction?.Invoke(new Bridge.GuitarSimulationActionDto(
                    Mode: activeMode == 1 ? "fret" : "chord",
                    CurrentPage: $"P{state.CurrentPage}",
                    CurrentSlot: state.CurrentSlot > 0 ? state.CurrentSlot : 1,
                    ActionType: "recovery",
                    StringNo: 0,
                    Fret: fretNum,
                    ChordName: activeMode == 2 ? recoveryLabel : null,
                    GameKey: $"P{state.CurrentPage} / S{state.CurrentSlot}",
                    Message: $"状态恢复完成: 对齐至 P{state.CurrentPage} 槽位 {state.CurrentSlot} ({recoveryLabel})",
                    TimestampMs: startOffsetUs / 1000
                ));
            }

            long lastUiUpdateTimestamp = 0;
            long uiUpdateIntervalTicks = (frequency * 50) / 1000; // 50ms UI 刷新

            _skippedGamepadPairIds.Clear();
            for (int i = startIndex; i < actions.Count; i++)
            {
                if (_stopRequested)
                {
                    stopReason = PlaybackStopReason.UserStopped;
                    stopMessage = "用户手动停止";
                    break;
                }

                if (IsPaused)
                {
                    long pauseStart = Stopwatch.GetTimestamp();
                    _pauseEvent.Wait();
                    if (_stopRequested)
                    {
                        stopReason = PlaybackStopReason.UserStopped;
                        stopMessage = "用户手动停止";
                        break;
                    }
                    baseTimestamp += (Stopwatch.GetTimestamp() - pauseStart);
                }

                // 前台焦点保护: 切出窗口立即停止，防止鼠标误点其他应用
                if (TargetWindow == IntPtr.Zero || EnforceForegroundCheck)
                {
                    if (TargetWindow == IntPtr.Zero || KeyboardSender.GetForegroundWindow() != TargetWindow)
                    {
                        stopReason = PlaybackStopReason.FocusLost;
                        stopMessage = TargetWindow == IntPtr.Zero
                            ? "尚未绑定游戏窗口，已安全停止演奏"
                            : "游戏窗口失去焦点，已自动安全停止";
                        break;
                    }
                }

                var act = actions[i];
                long targetTicks = baseTimestamp + (act.TimeUs * frequency) / 1_000_000;

                // 两阶段混合等待
                while (true)
                {
                    if (_stopRequested || IsPaused) break;

                    long currentTicks = Stopwatch.GetTimestamp();
                    long remainingTicks = targetTicks - currentTicks;
                    if (remainingTicks <= 0) break;

                    long remainingMs = (remainingTicks * 1000) / frequency;

                    if (currentTicks - lastUiUpdateTimestamp >= uiUpdateIntervalTicks)
                    {
                        lastUiUpdateTimestamp = currentTicks;
                        long currentUs = ((currentTicks - baseTimestamp) * 1_000_000) / frequency;
                        OnProgressChanged?.Invoke(Math.Clamp(currentUs, 0, totalDurationUs), totalDurationUs);
                    }

                    // Windows may round Thread.Sleep(1) up to the system timer
                    // interval (often 10–16 ms). Sleeping in the final few ms
                    // makes dense guitar notes arrive late enough to miss the
                    // game's input window. Sleep only while comfortably early,
                    // yield near the deadline, then use a very short spin.
                    if (remainingMs > 20)
                    {
                        int sleepMs = (int)Math.Min(10, remainingMs - 15);
                        Thread.Sleep(Math.Max(1, sleepMs));
                    }
                    else if (remainingMs > 2)
                    {
                        Thread.Yield();
                    }
                    else if (remainingTicks > frequency / 2_000)
                    {
                        // Yield during the final ~0.5–2 ms. Spinning across the whole
                        // 2 ms window for every dense action can monopolize a CPU core.
                        Thread.Yield();
                    }
                    else
                    {
                        Thread.SpinWait(20);
                    }
                }

                if (_stopRequested)
                {
                    stopReason = PlaybackStopReason.UserStopped;
                    stopMessage = "用户手动停止";
                    break;
                }

                if (IsPaused)
                {
                    if (_currentWheelPreset != null)
                    {
                        ReleaseGuitarKeys(_currentWheelPreset);
                    }
                    long pauseStart = Stopwatch.GetTimestamp();
                    _pauseEvent.Wait();
                    if (_stopRequested)
                    {
                        stopReason = PlaybackStopReason.UserStopped;
                        stopMessage = "用户手动停止";
                        break;
                    }
                    baseTimestamp += (Stopwatch.GetTimestamp() - pauseStart);
                    i--;
                    continue;
                }

                long execNowTicks = Stopwatch.GetTimestamp();
                long nowUs = ((execNowTicks - baseTimestamp) * 1_000_000) / frequency;
                if (timingGuard != null)
                {
                    long earliestUs = timingGuard.EarliestExecutionUs(act, nowUs);
                    if (act.Type is GuitarActionType.StringDown or GuitarActionType.StringUp)
                    {
                        for (int batchIndex = i + 1; batchIndex < actions.Count &&
                             actions[batchIndex].TimeUs == act.TimeUs && actions[batchIndex].Type == act.Type; batchIndex++)
                            earliestUs = Math.Max(earliestUs,
                                timingGuard.EarliestExecutionUs(actions[batchIndex], nowUs));
                    }
                    while (nowUs < earliestUs && !_stopRequested && !IsPaused)
                    {
                        long remainingUs = earliestUs - nowUs;
                        if (remainingUs > 2_000)
                            Thread.Sleep((int)Math.Min(10, (remainingUs + 999) / 1_000));
                        else
                            Thread.Yield();
                        nowUs = ((Stopwatch.GetTimestamp() - baseTimestamp) * 1_000_000) / frequency;
                    }
                    if (_stopRequested)
                    {
                        stopReason = PlaybackStopReason.UserStopped;
                        stopMessage = "用户手动停止";
                        break;
                    }
                    if (IsPaused) { i--; continue; }
                    if (TargetWindow == IntPtr.Zero ||
                        (EnforceForegroundCheck && KeyboardSender.GetForegroundWindow() != TargetWindow))
                    {
                        stopReason = PlaybackStopReason.FocusLost;
                        stopMessage = TargetWindow == IntPtr.Zero
                            ? "尚未绑定游戏窗口，已安全停止演奏"
                            : "游戏窗口失去焦点，已自动安全停止";
                        break;
                    }
                }
                long lateUs = Math.Max(0, nowUs - act.TimeUs);
                totalLateAccumUs += lateUs;
                if (lateUs > 15_000)
                {
                    report.LateActionsCount++;
                }
                if (lateUs > report.MaxLateUs)
                {
                    report.MaxLateUs = lateUs;
                }
                report.TotalActions++;

                // 批处理 StringDown / StringUp (同时按下的弦，如 Block Chord 齐奏原子批量下发)
                int activeMode = state.Mode;
                if (act.Type == GuitarActionType.StringDown || act.Type == GuitarActionType.StringUp)
                {
                    var batchActions = new List<GuitarAction> { act };
                    while (i + 1 < actions.Count &&
                           actions[i + 1].TimeUs == act.TimeUs &&
                           actions[i + 1].Type == act.Type)
                    {
                        i++;
                        batchActions.Add(actions[i]);
                    }

                    if (act.Type == GuitarActionType.StringDown)
                    {
                        if (UseVirtualGamepad)
                        {
                            foreach (var note in batchActions.ToArray())
                            {
                                if (nowUs <= note.DeadlineUs && nowUs - note.TimeUs <= 45_000) continue;
                                _skippedGamepadPairIds.Add(note.ScanCode);
                                batchActions.Remove(note);
                                report.SkippedNotes++;
                                report.Records.Add(new PlaybackDiagnosticRecord
                                {
                                    PlannedTimeUs = note.TimeUs, ActualTimeUs = nowUs, ActionType = "StringDown",
                                    ExpectedPage = note.RequiredPage, StatePage = state.CurrentPage,
                                    ExpectedSlot = note.RequiredSlot, StateSlot = state.CurrentSlot,
                                    MidiNote = note.NoteNumber, GuitarString = note.Value, IsSkipped = true,
                                    SkipReason = "GamepadNoteDeadlineMissed"
                                });
                            }
                            if (batchActions.Count == 0) continue;
                        }
                        // 硬件防竞态保护: 轮盘选槽与翻页正在稳定中时，精准等待稳定完成，严防键盘比鼠标抢先击弦
                        long busyUntilUs = Math.Max(state.WheelBusyUntilUs, state.NavBusyUntilUs);
                        if (!UseVirtualGamepad && (activeMode == 1 || act.RequiredPage > 0) && busyUntilUs > nowUs)
                        {
                            long waitUs = busyUntilUs - nowUs;
                            if (waitUs > 0 && waitUs <= 100_000)
                            {
                                int waitMs = (int)(waitUs / 1000) + 1;
                                Thread.Sleep(waitMs);
                                long newNowTicks = Stopwatch.GetTimestamp();
                                nowUs = ((newNowTicks - baseTimestamp) * 1_000_000) / frequency;
                            }
                        }

                        // 槽位强制对齐: 若当前轮盘槽位未对齐目标槽位，立即同步选槽并等待生效
                        if (!UseVirtualGamepad && act.RequiredSlot > 0 && state.CurrentSlot != act.RequiredSlot)
                        {
                            long selectStarted = Stopwatch.GetTimestamp();
                            if (SelectSlot(wheelController, act.RequiredSlot))
                            {
                                nowUs += (Stopwatch.GetTimestamp() - selectStarted) * 1_000_000 / frequency;
                                state.CurrentSlot = act.RequiredSlot;
                                CurrentSlot = act.RequiredSlot;
                                state.WheelBusyUntilUs = nowUs + PostSelectSettleUs(options);
                                int settleMs = Math.Clamp((int)(PostSelectSettleUs(options) / 1000), 10, 80);
                                long settleStarted = Stopwatch.GetTimestamp();
                                Thread.Sleep(settleMs);
                                nowUs += (Stopwatch.GetTimestamp() - settleStarted) * 1_000_000 / frequency;
                            }
                            else
                            {
                                foreach (var b in batchActions)
                                {
                                    report.SkippedNotes++;
                                    report.Records.Add(new PlaybackDiagnosticRecord
                                    {
                                        PlannedTimeUs = b.TimeUs,
                                        ActualTimeUs = nowUs,
                                        ActionType = "StringDown",
                                        ExpectedPage = b.RequiredPage,
                                        StatePage = state.CurrentPage,
                                        ExpectedSlot = b.RequiredSlot,
                                        StateSlot = state.CurrentSlot,
                                        MidiNote = b.NoteNumber,
                                        GuitarString = b.Value,
                                        IsSkipped = true,
                                        SkipReason = "WheelInputFailed"
                                    });
                                }
                                continue;
                            }
                        }

                        // 方案 7.3: 发音前置条件检查 (必须满足目标 Page/Slot 已就绪)
                        bool isReady = (activeMode == 1)
                            ? state.IsFretReady(act.RequiredPage, act.RequiredSlot, nowUs)
                            : state.IsChordReady(act.RequiredPage, act.RequiredSlot, nowUs);

                        if ((activeMode == 1 || act.RequiredPage > 0) && !isReady)
                        {
                            report.LateActionsCount++;
                            foreach (var b in batchActions)
                            {
                                report.Records.Add(new PlaybackDiagnosticRecord
                                {
                                    PlannedTimeUs = b.TimeUs,
                                    ActualTimeUs = nowUs,
                                    ActionType = "StringDown",
                                    ExpectedPage = b.RequiredPage,
                                    StatePage = state.CurrentPage,
                                    ExpectedSlot = b.RequiredSlot,
                                    StateSlot = state.CurrentSlot,
                                    MidiNote = b.NoteNumber,
                                    GuitarString = b.Value,
                                    IsSkipped = false,
                                    SkipReason = (activeMode == 1) ? "FretLagWarning" : "ChordLagWarning"
                                });
                            }
                        }

                        bool stringsDownSent = UseVirtualGamepad
                            ? _gamepad.SetStringsBatch(batchActions.Select(b => (b.Value, true)).ToArray())
                            : KeyboardSender.TrySendBatch(batchActions.Select(b => new KeyAction(b.TimeUs, b.ScanCode, isDown: true)).ToList());
                        if (!stringsDownSent)
                        {
                            if (UseVirtualGamepad) _gamepad.SetStringsBatch(batchActions.Select(b => (b.Value, false)).ToArray());
                            else KeyboardSender.TrySendBatch(batchActions.Select(b => new KeyAction(b.TimeUs, b.ScanCode, isDown: false)).ToList());
                            if (UseVirtualGamepad)
                                foreach (var note in batchActions) _skippedGamepadPairIds.Add(note.ScanCode);
                            report.SkippedNotes += batchActions.Count;
                            foreach (var b in batchActions)
                            {
                                report.Records.Add(new PlaybackDiagnosticRecord
                                {
                                    PlannedTimeUs = b.TimeUs,
                                    ActualTimeUs = nowUs,
                                    ActionType = "StringDown",
                                    ExpectedPage = b.RequiredPage,
                                    StatePage = state.CurrentPage,
                                    ExpectedSlot = b.RequiredSlot,
                                    StateSlot = state.CurrentSlot,
                                    MidiNote = b.NoteNumber,
                                    GuitarString = b.Value,
                                    IsSkipped = true,
                                        SkipReason = UseVirtualGamepad ? "GamepadInputFailed" : "KeyboardInputFailed"
                                });
                            }
                            continue;
                        }

                        if (batchActions.Count >= 3)
                        {
                            report.BlockChordCount++;
                        }
                        else
                        {
                            report.ArpeggioCount += batchActions.Count;
                        }
                        report.PlayedSoundNotes += batchActions.Count;
                        if (timingGuard != null)
                        {
                            long actualUs = ((Stopwatch.GetTimestamp() - baseTimestamp) * 1_000_000) / frequency;
                            foreach (var b in batchActions) timingGuard.MarkExecuted(b, actualUs);
                        }

                        foreach (var b in batchActions)
                        {
                            state.HeldStringKeys.Add(b.Value);
                            int digitKey = GameScanCodes.GetGameDigit(b.Value);
                            int fret = b.NoteNumber > 0
                                ? Math.Max(0, b.NoteNumber - GuitarTuning.Standard.GetOpenMidiNote(b.Value))
                                : (activeMode == 1 && state.CurrentPage > 0 ? (state.CurrentPage - 1) * 6 + Math.Max(1, (int)state.CurrentSlot) : 0);
                            OnSimulationAction?.Invoke(new Bridge.GuitarSimulationActionDto(
                                Mode: activeMode == 1 ? "fret" : "chord",
                                CurrentPage: $"P{state.CurrentPage}",
                                CurrentSlot: state.CurrentSlot > 0 ? state.CurrentSlot : 1,
                                ActionType: "pluck",
                                StringNo: b.Value,
                                Fret: fret,
                                ChordName: activeMode == 2 ? ResolveSlotLabel(activeMode, state.CurrentPage, state.CurrentSlot) : null,
                                GameKey: digitKey,
                                Message: string.Empty,
                                TimestampMs: nowUs / 1000
                            ));
                        }
                    }
                    else
                    {
                        if (UseVirtualGamepad)
                        {
                            batchActions.RemoveAll(note => _skippedGamepadPairIds.Remove(note.ScanCode));
                            if (batchActions.Count == 0) continue;
                        }
                        bool stringsUpSent = UseVirtualGamepad
                            ? _gamepad.SetStringsBatch(batchActions.Select(b => (b.Value, false)).ToArray())
                            : KeyboardSender.TrySendBatch(batchActions.Select(b => new KeyAction(b.TimeUs, b.ScanCode, isDown: false)).ToList());
                        if (!stringsUpSent)
                        {
                            if (UseVirtualGamepad)
                            {
                                _gamepad.ResetAll();
                                state.HeldStringKeys.Clear();
                                continue;
                            }
                            stopReason = PlaybackStopReason.Error;
                            stopMessage = "琴弦按键释放失败，已安全停止以避免后续控制动作失步。";
                            break;
                        }
                        if (timingGuard != null)
                        {
                            long actualUs = ((Stopwatch.GetTimestamp() - baseTimestamp) * 1_000_000) / frequency;
                            foreach (var b in batchActions) timingGuard.MarkExecuted(b, actualUs);
                        }
                        foreach (var b in batchActions)
                        {
                            state.HeldStringKeys.Remove(b.Value);
                            OnSimulationAction?.Invoke(new Bridge.GuitarSimulationActionDto(
                                Mode: activeMode == 1 ? "fret" : "chord",
                                CurrentPage: $"P{state.CurrentPage}",
                                CurrentSlot: state.CurrentSlot > 0 ? state.CurrentSlot : 1,
                                ActionType: "release",
                                StringNo: b.Value,
                                Fret: 0,
                                ChordName: activeMode == 2 ? ResolveSlotLabel(activeMode, state.CurrentPage, state.CurrentSlot) : null,
                                GameKey: GameScanCodes.GetGameDigit(b.Value),
                                Message: string.Empty,
                                TimestampMs: nowUs / 1000
                            ));
                        }
                    }
                }
                else
                {
                    if (!ExecuteAction(act, wheelController, state, options, nowUs, report, playMode))
                    {
                        if (UseVirtualGamepad)
                        {
                            // A failed report is local to this control event. The
                            // state transition is committed only by ExecuteAction
                            // after a successful submit, so later notes can still
                            // run their normal page/slot feasibility checks.
                            continue;
                        }
                        stopReason = PlaybackStopReason.Error;
                        stopMessage = "键盘或轮盘输入未能确认，已安全停止并释放按键，避免页面或和弦错位。";
                        break;
                    }
                    if (timingGuard != null)
                        timingGuard.MarkExecuted(act,
                            ((Stopwatch.GetTimestamp() - baseTimestamp) * 1_000_000) / frequency);
                }

                // 刷新进度
                long nowTicks = Stopwatch.GetTimestamp();
                if (nowTicks - lastUiUpdateTimestamp >= uiUpdateIntervalTicks)
                {
                    lastUiUpdateTimestamp = nowTicks;
                    long currentUs = ((nowTicks - baseTimestamp) * 1_000_000) / frequency;
                    OnProgressChanged?.Invoke(Math.Clamp(currentUs, 0, totalDurationUs), totalDurationUs);
                }
            }

            if (!_stopRequested && stopReason == PlaybackStopReason.Completed)
            {
                OnProgressChanged?.Invoke(totalDurationUs, totalDurationUs);
            }
        }
        catch (Exception ex)
        {
            stopReason = PlaybackStopReason.Error;
            stopMessage = $"播放异常: {ex.Message}";
        }
        finally
        {
            // 1. 释放所有 1~6 弦数字按键，绝不卡键
            ReleaseGuitarKeys(wheelController.Preset);

            // 2. 复位轮盘与恢复鼠标原位置
            if (!UseVirtualGamepad) wheelController.Reset(initialMousePos);

            // 品格模式正常弹完后切到和弦模式再切回来，让游戏重置琴弦状态。
            // 仅在原绑定游戏窗口仍处于前台时发送，避免把 Z 键输入到其他程序。
            if (stopReason == PlaybackStopReason.Completed &&
                (playMode is 1 or 3 || playMode == 2 && state.Mode == 1))
            {
                int resetTaps = (playMode == 2 && state.Mode == 1) || (playMode == 3 && state.Mode == 2) ? 1 : 2;
                bool restored = TryRestoreFretStrings(resetTaps);
                if (restored)
                {
                    CurrentMode = 1;
                    CurrentPage = 1;
                    CurrentSlot = 0;
                }
                stopMessage = restored
                    ? "吉他模式演奏完成，琴弦状态已复位。"
                    : "吉他模式演奏完成；游戏窗口不在前台或输入失败，未执行模式复位。";
            }

            if (UseVirtualGamepad) _gamepad.ResetAll();

            if (report.TotalActions > 0)
            {
                report.AverageLateUs = (double)totalLateAccumUs / report.TotalActions;
            }
            LastDiagnosticReport = report;

            lock (_stateLock)
            {
                IsPlaying = false;
                IsPaused = false;
            }

            OnPlaybackDiagnosticsCompleted?.Invoke(report);
            OnPlaybackStopped?.Invoke(stopReason, stopMessage);
        }
    }

    private bool TryRestoreFretStrings(int tapCount = 2)
    {
        if (TargetWindow == IntPtr.Zero || KeyboardSender.GetForegroundWindow() != TargetWindow)
            return false;

        const int keyHoldMs = 45;
        const int keyGapMs = 60;
        for (int tap = 0; tap < tapCount; tap++)
        {
            if (KeyboardSender.GetForegroundWindow() != TargetWindow)
                return false;

            bool pressed = UseVirtualGamepad
                ? _gamepad.SendModeSwitch(true)
                : KeyboardSender.TrySendBatch(new[] { new KeyAction(0, GameScanCodes.KeyZ, isDown: true) });
            if (!pressed) return false;

            Thread.Sleep(keyHoldMs);
            bool released = UseVirtualGamepad
                ? _gamepad.SendModeSwitch(false)
                : KeyboardSender.TrySendBatch(new[] { new KeyAction(0, GameScanCodes.KeyZ, isDown: false) });
            if (!released) return false;
            if (tap < tapCount - 1) Thread.Sleep(keyGapMs);
        }

        return true;
    }

    private bool ExecuteAction(
        GuitarAction act,
        WheelController wheel,
        GameStateMachine state,
        GuitarCompilerOptions options,
        long nowUs,
        PlaybackDiagnosticReport report,
        int playMode)
    {
        int activeMode = state.Mode;
        switch (act.Type)
        {
            case GuitarActionType.ModeSwitchDown:
            {
                bool sent = UseVirtualGamepad
                    ? _gamepad.SendModeSwitch(true)
                    : KeyboardSender.TrySendBatch(new[] { new KeyAction(act.TimeUs, act.ScanCode, isDown: true) });
                if (!sent)
                    return false;
                state.Mode = act.Value;
                CurrentMode = state.Mode;
                state.CurrentPage = 1;
                CurrentPage = 1;
                state.CurrentSlot = 0;
                CurrentSlot = 0;
                state.NavBusyUntilUs = nowUs + options.PageKeyHoldUs;
                wheel.InvalidateCurrentSlot();
                OnSimulationAction?.Invoke(new Bridge.GuitarSimulationActionDto(
                    Mode: state.Mode == 1 ? "fret" : "chord",
                    CurrentPage: "P1", CurrentSlot: 1, ActionType: "mode",
                    StringNo: 0, Fret: 0, ChordName: null, GameKey: "Z",
                    Message: state.Mode == 1 ? "切换到品格模式，页面归位 P1" : "切换到和弦模式，页面归位 P1",
                    TimestampMs: nowUs / 1000));
                break;
            }
            case GuitarActionType.ModeSwitchUp:
                if (UseVirtualGamepad)
                {
                    if (!_gamepad.SendModeSwitch(false)) return false;
                }
                else KeyboardSender.TrySendBatch(new[] { new KeyAction(act.TimeUs, act.ScanCode, isDown: false) });
                break;

            case GuitarActionType.PageNav:
            {
                bool sent = UseVirtualGamepad
                    ? _gamepad.SendPageNav(act.ScanCode == wheel.Preset.PageNextScanCode ? 1 : -1, true)
                    : KeyboardSender.TrySendBatch(new[] { new KeyAction(act.TimeUs, act.ScanCode, isDown: true) });
                if (!sent)
                {
                    report.Records.Add(new PlaybackDiagnosticRecord
                    {
                        PlannedTimeUs = act.TimeUs,
                        ActualTimeUs = nowUs,
                        ActionType = "PageNav",
                        ExpectedPage = act.Value,
                        StatePage = state.CurrentPage,
                        IsSkipped = true,
                        SkipReason = "KeyboardInputFailed"
                    });
                    return false;
                }

                state.CurrentPage = act.Value;
                CurrentPage = act.Value;
                state.CurrentSlot = 0;
                CurrentSlot = 0;
                state.NavBusyUntilUs = nowUs + options.PageInterTapGapUs;
                wheel.InvalidateCurrentSlot();
                report.PageTapCount++;

                string navKey = UseVirtualGamepad
                    ? (act.ScanCode == wheel.Preset.PageNextScanCode ? "RB" : "LB")
                    : act.ScanCode == 0x14 ? "T" : "R";
                OnSimulationAction?.Invoke(new Bridge.GuitarSimulationActionDto(
                    Mode: activeMode == 1 ? "fret" : "chord",
                    CurrentPage: $"P{act.Value}",
                    CurrentSlot: state.CurrentSlot > 0 ? state.CurrentSlot : 1,
                    ActionType: "page",
                    StringNo: 0,
                    Fret: 0,
                    ChordName: null,
                    GameKey: navKey,
                    Message: $"翻页动作：切换至 P{act.Value} (按键 {navKey})",
                    TimestampMs: nowUs / 1000
                ));
                break;
            }

            case GuitarActionType.PageNavUp:
            {
                bool sent = UseVirtualGamepad
                    ? _gamepad.SendPageNav(act.ScanCode == wheel.Preset.PageNextScanCode ? 1 : -1, false)
                    : KeyboardSender.TrySendBatch(new[] { new KeyAction(act.TimeUs, act.ScanCode, isDown: false) });
                if (!sent)
                {
                    if (UseVirtualGamepad) _gamepad.SendPageNav(act.ScanCode == wheel.Preset.PageNextScanCode ? 1 : -1, false);
                    else KeyboardSender.TrySendBatch(new[] { new KeyAction(act.TimeUs, act.ScanCode, isDown: false) });
                    report.Records.Add(new PlaybackDiagnosticRecord
                    {
                        PlannedTimeUs = act.TimeUs,
                        ActualTimeUs = nowUs,
                        ActionType = "PageNavUp",
                        ExpectedPage = act.Value,
                        StatePage = state.CurrentPage,
                        IsSkipped = true,
                        SkipReason = "KeyboardReleaseFailed"
                    });
                    return false;
                }
                break;
            }

            case GuitarActionType.WheelSelect:
            {
                long selectStarted = Stopwatch.GetTimestamp();
                if (!SelectSlot(wheel, act.Value, act.ScanCode))
                {
                    report.Records.Add(new PlaybackDiagnosticRecord
                    {
                        PlannedTimeUs = act.TimeUs,
                        ActualTimeUs = nowUs,
                        ActionType = "WheelSelect",
                        ExpectedPage = act.RequiredPage,
                        StatePage = state.CurrentPage,
                        ExpectedSlot = act.Value,
                        StateSlot = state.CurrentSlot,
                        IsSkipped = true,
                        SkipReason = "WheelInputFailed"
                    });
                    return false;
                }

                long elapsedUs = (Stopwatch.GetTimestamp() - selectStarted) * 1_000_000 / Stopwatch.Frequency;
                nowUs += elapsedUs;

                state.CurrentSlot = act.Value;
                CurrentSlot = act.Value;
                // SelectSlot may first finish a held note and a D-pad neutral
                // interval. Stabilization starts only after the LS report.
                state.WheelBusyUntilUs = nowUs + PostSelectSettleUs(options);
                report.WheelSelectCount++;

                string slotLabel = ResolveSlotLabel(activeMode, state.CurrentPage, act.Value);
                int fretNum = activeMode == 1 ? (state.CurrentPage == 5 ? 0 : (state.CurrentPage - 1) * 6 + act.Value) : 0;
                OnSimulationAction?.Invoke(new Bridge.GuitarSimulationActionDto(
                    Mode: activeMode == 1 ? "fret" : "chord",
                    CurrentPage: $"P{state.CurrentPage}",
                    CurrentSlot: act.Value,
                    ActionType: "slot",
                    StringNo: 0,
                    Fret: fretNum,
                    ChordName: activeMode == 2 ? slotLabel : null,
                    GameKey: $"Slot {act.Value}",
                    Message: $"轮盘选槽：Slot {act.Value} ({slotLabel})",
                    TimestampMs: nowUs / 1000
                ));
                break;
            }

            case GuitarActionType.WheelSelectUp:
                if (UseVirtualGamepad && !_gamepad.NeutralizeSlot(act.Value, act.ScanCode)) return false;
                break;

            case GuitarActionType.StrumClick:
            {
                long busyUntilUs = Math.Max(state.WheelBusyUntilUs, state.NavBusyUntilUs);
                if (!UseVirtualGamepad && (activeMode == 1 || act.RequiredPage > 0) && busyUntilUs > nowUs)
                {
                    long waitUs = busyUntilUs - nowUs;
                    if (waitUs > 0 && waitUs <= 100_000)
                    {
                        int waitMs = (int)(waitUs / 1000) + 1;
                        long waitStarted = Stopwatch.GetTimestamp();
                        Thread.Sleep(waitMs);
                        nowUs += (Stopwatch.GetTimestamp() - waitStarted) * 1_000_000 / Stopwatch.Frequency;
                    }
                }
                if (!UseVirtualGamepad && act.RequiredSlot > 0 && state.CurrentSlot != act.RequiredSlot)
                {
                    long selectStarted = Stopwatch.GetTimestamp();
                    if (!SelectSlot(wheel, act.RequiredSlot))
                    {
                        report.SkippedNotes++;
                        report.Records.Add(new PlaybackDiagnosticRecord
                        {
                            PlannedTimeUs = act.TimeUs,
                            ActualTimeUs = nowUs,
                            ActionType = "StrumClick",
                            ExpectedPage = act.RequiredPage,
                            StatePage = state.CurrentPage,
                            ExpectedSlot = act.RequiredSlot,
                            StateSlot = state.CurrentSlot,
                            IsSkipped = true,
                            SkipReason = "WheelInputFailed"
                        });
                        break;
                    }

                    nowUs += (Stopwatch.GetTimestamp() - selectStarted) * 1_000_000 / Stopwatch.Frequency;
                    state.CurrentSlot = act.RequiredSlot;
                    CurrentSlot = act.RequiredSlot;
                    state.WheelBusyUntilUs = nowUs + PostSelectSettleUs(options);
                    int settleMs = Math.Clamp((int)(PostSelectSettleUs(options) / 1000), 10, 80);
                    long settleStarted = Stopwatch.GetTimestamp();
                    Thread.Sleep(settleMs);
                    nowUs += (Stopwatch.GetTimestamp() - settleStarted) * 1_000_000 / Stopwatch.Frequency;
                }

                if (act.RequiredPage > 0 && !state.IsChordReady(act.RequiredPage, act.RequiredSlot, nowUs))
                {
                    report.LateActionsCount++;
                    report.Records.Add(new PlaybackDiagnosticRecord
                    {
                        PlannedTimeUs = act.TimeUs,
                        ActualTimeUs = nowUs,
                        ActionType = "StrumClick",
                        ExpectedPage = act.RequiredPage,
                        StatePage = state.CurrentPage,
                        ExpectedSlot = act.RequiredSlot,
                        StateSlot = state.CurrentSlot,
                        IsSkipped = false,
                        SkipReason = "ChordLagWarning"
                    });
                }

                bool strumSent = UseVirtualGamepad
                    ? _gamepad.SetStrum(act.MouseButton, true)
                    : wheel.SendStrumClick(TargetWindow, act.MouseButton);
                if (!strumSent)
                {
                    report.SkippedNotes++;
                    report.Records.Add(new PlaybackDiagnosticRecord
                    {
                        PlannedTimeUs = act.TimeUs,
                        ActualTimeUs = nowUs,
                        ActionType = "StrumClick",
                        ExpectedPage = act.RequiredPage,
                        StatePage = state.CurrentPage,
                        ExpectedSlot = act.RequiredSlot,
                        StateSlot = state.CurrentSlot,
                        IsSkipped = true,
                        SkipReason = "MouseInputFailed"
                    });
                    break;
                }

                report.StrumCount++;
                report.PlayedSoundNotes++;

                bool isDown = act.MouseButton == MouseButton.Right;
                string chordName = ResolveSlotLabel(2, state.CurrentPage, state.CurrentSlot);
                OnSimulationAction?.Invoke(new Bridge.GuitarSimulationActionDto(
                    Mode: "chord",
                    CurrentPage: $"P{state.CurrentPage}",
                    CurrentSlot: state.CurrentSlot > 0 ? state.CurrentSlot : 1,
                    ActionType: isDown ? "strumDown" : "strumUp",
                    StringNo: 0,
                    Fret: 0,
                    ChordName: chordName,
                    GameKey: isDown ? "鼠标右键" : "鼠标左键",
                    Message: $"扫弦动作：{(isDown ? "下扫弦 (鼠标右键)" : "上扫弦 (鼠标左键)")} [{chordName}]",
                    TimestampMs: nowUs / 1000
                ));
                break;
            }

            case GuitarActionType.StrumUp:
                if (UseVirtualGamepad && !_gamepad.SetStrum(MouseButton.Left, false)) return false;
                break;

            case GuitarActionType.StringDown:
            {
                long busyUntilUs = Math.Max(state.WheelBusyUntilUs, state.NavBusyUntilUs);
                if (!UseVirtualGamepad && act.RequiredPage > 0 && busyUntilUs > nowUs)
                {
                    long waitUs = busyUntilUs - nowUs;
                    if (waitUs > 0 && waitUs <= 100_000)
                    {
                        int waitMs = (int)(waitUs / 1000) + 1;
                        Thread.Sleep(waitMs);
                    }
                }
                if (!UseVirtualGamepad && act.RequiredSlot > 0 && state.CurrentSlot != act.RequiredSlot)
                {
                    long selectStarted = Stopwatch.GetTimestamp();
                    if (!SelectSlot(wheel, act.RequiredSlot))
                    {
                        report.SkippedNotes++;
                        report.Records.Add(new PlaybackDiagnosticRecord
                        {
                            PlannedTimeUs = act.TimeUs,
                            ActualTimeUs = nowUs,
                            ActionType = "StringDown",
                            ExpectedPage = act.RequiredPage,
                            StatePage = state.CurrentPage,
                            ExpectedSlot = act.RequiredSlot,
                            StateSlot = state.CurrentSlot,
                            MidiNote = act.NoteNumber,
                            GuitarString = act.Value,
                            IsSkipped = true,
                            SkipReason = "WheelInputFailed"
                        });
                        break;
                    }
                    nowUs += (Stopwatch.GetTimestamp() - selectStarted) * 1_000_000 / Stopwatch.Frequency;
                    state.CurrentSlot = act.RequiredSlot;
                    CurrentSlot = act.RequiredSlot;
                    state.WheelBusyUntilUs = nowUs + PostSelectSettleUs(options);
                    int settleMs = Math.Clamp((int)(PostSelectSettleUs(options) / 1000), 10, 80);
                    long settleStarted = Stopwatch.GetTimestamp();
                    Thread.Sleep(settleMs);
                    nowUs += (Stopwatch.GetTimestamp() - settleStarted) * 1_000_000 / Stopwatch.Frequency;
                }

                bool isReady = (activeMode == 1)
                    ? state.IsFretReady(act.RequiredPage, act.RequiredSlot, nowUs)
                    : state.IsChordReady(act.RequiredPage, act.RequiredSlot, nowUs);

                if ((activeMode == 1 || act.RequiredPage > 0) && !isReady)
                {
                    report.LateActionsCount++;
                    report.Records.Add(new PlaybackDiagnosticRecord
                    {
                        PlannedTimeUs = act.TimeUs,
                        ActualTimeUs = nowUs,
                        ActionType = "StringDown",
                        ExpectedPage = act.RequiredPage,
                        StatePage = state.CurrentPage,
                        ExpectedSlot = act.RequiredSlot,
                        StateSlot = state.CurrentSlot,
                        MidiNote = act.NoteNumber,
                        GuitarString = act.Value,
                        IsSkipped = false,
                        SkipReason = (activeMode == 1) ? "FretLagWarning" : "ChordLagWarning"
                    });
                }

                if (UseVirtualGamepad) _gamepad.SetStringState(act.Value, true);
                else KeyboardSender.SendBatch(new[] { new KeyAction(act.TimeUs, act.ScanCode, isDown: true) });
                state.HeldStringKeys.Add(act.Value);
                report.PlayedSoundNotes++;

                int digitKey = GameScanCodes.GetGameDigit(act.Value);
                int fret = act.NoteNumber > 0
                    ? Math.Max(0, act.NoteNumber - GuitarTuning.Standard.GetOpenMidiNote(act.Value))
                    : (activeMode == 1 && state.CurrentPage > 0 ? (state.CurrentPage - 1) * 6 + Math.Max(1, (int)state.CurrentSlot) : 0);
                OnSimulationAction?.Invoke(new Bridge.GuitarSimulationActionDto(
                    Mode: activeMode == 1 ? "fret" : "chord",
                    CurrentPage: $"P{state.CurrentPage}",
                    CurrentSlot: state.CurrentSlot > 0 ? state.CurrentSlot : 1,
                    ActionType: "pluck",
                    StringNo: act.Value,
                    Fret: fret,
                    ChordName: activeMode == 2 ? ResolveSlotLabel(activeMode, state.CurrentPage, state.CurrentSlot) : null,
                    GameKey: digitKey,
                    Message: string.Empty,
                    TimestampMs: nowUs / 1000
                ));

                break;
            }

            case GuitarActionType.StringUp:
            {
                if (UseVirtualGamepad) _gamepad.SetStringState(act.Value, false);
                else KeyboardSender.SendBatch(new[] { new KeyAction(act.TimeUs, act.ScanCode, isDown: false) });
                state.HeldStringKeys.Remove(act.Value);
                break;
            }
        }

        return true;
    }

    private void ReleaseGuitarKeys(WheelPreset preset)
    {
        if (UseVirtualGamepad)
        {
            Span<(byte StringNo, bool IsDown)> strings =
            [
                (1, false), (2, false), (3, false),
                (4, false), (5, false), (6, false)
            ];
            _gamepad.SetStringsBatch(strings);
            _gamepad.SendPageNav(-1, false);
            _gamepad.SendPageNav(1, false);
            _gamepad.SendModeSwitch(false);
            return;
        }
        var actions = new List<KeyAction>(8);
        foreach (var sc in preset.StringScanCodes)
        {
            actions.Add(new KeyAction(0, sc, isDown: false));
        }
        actions.Add(new KeyAction(0, preset.PagePrevScanCode, isDown: false));
        actions.Add(new KeyAction(0, preset.PageNextScanCode, isDown: false));
        actions.Add(new KeyAction(0, GameScanCodes.KeyZ, isDown: false));
        KeyboardSender.SendBatch(actions);
    }
}
