using Nte.GuitarChart.Core;
using System.Collections.Concurrent;
using System.Threading;
using MidiAutoPlayer.SemiAuto.Input;
using GuitarChartModel = Nte.GuitarChart.Core.GuitarChart;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Preview;
using MidiAutoPlayer.Guitar;
using MidiAutoPlayer.SemiAuto.Modes;

namespace Nte.GuitarChart.WinForms;

public sealed class SemiAutoController : ISemiAutoAppService, IDisposable
{
    private readonly OverlayWindow _window;
    private readonly NativeInputObserver _inputObserver;
    private readonly Func<GuitarChartModel?> _chartProvider;
    private readonly Action<bool>? _clickThroughChanged;
    private readonly Func<GuitarPosition, int?, bool>? _autoPosition;
    private readonly AppConfig? _config;
    private readonly Action? _saveConfig;
    private readonly Func<string, object?>? _listGuitarSongs;
    private readonly Func<string, Task>? _selectGuitarSong;
    private readonly Func<string, Task<object?>>? _listRecommendedGuitarScores;
    private readonly Func<string, Task>? _selectRecommendedGuitarScore;
    private int _capturingLaneKey;
    private int _immersionHotkeyCaptureActive;
    private bool _manualPracticePosition;
    private BridgeController? _bridge;
    private PlayMode _currentMode = PlayMode.RhythmNormal;
    private PositionExecutionMode _executionMode = PositionExecutionMode.SemiAuto;
    private int _speedPercent = 100;
    private readonly SpeedControlManager _speedManager = new();
    private volatile bool _isPlaying;
    private CancellationTokenSource? _rhythmCountdown;
    private bool _isLocked;
    private readonly ImmersionSessionState _immersionState = new();
    private bool _immersionHotkeyRegistrationFailed;
    private string _currentTheme = "dark";
    private GuitarChartModel? _currentChart;
    private PracticeSessionController? _practiceSession;
    private readonly PracticeTimeline _practiceTimeline = new();
    private int _practiceDriverKey;
    private readonly RhythmSessionController _rhythmSession = new();
    private readonly MidiPreviewPlayer _simulatedMidiPlayer;
    private readonly ConcurrentDictionary<Guid, byte> _judgedNotes = new();
    private readonly ConcurrentDictionary<Guid, byte> _judgedSwitches = new();
    private readonly ConcurrentDictionary<int, long> _heldGameKeys = new();
    private readonly ConcurrentQueue<(GuitarPosition Position, int Generation)> _positionQueue = new();
    private readonly AutoResetEvent _positionSignal = new(false);
    private readonly HashSet<ushort> _autoHeldScanCodes = new();
    private readonly object _autoHeldLock = new();
    private Thread? _playbackThread;
    private Thread? _autoInputThread;
    private readonly Thread _positionThread;
    private volatile bool _positionThreadStop;
    private int _positionGeneration;
    private volatile bool _stopRequested;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private long _currentTimeUs;
    private long _lastPublishedTickMs;
    private bool? _lastPublishedPlaying;

    public SemiAutoController(OverlayWindow window, Func<GuitarChartModel?> chartProvider,
        Action<bool>? clickThroughChanged = null, Func<GuitarPosition, int?, bool>? autoPosition = null,
        AppConfig? config = null, Action? saveConfig = null,
        Func<string, object?>? listGuitarSongs = null, Func<string, Task>? selectGuitarSong = null,
        Func<string, Task<object?>>? listRecommendedGuitarScores = null, Func<string, Task>? selectRecommendedGuitarScore = null)
    {
        _window = window;
        _chartProvider = chartProvider;
        _clickThroughChanged = clickThroughChanged;
        _autoPosition = autoPosition;
        _config = config;
        _saveConfig = saveConfig;
        _listGuitarSongs = listGuitarSongs;
        _selectGuitarSong = selectGuitarSong;
        _listRecommendedGuitarScores = listRecommendedGuitarScores;
        _selectRecommendedGuitarScore = selectRecommendedGuitarScore;
        _simulatedMidiPlayer = new MidiPreviewPlayer();
        _inputObserver = new NativeInputObserver();
        if (_config != null && GameLaneKeyBinding.NormalizeConfig(_config)) _saveConfig?.Invoke();
        if (_config?.SemiAutoGameLaneKeyBindings is { Count: 6 } laneBindings)
            _inputObserver.SetGameLaneBindings(laneBindings);
        int configuredPositionKey = config?.SemiAutoPositionSwitchVk ?? PositionSwitchKeyBinding.DefaultVirtualKey;
        int configuredScanCode = config?.SemiAutoPositionSwitchScanCode ?? 0;
        bool configuredAllowed = IsAllowedPositionKey(configuredPositionKey, configuredScanCode);
        int fallbackPositionKey = PositionSwitchKeyBinding.DefaultVirtualKey;
        _inputObserver.PositionSwitchVk = configuredAllowed ? configuredPositionKey : fallbackPositionKey;
        _inputObserver.PositionSwitchScanCode = configuredAllowed ? configuredScanCode : 0x24;
        if (_config != null && (!configuredAllowed || _config.SemiAutoPositionSwitchVk != _inputObserver.PositionSwitchVk || _config.SemiAutoPositionSwitchScanCode != _inputObserver.PositionSwitchScanCode))
        {
            _config.SemiAutoPositionSwitchVk = _inputObserver.PositionSwitchVk;
            _config.SemiAutoPositionSwitchScanCode = _inputObserver.PositionSwitchScanCode;
            _saveConfig?.Invoke();
        }
        int immersionVk = _config?.SemiAutoImmersionHotkeyVk ?? ImmersionHotkeyBinding.DefaultVirtualKey;
        if (!IsAllowedImmersionHotkey(immersionVk)) immersionVk = FindAllowedImmersionHotkey();
        if (_config != null && _config.SemiAutoImmersionHotkeyVk != immersionVk)
        {
            _config.SemiAutoImmersionHotkeyVk = immersionVk;
            _saveConfig?.Invoke();
        }
        _window.ImmersionHotkeyPressed += ToggleImmersion;
        _immersionHotkeyRegistrationFailed = !_window.TrySetImmersionHotkey(immersionVk);
        _inputObserver.InputObserved += OnInputObserved;
        _inputObserver.PositionSwitchKeyPressed += OnPositionSwitchKeyPressed;
        _inputObserver.LaneKeyCaptured += OnLaneKeyCaptured;
        _inputObserver.KeyStateChanged += OnGlobalKeyStateChanged;
        _inputObserver.ImmersionHotkeyCaptured += OnImmersionHotkeyCaptured;
        _positionThread = new Thread(PositionExecutionLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "NTE_PositionExecution"
        };
        _positionThread.Start();
    }

    public void AttachBridge(BridgeController bridge) => _bridge = bridge;
    public bool IsPlaying => _isPlaying;
    public Task TogglePlaybackAsync()
    {
        if (_isPlaying) return PauseAsync();

        bool requiresStartPage = _currentMode == PlayMode.AutoShowcase
            || (_currentMode != PlayMode.Simulated && _executionMode == PositionExecutionMode.SemiAuto);
        if (requiresStartPage)
        {
            if (_immersionState.IsEnabled)
            {
                _immersionState.ToggleManually();
                SynchronizeImmersionState();
            }
            _bridge?.Publish(new { type = "playback.start-page.request", payload = new { } });
            return Task.CompletedTask;
        }

        return StartAsync();
    }

    public void LoadChart(GuitarChartModel chart)
    {
        StopPlayback();
        _currentChart = chart;
        var chartNotes = chart.Notes.Select(n => new MidiAutoPlayer.Models.RawMidiNote
        {
            StartUs = n.StartUs, DurationUs = n.DurationUs, NoteNumber = n.NoteNumber,
            Velocity = n.Velocity, Channel = n.Channel, Program = n.Program, TrackId = n.TrackIndex
        }).ToList();
        _speedManager.ApplyAutoSpeedOnMidiImport(MidiSpeedOptimizer.CalculateAutoSpeedPercent(chartNotes));
        _speedPercent = _speedManager.EffectiveSpeedPercent;
        _currentTimeUs = 0;
        _practiceSession = null;
        _practiceTimeline.Pause(MonotonicTimeUs());
        _practiceTimeline.Seek(0, MonotonicTimeUs());
        _practiceDriverKey = 0;
        _judgedNotes.Clear();
        _judgedSwitches.Clear();
        ResetEngines();
        _bridge?.Publish(new { type = "speed.state", payload = new { speedPercent = _speedPercent, isAutoSpeed = true } });
        PublishPlaybackTick("琴谱已就绪", false);
        PublishChart();
    }

    public void ClearChart(string reason)
    {
        StopPlayback();
        _currentChart = null;
        _practiceSession = null;
        _currentTimeUs = 0;
        _practiceTimeline.Pause(MonotonicTimeUs());
        _practiceTimeline.Seek(0, MonotonicTimeUs());
        _practiceDriverKey = 0;
        _judgedNotes.Clear();
        _judgedSwitches.Clear();
        ResetEngines();
        PublishPlaybackTick(reason, false);
        _bridge?.Publish(new { type = "chart.clear", payload = new { message = reason } });
    }

    private void StopPlayback()
    {
        if (_currentMode == PlayMode.Practice)
        {
            long nowUs = MonotonicTimeUs();
            _practiceTimeline.Pause(nowUs);
            _currentTimeUs = _practiceTimeline.CurrentTimeUs(nowUs);
            _practiceDriverKey = 0;
        }
        CancelRhythmCountdown();
        int cancelledLaneCapture = _capturingLaneKey;
        _capturingLaneKey = 0;
        _isPlaying = false;
        _stopRequested = true;
        Interlocked.Increment(ref _positionGeneration);
        while (_positionQueue.TryDequeue(out _)) { }
        _pauseEvent.Set();
        _rhythmSession.Stop();
        _simulatedMidiPlayer.Stop();
        _inputObserver.Stop();
        if (cancelledLaneCapture != 0)
            _bridge?.Publish(new { type = "lane-key.capture.state", payload = new { active = false, lane = cancelledLaneCapture } });
        foreach (var key in _heldGameKeys.Keys)
            _bridge?.Publish(new { type = "input.feedback", payload = new { lane = key, pressed = false } });
        _heldGameKeys.Clear();
        _window.SetClickThrough(false);
        EndAutomaticImmersion();
        _clickThroughChanged?.Invoke(false);
        var thread = _playbackThread;
        if (thread is { IsAlive: true } && thread != Thread.CurrentThread) thread.Join(500);
        var autoThread = _autoInputThread;
        if (autoThread is { IsAlive: true } && autoThread != Thread.CurrentThread) autoThread.Join(500);
        ReleaseShowcaseKeys();
        if (thread is not { IsAlive: true }) _playbackThread = null;
        if (autoThread is not { IsAlive: true }) _autoInputThread = null;
    }

    private void CancelRhythmCountdown()
    {
        var countdown = _rhythmCountdown;
        _rhythmCountdown = null;
        if (countdown != null && !countdown.IsCancellationRequested) countdown.Cancel();
        _bridge?.Publish(new { type = "playback.countdown", payload = new { active = false, seconds = 0 } });
    }

    private void PublishChart()
    {
        if (_currentChart != null) _bridge?.Publish(new { type = "chart.render", payload = new { chart = _currentChart } });
    }

    private void ResetEngines()
    {
        _rhythmSession.Configure(_speedPercent);
    }

    public Task ChangeModeAsync(PlayMode mode)
    {
        if (_isPlaying || _rhythmCountdown != null) StopPlayback();
        _currentMode = mode;
        _practiceSession = null;
        _practiceTimeline.Pause(MonotonicTimeUs());
        _practiceTimeline.Seek(0, MonotonicTimeUs());
        _practiceDriverKey = 0;
        _judgedNotes.Clear();
        _judgedSwitches.Clear();
        ResetEngines();
        if (mode == PlayMode.Practice) PublishPracticeState("准备逐个练习目标");
        return Task.CompletedTask;
    }

    public Task SetExecutionModeAsync(PositionExecutionMode mode)
    {
        if (_isPlaying || _rhythmCountdown != null) StopPlayback();
        _executionMode = mode;
        _bridge?.Publish(new { type = "execution.mode.state", payload = new { mode = mode == PositionExecutionMode.FullManual ? "full-manual" : "semi-auto" } });
        if (_currentMode == PlayMode.Practice) PublishPracticeState("执行方式已切换，按开始继续");
        return Task.CompletedTask;
    }

    public Task StartAsync(int? initialPage = null)
    {
        if (Volatile.Read(ref _immersionHotkeyCaptureActive) != 0)
            CancelImmersionHotkeyCaptureAsync();
        if (initialPage is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(initialPage), "起始页必须为 P1～P5。");
        if (_isPlaying) return Task.CompletedTask;
        bool requiresStartPage = _currentMode == PlayMode.AutoShowcase
            || (_currentMode != PlayMode.Simulated && _executionMode == PositionExecutionMode.SemiAuto);
        if (requiresStartPage && !initialPage.HasValue)
        {
            Toast("error", "开始前请选择游戏当前所在页（P1～P5）。");
            return Task.CompletedTask;
        }
        if (_currentChart == null && _chartProvider() is { } loaded) LoadChart(loaded);
        if (_currentChart == null)
        {
            Toast("warning", "尚未载入有效琴谱，请先选择歌曲。");
            return Task.CompletedTask;
        }

        if (_currentMode == PlayMode.Practice) return StartPractice(initialPage);
        return StartRhythm(initialPage);
    }

    private Task StartPractice(int? initialPage, bool skipInitialPosition = false)
    {
        var chart = _currentChart!;
        if (_practiceSession == null || _practiceSession.IsCompleted)
        {
            _practiceSession = new PracticeSessionController(chart);
            _judgedNotes.Clear();
            _currentTimeUs = _practiceSession.Current?.StartUs ?? 0;
        }
        var target = _practiceSession.GetUpcomingTargets(_currentTimeUs).FirstOrDefault() ?? _practiceSession.Current;
        if (target == null)
        {
            PublishPracticeState("本段完成");
            PublishPlaybackTick("本段完成", false);
            return Task.CompletedTask;
        }
        if (!skipInitialPosition && target?.Position is { } position && !PositionTo(position, initialPage)) return Task.CompletedTask;
        _window.SetClickThrough(false);
        _clickThroughChanged?.Invoke(false);
        if (!_inputObserver.Start()) { Toast("error", $"全局键盘监听启动失败 (Win32 {_lastError()})，无法开始练习。"); return Task.CompletedTask; }
        _isPlaying = true;
        _stopRequested = false;
        _practiceDriverKey = 0;
        long nowUs = MonotonicTimeUs();
        _practiceTimeline.Pause(nowUs);
        _practiceTimeline.Seek(_currentTimeUs, nowUs);
        PublishPracticeState("等待按下当前目标键");
        PublishPlaybackTick("练习已就绪，等待按键", false);
        _playbackThread = new Thread(PracticeTimelineLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "NTE_PracticeTimeline"
        };
        _playbackThread.Start();
        StartAutomaticImmersion();
        return Task.CompletedTask;
    }

    private async Task StartRhythm(int? initialPage, bool skipInitialPosition = false)
    {
        if (_currentMode == PlayMode.Simulated && !_simulatedMidiPlayer.IsAvailable)
            Toast("warning", "系统 MIDI 输出设备不可用：谱面和按键判定仍会继续，但无法输出声音。");
        if (_currentMode == PlayMode.AutoShowcase && (_config == null || _config.TargetWindowHandle == IntPtr.Zero || !KeyboardSender.IsForeground(_config.TargetWindowHandle)))
        {
            Toast("warning", "锁屏可打需要先绑定游戏窗口，并将游戏切到前台。自动演奏会向该窗口发送按键。");
            return;
        }
        if (_currentMode == PlayMode.AutoShowcase && _currentChart!.UnplayableNoteCount > 0)
            Toast("warning", $"当前曲谱有 {_currentChart.UnplayableNoteCount} 个音符无法共用同一轮盘位置，锁屏可打会跳过这些音符。");
        if (_rhythmCountdown != null) return;
        bool resume = _currentTimeUs > 0 && _currentTimeUs < _currentChart!.DurationUs;
        if (!resume) { _currentTimeUs = 0; _judgedNotes.Clear(); _judgedSwitches.Clear(); ResetEngines(); }
        if (!skipInitialPosition)
        {
            if (_currentMode == PlayMode.Simulated)
            {
                // MIDI follow mode plays the complete chart without controlling game position.
            }
            else if (_executionMode == PositionExecutionMode.SemiAuto)
            {
                var position = _currentChart!.Notes.FirstOrDefault(n => n.StartUs >= _currentTimeUs && n.Position.HasValue)?.Position;
                if (position.HasValue && !PositionTo(position.Value, initialPage)) return;
            }
            else
            {
                var position = _currentChart!.Notes.FirstOrDefault(n => n.StartUs >= _currentTimeUs && n.Position.HasValue)?.Position;
                if (position.HasValue && !PositionTo(position.Value, _currentMode == PlayMode.AutoShowcase ? initialPage : null) && _currentMode == PlayMode.AutoShowcase) return;
            }
        }
        _window.SetClickThrough(false);
        _clickThroughChanged?.Invoke(false);
        if (_currentMode is PlayMode.RhythmNormal or PlayMode.RhythmHell or PlayMode.Simulated)
        {
            var countdown = new CancellationTokenSource();
            _rhythmCountdown = countdown;
            try
            {
                for (int seconds = 3; seconds > 0; seconds--)
                {
                    _bridge?.Publish(new { type = "playback.countdown", payload = new { active = true, seconds } });
                    await Task.Delay(1000, countdown.Token);
                }
            }
            catch (OperationCanceledException) when (countdown.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                bool ownsCountdown = ReferenceEquals(_rhythmCountdown, countdown);
                if (ownsCountdown) _rhythmCountdown = null;
                countdown.Dispose();
                if (ownsCountdown)
                    _bridge?.Publish(new { type = "playback.countdown", payload = new { active = false, seconds = 0 } });
            }
        }
        _isPlaying = true;
        _stopRequested = false;
        _pauseEvent.Set();
        _rhythmSession.Start(_currentTimeUs);
        if (_currentMode != PlayMode.AutoShowcase && !_inputObserver.Start())
        {
            _isPlaying = false;
            _rhythmSession.Stop();
            Toast("error", $"全局键盘监听启动失败 (Win32 {_lastError()})，无法开始音游。");
            return;
        }
        if (_currentMode == PlayMode.Simulated) StartSimulatedMidiPlayback(_currentTimeUs);
        if (_currentMode == PlayMode.AutoShowcase)
        {
            _autoInputThread = new Thread(AutoShowcaseInputLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "NTE_AutoShowcaseInput"
            };
            _autoInputThread.Start();
        }
        _playbackThread = new Thread(RhythmPlaybackLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "NTE_RhythmPlayback" };
        _playbackThread.Start();
        StartAutomaticImmersion(force: _currentMode == PlayMode.AutoShowcase);
    }

    private bool PositionTo(GuitarPosition position, int? initialPage = null)
    {
        if (_executionMode == PositionExecutionMode.FullManual && _currentMode != PlayMode.AutoShowcase)
        {
            _bridge?.Publish(new { type = "position.prompt", payload = new { targetPage = position.Page, targetSlot = position.Slot, fret = position.Fret, stateText = $"请手动切换至 P{position.Page} · Slot {position.Slot} · {position.Fret}品", cueTimeUs = _currentTimeUs } });
            return true;
        }
        if (_autoPosition == null) return true;
        try
        {
            if (_autoPosition(position, initialPage))
            {
                _bridge?.Publish(new { type = "position.active", payload = new { page = position.Page, slot = position.Slot, stateText = $"已定位至 P{position.Page} · Slot {position.Slot}" } });
                return true;
            }
            Toast("warning", $"无法定位到 P{position.Page} · Slot {position.Slot}，请绑定游戏窗口并确认游戏处于前台。");
            return false;
        }
        catch (Exception ex) { Toast("error", $"位置切换失败：{ex.Message}"); return false; }
    }

    public Task PauseAsync()
    {
        if (!_isPlaying)
        {
            if (_rhythmCountdown != null)
            {
                StopPlayback();
                PublishPlaybackTick("已暂停", false);
            }
            return Task.CompletedTask;
        }
        if (_currentMode == PlayMode.Practice)
        {
            StopPlayback();
            PublishPracticeState("已暂停，当前时间保留");
            PublishPlaybackTick("已暂停", false);
            return Task.CompletedTask;
        }
        StopPlayback();
        PublishPlaybackTick("已暂停", false);
        return Task.CompletedTask;
    }

    public Task ResetAsync()
    {
        StopPlayback();
        _currentTimeUs = 0;
        _practiceTimeline.Seek(0, MonotonicTimeUs());
        _practiceDriverKey = 0;
        _practiceSession = null;
        _judgedNotes.Clear();
        _judgedSwitches.Clear();
        ResetEngines();
        PublishPlaybackTick("已重置", false);
        if (_currentMode == PlayMode.Practice) PublishPracticeState("练习已重置");
        return Task.CompletedTask;
    }

    public Task SeekAsync(double ratio)
    {
        var chart = _currentChart;
        if (chart == null || chart.DurationUs <= 0) return Task.CompletedTask;

        ratio = Math.Clamp(ratio, 0d, 1d);
        bool resume = _isPlaying;
        StopPlayback();
        _judgedNotes.Clear();
        _judgedSwitches.Clear();

        if (_currentMode == PlayMode.Practice)
        {
            _practiceSession = new PracticeSessionController(chart);
            _currentTimeUs = (long)(chart.DurationUs * ratio);
            _practiceTimeline.Seek(_currentTimeUs, MonotonicTimeUs());
            var practicePosition = _practiceSession.GetUpcomingTargets(_currentTimeUs).FirstOrDefault()?.Position;
            if (!resume && practicePosition is { } targetPosition) PositionTo(targetPosition);
            PublishPracticeState(_practiceSession.GetUpcomingTargets(_currentTimeUs).Count == 0 ? "本段完成" : "已定位，按开始后继续练习");
        }
        else
        {
            _currentTimeUs = (long)(chart.DurationUs * ratio);
            ResetEngines();
            if (_executionMode == PositionExecutionMode.SemiAuto)
            {
                var position = chart.Notes.FirstOrDefault(n => n.StartUs >= _currentTimeUs && n.Position.HasValue)?.Position;
                if (position.HasValue) PositionTo(position.Value);
            }
            else
            {
                var position = chart.Notes.FirstOrDefault(n => n.StartUs >= _currentTimeUs && n.Position.HasValue)?.Position;
                if (position.HasValue) PositionTo(position.Value);
            }
        }

        PublishPlaybackTick("已定位", false);
        if (resume)
        {
            if (_currentMode == PlayMode.Practice)
            {
                var startTask = StartPractice(initialPage: null, skipInitialPosition: true);
                if (_practiceSession?.GetUpcomingTargets(_currentTimeUs).FirstOrDefault()?.Position is { } practicePosition)
                    QueuePositionTo(practicePosition);
                return startTask;
            }
            return StartRhythm(initialPage: null, skipInitialPosition: true);
        }
        return Task.CompletedTask;
    }

    public Task SetSpeedAsync(int speedPercent)
    {
        long nowUs = MonotonicTimeUs();
        if (_isPlaying)
            _currentTimeUs = _currentMode == PlayMode.Practice
                ? _practiceTimeline.CurrentTimeUs(nowUs)
                : _rhythmSession.CurrentTimeUs(_speedPercent);
        _speedPercent = Math.Clamp(speedPercent, 25, 200);
        _speedManager.SetUserManualSpeed(_speedPercent);
        _rhythmSession.SetSpeedRatio(_speedPercent / 100d);
        if (_currentMode == PlayMode.Practice) _practiceTimeline.SetSpeed(_speedPercent, nowUs);
        if (_isPlaying && _currentMode != PlayMode.Practice)
        {
            _rhythmSession.Start(_currentTimeUs);
            if (_currentMode == PlayMode.Simulated) StartSimulatedMidiPlayback(_currentTimeUs);
        }
        return Task.CompletedTask;
    }

    public Task BeginPositionSwitchKeyCaptureAsync()
    {
        return BeginLaneKeyCaptureAsync(7);
    }

    public Task BeginLaneKeyCaptureAsync(int lane)
    {
        if (lane is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(lane), "轨道编号必须为 1～7。");
        if (_isPlaying || _rhythmCountdown != null)
        {
            _bridge?.Publish(new { type = "lane-key.error", payload = new { message = "请先暂停演奏，再设置键位。" } });
            return Task.CompletedTask;
        }
        if (!_inputObserver.BeginLaneKeyCapture(lane)) return Task.CompletedTask;
        _capturingLaneKey = lane;
        if (!_inputObserver.Start())
        {
            _capturingLaneKey = 0;
            _inputObserver.CancelLaneKeyCapture();
            _bridge?.Publish(new { type = "lane-key.error", payload = new { message = $"无法启动全局键盘监听 (Win32 {_lastError()})。" } });
            return Task.CompletedTask;
        }
        _bridge?.Publish(new { type = "lane-key.capture.state", payload = new { active = true, lane } });
        if (lane == 7)
            _bridge?.Publish(new { type = "position-key.capture", payload = new { active = true, virtualKey = _inputObserver.PositionSwitchVk, label = FormatVirtualKey(_inputObserver.PositionSwitchVk, _inputObserver.PositionSwitchScanCode) } });
        return Task.CompletedTask;
    }

    public Task CancelLaneKeyCaptureAsync()
    {
        int lane = _capturingLaneKey;
        if (lane == 0) return Task.CompletedTask;
        _capturingLaneKey = 0;
        _inputObserver.CancelLaneKeyCapture();
        if (!_isPlaying) _inputObserver.Stop();
        _bridge?.Publish(new { type = "lane-key.capture.state", payload = new { active = false, lane } });
        return Task.CompletedTask;
    }

    public Task ResetLaneKeyBindingsAsync()
    {
        if (_isPlaying || _rhythmCountdown != null)
        {
            _bridge?.Publish(new { type = "lane-key.error", payload = new { message = "请先暂停演奏，再恢复默认键位。" } });
            return Task.CompletedTask;
        }
        var defaults = GameLaneKeyBinding.CreateDefaultGameLanes();
        if (_config != null) _config.SemiAutoGameLaneKeyBindings = defaults.Select(k => new PhysicalKeyBinding(k.VirtualKey, k.ScanCode)).ToList();
        _inputObserver.SetGameLaneBindings(defaults);
        _inputObserver.PositionSwitchVk = PositionSwitchKeyBinding.DefaultVirtualKey;
        _inputObserver.PositionSwitchScanCode = 0x24;
        if (_config != null)
        {
            _config.SemiAutoPositionSwitchVk = _inputObserver.PositionSwitchVk;
            _config.SemiAutoPositionSwitchScanCode = _inputObserver.PositionSwitchScanCode;
            _saveConfig?.Invoke();
        }
        _inputObserver.CancelLaneKeyCapture();
        _capturingLaneKey = 0;
        if (!_isPlaying) _inputObserver.Stop();
        PublishLaneKeyState();
        var label = FormatVirtualKey(_inputObserver.PositionSwitchVk, _inputObserver.PositionSwitchScanCode);
        _bridge?.Publish(new { type = "position-key.bound", payload = new { virtualKey = _inputObserver.PositionSwitchVk, scanCode = _inputObserver.PositionSwitchScanCode, label } });
        _bridge?.Publish(new { type = "lane-key.capture.state", payload = new { active = false, lane = 0 } });
        return Task.CompletedTask;
    }

    public Task ResetPositionSwitchKeyAsync()
    {
        SetPositionSwitchKey(PositionSwitchKeyBinding.DefaultVirtualKey, 0x24);
        return Task.CompletedTask;
    }

    public Task SetOverlayOpacityAsync(int percent)
    {
        var applied = _window.SetOverlayOpacity(percent);
        if (_config != null) { _config.SemiAutoOverlayOpacity = applied; _saveConfig?.Invoke(); }
        _bridge?.Publish(new { type = "window.opacity", payload = new { percent = applied } });
        return Task.CompletedTask;
    }

    public Task SetOverlayWideLayoutAsync(bool wide)
    {
        _window.SetWideLayout(wide);
        if (_config != null)
        {
            _config.SemiAutoOverlayWideMode = wide;
            _saveConfig?.Invoke();
        }
        _bridge?.Publish(new { type = "window.layout", payload = new { layout = wide ? "wide" : "portrait" } });
        return Task.CompletedTask;
    }

    public Task SetAutoImmersionAsync(bool enabled)
    {
        if (_config != null)
        {
            _config.SemiAutoAutoImmersion = enabled;
            _saveConfig?.Invoke();
        }
        PublishImmersionState();
        return Task.CompletedTask;
    }

    public Task BeginImmersionHotkeyCaptureAsync()
    {
        if (_isPlaying || _rhythmCountdown != null || _capturingLaneKey != 0)
        {
            _bridge?.Publish(new { type = "immersion.hotkey.error", payload = new { message = "请先暂停演奏并退出其他按键设置，再修改沉浸快捷键。" } });
            return Task.CompletedTask;
        }

        Interlocked.Exchange(ref _immersionHotkeyCaptureActive, 1);
        _inputObserver.BeginImmersionHotkeyCapture();
        if (!_inputObserver.Start())
        {
            Interlocked.Exchange(ref _immersionHotkeyCaptureActive, 0);
            _inputObserver.CancelImmersionHotkeyCapture();
            _bridge?.Publish(new { type = "immersion.hotkey.error", payload = new { message = $"无法启动全局键盘监听 (Win32 {_lastError()})，快捷键未更改。" } });
            return Task.CompletedTask;
        }

        _bridge?.Publish(new { type = "immersion.hotkey.capture.state", payload = new { active = true, message = "请按一个按键；Esc 取消" } });
        return Task.CompletedTask;
    }

    public Task CancelImmersionHotkeyCaptureAsync()
    {
        Interlocked.Exchange(ref _immersionHotkeyCaptureActive, 0);
        _inputObserver.CancelImmersionHotkeyCapture();
        if (!_isPlaying && _capturingLaneKey == 0) _inputObserver.Stop();
        _bridge?.Publish(new { type = "immersion.hotkey.capture.state", payload = new { active = false, cancelled = true } });
        return Task.CompletedTask;
    }

    private void OnImmersionHotkeyCaptured(int virtualKey, int scanCode, bool hasModifiers)
    {
        if (Volatile.Read(ref _immersionHotkeyCaptureActive) == 0) return;
        _ = CompleteImmersionHotkeyCaptureAsync(virtualKey, hasModifiers);
    }

    private async Task CompleteImmersionHotkeyCaptureAsync(int virtualKey, bool hasModifiers)
    {
        if (virtualKey == 0x1B)
        {
            await CancelImmersionHotkeyCaptureAsync();
            return;
        }

        if (hasModifiers)
        {
            if (Volatile.Read(ref _immersionHotkeyCaptureActive) == 0) return;
            _inputObserver.BeginImmersionHotkeyCapture();
            _bridge?.Publish(new { type = "immersion.hotkey.capture.state", payload = new { active = true, message = "只支持单个按键，不支持组合键；请重试，Esc 取消", error = true } });
            return;
        }

        if (Interlocked.Exchange(ref _immersionHotkeyCaptureActive, 0) == 0) return;
        _bridge?.Publish(new { type = "immersion.hotkey.capture.state", payload = new { active = false } });
        try
        {
            await SetImmersionHotkeyAsync(virtualKey);
        }
        catch (Exception ex)
        {
            _bridge?.Publish(new { type = "immersion.hotkey.error", payload = new { message = $"快捷键设置失败：{ex.Message}" } });
        }
        finally
        {
            if (!_isPlaying && _capturingLaneKey == 0) _inputObserver.Stop();
        }
    }

    public Task SetImmersionHotkeyAsync(int virtualKey)
    {
        if (virtualKey == (_config?.SemiAutoImmersionHotkeyVk ?? _window.ImmersionHotkeyVk))
        {
            PublishImmersionState();
            return Task.CompletedTask;
        }
        if (!IsAllowedImmersionHotkey(virtualKey))
        {
            _bridge?.Publish(new { type = "immersion.hotkey.error", payload = new { message = "该键与全局快捷键、轨道键或换位键冲突，或该键被系统保留。" } });
            return Task.CompletedTask;
        }
        if (!_window.TrySetImmersionHotkey(virtualKey))
        {
            _bridge?.Publish(new { type = "immersion.hotkey.error", payload = new { message = "全局按键监听启动失败，快捷键未更改。" } });
            return Task.CompletedTask;
        }
        _immersionHotkeyRegistrationFailed = false;
        if (_config != null)
        {
            _config.SemiAutoImmersionHotkeyVk = virtualKey;
            _saveConfig?.Invoke();
        }
        PublishImmersionState();
        return Task.CompletedTask;
    }

    private bool IsAllowedImmersionHotkey(int virtualKey) => ImmersionHotkeyBinding.IsAllowed(
        virtualKey,
        _config?.GlobalHotkeyVk ?? 0x77,
        _inputObserver.GameLaneBindings,
        new PhysicalKeyBinding(_inputObserver.PositionSwitchVk, _inputObserver.PositionSwitchScanCode));

    private int FindAllowedImmersionHotkey()
    {
        for (int key = ImmersionHotkeyBinding.DefaultVirtualKey; key <= 0x5A; key++)
            if (IsAllowedImmersionHotkey(key)) return key;
        for (int key = 0x41; key < ImmersionHotkeyBinding.DefaultVirtualKey; key++)
            if (IsAllowedImmersionHotkey(key)) return key;
        return ImmersionHotkeyBinding.DefaultVirtualKey;
    }

    private void ToggleImmersion()
    {
        _immersionState.ToggleManually();
        SynchronizeImmersionState();
    }

    private void StartAutomaticImmersion(bool force = false)
    {
        if (!_immersionState.StartAutomatic(force || _config?.SemiAutoAutoImmersion == true)) return;
        SynchronizeImmersionState();
    }

    private void EndAutomaticImmersion()
    {
        if (!_immersionState.EndAutomatic()) return;
        SynchronizeImmersionState();
    }

    private void SynchronizeImmersionState()
    {
        _window.SetImmersive(_immersionState.IsEnabled);
        PublishImmersionState();
    }

    private void PublishImmersionState()
    {
        _bridge?.Publish(new
        {
            type = "immersion.state",
            payload = new
            {
                enabled = _immersionState.IsEnabled,
                hotkey = ImmersionHotkeyBinding.FormatLabel(_config?.SemiAutoImmersionHotkeyVk ?? _window.ImmersionHotkeyVk),
                auto = _config?.SemiAutoAutoImmersion ?? false
            }
        });
    }

    public Task SetManualPracticePositionAsync(bool enabled)
    {
        _manualPracticePosition = enabled;
        _bridge?.Publish(new { type = "position.manual-practice.state", payload = new { enabled } });
        return Task.CompletedTask;
    }

    public Task ListGuitarSongsAsync(string query)
    {
        var result = _listGuitarSongs?.Invoke(query) ?? new { items = Array.Empty<object>(), counts = new { guitar = 0 } };
        _bridge?.Publish(new { type = "library.guitar.list", payload = new { query, data = result } });
        return Task.CompletedTask;
    }

    public Task SelectGuitarSongAsync(string songId)
    {
        if (string.IsNullOrWhiteSpace(songId)) throw new ArgumentException("请选择曲库中的吉他曲目。", nameof(songId));
        if (_selectGuitarSong == null) throw new InvalidOperationException("当前悬浮窗没有连接到吉他曲库。");
        _bridge?.Publish(new { type = "library.guitar.loading", payload = new { songId } });
        return _selectGuitarSong(songId);
    }

    public async Task ListRecommendedGuitarScoresAsync(string query)
    {
        object? result = await (_listRecommendedGuitarScores?.Invoke(query) ?? Task.FromResult<object?>(new { status = "unavailable", offline = false, items = Array.Empty<object>() }));
        _bridge?.Publish(new { type = "library.recommended.list", payload = new { query, data = result } });
    }

    public Task SelectRecommendedGuitarScoreAsync(string scoreId)
    {
        if (string.IsNullOrWhiteSpace(scoreId)) throw new ArgumentException("请选择推荐谱。", nameof(scoreId));
        if (_selectRecommendedGuitarScore == null) throw new InvalidOperationException("推荐谱服务暂不可用。");
        _bridge?.Publish(new { type = "library.guitar.loading", payload = new { scoreId } });
        return _selectRecommendedGuitarScore(scoreId);
    }

    public Task BeginWindowDragAsync()
    {
        if (_isLocked)
        {
            _bridge?.Publish(new { type = "window.drag.state", payload = new { dragging = false } });
            return Task.CompletedTask;
        }

        _bridge?.Publish(new { type = "window.drag.state", payload = new { dragging = true } });
        try { _window.BeginNativeDrag(locked: false); }
        finally { _bridge?.Publish(new { type = "window.drag.state", payload = new { dragging = false } }); }
        return Task.CompletedTask;
    }

    private void OnLaneKeyCaptured(int lane, int virtualKey, int scanCode)
    {
        if (_capturingLaneKey != lane) return;
        if (virtualKey == 0x1B)
        {
            _capturingLaneKey = 0;
            _inputObserver.CancelLaneKeyCapture();
            if (!_isPlaying) _inputObserver.Stop();
            _bridge?.Publish(new { type = "lane-key.capture.state", payload = new { active = false, lane, cancelled = true } });
            return;
        }

        if (!GameLaneKeyBinding.IsAllowedPhysicalKey(virtualKey, scanCode, _config?.GlobalHotkeyVk ?? 0x77)
            || virtualKey == (_config?.SemiAutoImmersionHotkeyVk ?? ImmersionHotkeyBinding.DefaultVirtualKey)
            || (lane == 7 && !IsAllowedPositionKey(virtualKey, scanCode)))
        {
            _bridge?.Publish(new { type = "lane-key.error", payload = new { message = "该键为系统、快捷键或轨道控制保留键，请按其他键。" } });
            return;
        }

        var current = _inputObserver.GameLaneBindings.Select(k => new PhysicalKeyBinding(k.VirtualKey, k.ScanCode)).ToList();
        var position = new PhysicalKeyBinding(_inputObserver.PositionSwitchVk, _inputObserver.PositionSwitchScanCode);
        var candidate = new PhysicalKeyBinding(virtualKey, scanCode);
        if (lane == 7) position = candidate;
        else current[lane - 1] = candidate;
        if (!GameLaneKeyBinding.AreUnique(current.Append(position).ToArray()))
        {
            _bridge?.Publish(new { type = "lane-key.error", payload = new { message = "这个键已分配给其他轨道，请选择不同按键。" } });
            return;
        }

        _capturingLaneKey = 0;
        _inputObserver.CancelLaneKeyCapture();
        if (!_isPlaying) _inputObserver.Stop();
        if (lane == 7)
        {
            SetPositionSwitchKey(virtualKey, scanCode);
        }
        else
        {
            _inputObserver.SetGameLaneBindings(current);
            if (_config != null)
            {
                _config.SemiAutoGameLaneKeyBindings = current;
                _saveConfig?.Invoke();
            }
            PublishLaneKeyState();
            Toast("success", $"第 {lane} 轨已设置为 {GameLaneKeyBinding.FormatLabel(virtualKey, scanCode)}");
        }
        _bridge?.Publish(new { type = "lane-key.capture.state", payload = new { active = false, lane } });
    }

    private void SetPositionSwitchKey(int virtualKey, int scanCode)
    {
        var allKeys = _inputObserver.GameLaneBindings
            .Append(new PhysicalKeyBinding(virtualKey, scanCode)).ToArray();
        if (!IsAllowedPositionKey(virtualKey, scanCode)
            || virtualKey == (_config?.SemiAutoImmersionHotkeyVk ?? ImmersionHotkeyBinding.DefaultVirtualKey)
            || !GameLaneKeyBinding.AreUnique(allKeys))
        {
            _bridge?.Publish(new { type = "lane-key.error", payload = new { message = "该键与其他轨道或全局快捷键冲突。" } });
            return;
        }
        _inputObserver.PositionSwitchVk = virtualKey;
        _inputObserver.PositionSwitchScanCode = scanCode;
        if (_config != null) { _config.SemiAutoPositionSwitchVk = virtualKey; _config.SemiAutoPositionSwitchScanCode = scanCode; _saveConfig?.Invoke(); }
        var label = FormatVirtualKey(virtualKey, scanCode);
        _bridge?.Publish(new { type = "position-key.bound", payload = new { virtualKey, scanCode, label } });
        PublishLaneKeyState();
        Toast("success", $"换位键已设置为 {label}");
    }

    private bool IsAllowedPositionKey(int virtualKey, int scanCode) =>
        PositionSwitchKeyBinding.IsAllowedPhysicalKey(virtualKey, scanCode)
        && virtualKey != _config?.GlobalHotkeyVk
        && virtualKey != (_config?.SemiAutoImmersionHotkeyVk ?? ImmersionHotkeyBinding.DefaultVirtualKey);

    private static string FormatVirtualKey(int vk, int scanCode = 0) => GameLaneKeyBinding.FormatLabel(vk, scanCode);

    private void PublishLaneKeyState()
    {
        var labels = _inputObserver.GameLaneBindings
            .Select(key => GameLaneKeyBinding.FormatLabel(key.VirtualKey, key.ScanCode))
            .Append(GameLaneKeyBinding.FormatLabel(_inputObserver.PositionSwitchVk, _inputObserver.PositionSwitchScanCode))
            .ToArray();
        _bridge?.Publish(new { type = "lane-key.state", payload = new { keys = labels } });
    }

    private static int _lastError() => System.Runtime.InteropServices.Marshal.GetLastWin32Error();

    public Task ResynchronizeAsync()
    {
        return Task.CompletedTask;
    }
    public Task HideOverlayAsync() { _window.Hide(); return Task.CompletedTask; }
    public Task SetWindowLockedAsync(bool locked)
    {
        _isLocked = locked;
        _bridge?.Publish(new { type = "window.locked", payload = new { locked } });
        return Task.CompletedTask;
    }
    public Task SetThemeAsync(string theme)
    {
        _currentTheme = theme == "light" ? "light" : "dark";
        if (_config != null)
        {
            string configTheme = _currentTheme == "light" ? "Light" : "DarkGlass";
            if (!string.Equals(_config.ThemeName, configTheme, StringComparison.Ordinal))
            {
                _config.ThemeName = configTheme;
                _saveConfig?.Invoke();
            }
        }
        _bridge?.Publish(new { type = "theme.set", payload = new { theme = _currentTheme } });
        return Task.CompletedTask;
    }
    public Task WebReadyAsync()
    {
        SetThemeAsync(_currentTheme);
        var wheelSlots = _config?.WheelPreset?.Slots ?? Array.Empty<NormalizedPoint>();
        _bridge?.Publish(new { type = "wheel.layout", payload = new { slots = wheelSlots.Select(p => new { x = p.X, y = p.Y }).ToArray() } });
        _bridge?.Publish(new { type = "mode.state", payload = new { mode = _currentMode switch { PlayMode.Practice => "practice", PlayMode.RhythmHell => "rhythm-hell", PlayMode.AutoShowcase => "auto-showcase", PlayMode.Simulated => "simulated", _ => "rhythm-normal" } } });
        _bridge?.Publish(new { type = "speed.state", payload = new { speedPercent = _speedPercent } });
        _bridge?.Publish(new { type = "position.manual-practice.state", payload = new { enabled = _manualPracticePosition } });
        _bridge?.Publish(new { type = "execution.mode.state", payload = new { mode = _executionMode == PositionExecutionMode.FullManual ? "full-manual" : "semi-auto" } });
        if (_config != null) SetOverlayOpacityAsync(_config.SemiAutoOverlayOpacity);
        PublishImmersionState();
        if (_immersionHotkeyRegistrationFailed)
            _bridge?.Publish(new { type = "immersion.hotkey.error", payload = new { message = "全局按键监听启动失败，请重新设置快捷键。" } });
        PublishLaneKeyState();
        _bridge?.Publish(new { type = "window.layout", payload = new { layout = _window.IsWideLayout ? "wide" : "portrait" } });
        _bridge?.Publish(new { type = "position-key.bound", payload = new { virtualKey = _inputObserver.PositionSwitchVk, scanCode = _inputObserver.PositionSwitchScanCode, label = FormatVirtualKey(_inputObserver.PositionSwitchVk, _inputObserver.PositionSwitchScanCode) } });
        PublishChart();
        if (_currentMode == PlayMode.Practice) PublishPracticeState("准备逐个练习目标");
        return Task.CompletedTask;
    }

    private void RhythmPlaybackLoop()
    {
        var chart = _currentChart;
        if (chart == null) return;
        long durationUs = chart.DurationUs;
        long startOffsetUs = _currentTimeUs;
        int noteIndex = 0;
        while (noteIndex < chart.Notes.Count && chart.Notes[noteIndex].StartUs < startOffsetUs) noteIndex++;
        int eventIndex = 0;
        while (eventIndex < chart.PositionEvents.Count && chart.PositionEvents[eventIndex].TargetTimeUs < startOffsetUs) eventIndex++;
        int switchMissIndex = 0;
        while (switchMissIndex < chart.PositionEvents.Count && chart.PositionEvents[switchMissIndex].DeadlineTimeUs < startOffsetUs) switchMissIndex++;
        while (!_stopRequested && _currentTimeUs < durationUs + 500_000)
        {
            _pauseEvent.Wait();
            if (_stopRequested) break;
            _currentTimeUs = _rhythmSession.CurrentTimeUs(_speedPercent);
            if (_stopRequested) break;
            while (eventIndex < chart.PositionEvents.Count && chart.PositionEvents[eventIndex].PromptTimeUs <= _currentTimeUs)
            {
                var ev = chart.PositionEvents[eventIndex++];
                if (_currentMode != PlayMode.Simulated)
                {
                    if (_executionMode == PositionExecutionMode.SemiAuto || _currentMode == PlayMode.AutoShowcase)
                        QueuePositionTo(ev.To);
                    else
                        PositionTo(ev.To);
                    _bridge?.Publish(new { type = "position.prompt", payload = new { eventId = ev.Id, targetPage = ev.To.Page, targetSlot = ev.To.Slot, deadlineUs = ev.DeadlineTimeUs } });
                }
            }
            while (switchMissIndex < chart.PositionEvents.Count && chart.PositionEvents[switchMissIndex].DeadlineTimeUs < _currentTimeUs)
            {
                var expiredSwitch = chart.PositionEvents[switchMissIndex++];
                if (_executionMode != PositionExecutionMode.FullManual || _currentMode != PlayMode.RhythmHell || !_judgedSwitches.TryAdd(expiredSwitch.Id, 0)) continue;
            }
            long missWindowUs = 160_000L * _speedPercent / 100;
            while (_currentMode != PlayMode.AutoShowcase && noteIndex < chart.Notes.Count && chart.Notes[noteIndex].StartUs + missWindowUs < _currentTimeUs)
            {
                var expired = chart.Notes[noteIndex++];
                if (!_judgedNotes.TryAdd(expired.Id, 0)) continue;
                PublishNoteFeedback(expired, HitGrade.Miss);
            }
            while (noteIndex < chart.Notes.Count && chart.Notes[noteIndex].StartUs + chart.Notes[noteIndex].DurationUs <= _currentTimeUs) noteIndex++;
            PublishPlaybackTick("正在演奏", true);
            Thread.Sleep(16);
        }
        bool completed = !_stopRequested && _currentTimeUs >= durationUs;
        _isPlaying = false;
        _rhythmSession.Stop();
        EndAutomaticImmersion();
        _window.SetClickThrough(false);
        _clickThroughChanged?.Invoke(false);
        _inputObserver.Stop();
        ReleaseShowcaseKeys();
        if (completed) { _currentTimeUs = durationUs; PublishPlaybackTick("演奏已结束", false); }
    }

    private void QueuePositionTo(GuitarPosition position)
    {
        _positionQueue.Enqueue((position, Volatile.Read(ref _positionGeneration)));
        _positionSignal.Set();
    }

    private void PositionExecutionLoop()
    {
        while (!_positionThreadStop)
        {
            _positionSignal.WaitOne();
            if (_positionThreadStop) break;
            (GuitarPosition Position, int Generation)? latest = null;
            while (_positionQueue.TryDequeue(out var request)) latest = request;
            if (latest is not { } pending
                || pending.Generation != Volatile.Read(ref _positionGeneration)
                || !_isPlaying || _stopRequested)
                continue;
            PositionTo(pending.Position);
        }
    }

    private void AutoShowcaseInputLoop()
    {
        var chart = _currentChart;
        if (chart == null) return;
        var events = AutoShowcaseInputPlan.Create(chart, _config?.WheelPreset?.StringScanCodes, _inputObserver.GameLaneBindings);
        var notesById = chart.Notes.ToDictionary(note => note.Id);
        int index = 0;
        while (index < events.Count && events[index].TimeUs < _currentTimeUs) index++;
        long frequency = System.Diagnostics.Stopwatch.Frequency;
        while (!_stopRequested && index < events.Count)
        {
            _pauseEvent.Wait();
            if (_stopRequested) break;
            long nowUs = _rhythmSession.CurrentTimeUs(_speedPercent);
            long remainingUs = events[index].TimeUs - nowUs;
            if (remainingUs > 2_000)
            {
                Thread.Sleep((int)Math.Min(remainingUs / 1000 - 1, 10));
                continue;
            }
            if (remainingUs > 0)
            {
                long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + remainingUs * frequency / 1_000_000;
                while (!_stopRequested && _pauseEvent.IsSet && System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
                    Thread.SpinWait(32);
                continue;
            }

            var inputEvent = events[index++];
            if (inputEvent.IsDown)
            {
                bool alreadyHeld;
                lock (_autoHeldLock) alreadyHeld = _autoHeldScanCodes.Contains(inputEvent.ScanCode);
                if (alreadyHeld && !SendShowcaseKey(inputEvent with { IsDown = false })) { _stopRequested = true; break; }
            }
            if (!SendShowcaseKey(inputEvent)) { _stopRequested = true; break; }
            if (inputEvent.IsDown && _judgedNotes.TryAdd(inputEvent.NoteId, 0))
            {
                if (notesById.TryGetValue(inputEvent.NoteId, out var note))
                    PublishNoteFeedback(note, HitGrade.Perfect);
            }
        }
    }

    private void OnInputObserved(object? sender, InputObservation obs)
    {
        if (_currentMode == PlayMode.AutoShowcase) return;
        if (!obs.IsKeyDown)
        {
            _heldGameKeys.TryRemove(obs.GameKey, out _);
            if (_currentMode == PlayMode.Practice && _isPlaying && _practiceDriverKey == obs.GameKey)
            {
                _practiceTimeline.Pause(obs.WallClockUs);
                _currentTimeUs = _practiceTimeline.CurrentTimeUs(obs.WallClockUs);
                _practiceDriverKey = 0;
                EndAutomaticImmersion();
                PublishPracticeState("已松开，谱面暂停");
                PublishPlaybackTick("已松开，谱面暂停", false);
            }
            return;
        }
        if (!_isPlaying || obs.IsAutoRepeat) return;
        _heldGameKeys[obs.GameKey] = obs.WallClockUs;
        if (_currentMode == PlayMode.Practice) { HandlePracticeKey(obs); return; }
        HandleRhythmKey(obs);
    }

    private bool SendShowcaseKey(AutoShowcaseInputEvent inputEvent)
    {
        if (_stopRequested) return false;
        var target = _config?.TargetWindowHandle ?? IntPtr.Zero;
        if (target == IntPtr.Zero || !KeyboardSender.IsForeground(target))
        {
            Toast("warning", "游戏窗口已失去前台焦点，自动演奏已停止并释放按键。");
            return false;
        }
        if (!KeyboardSender.TrySendBatch(new[] { new KeyAction(0, inputEvent.ScanCode, inputEvent.IsDown) }))
        {
            Toast("error", "向游戏发送按键失败，自动演奏已停止并释放按键。");
            return false;
        }
        lock (_autoHeldLock)
        {
            if (inputEvent.IsDown) _autoHeldScanCodes.Add(inputEvent.ScanCode);
            else _autoHeldScanCodes.Remove(inputEvent.ScanCode);
        }
        _bridge?.Publish(new { type = "input.feedback", payload = new { lane = inputEvent.GameKey, pressed = inputEvent.IsDown } });
        return true;
    }

    private void ReleaseShowcaseKeys()
    {
        ushort[] held;
        lock (_autoHeldLock) { held = _autoHeldScanCodes.ToArray(); _autoHeldScanCodes.Clear(); }
        if (held.Length > 0)
            KeyboardSender.TrySendBatch(held.Select(scanCode => new KeyAction(0, scanCode, false)).ToArray());
        foreach (var scanCode in held)
        {
            int lane = _inputObserver.GameLaneBindings
                .Select((binding, index) => (binding, index))
                .Where(item => item.binding.ScanCode == scanCode)
                .Select(item => item.index + 1)
                .FirstOrDefault();
            if (lane != 0) _bridge?.Publish(new { type = "input.feedback", payload = new { lane, pressed = false } });
        }
    }

    private void HandlePracticeKey(InputObservation obs)
    {
        if (_practiceSession == null) return;
        var targetGroup = _practiceSession.GetTargetsAt(_practiceTimeline.CurrentTimeUs(obs.WallClockUs));
        if (!_practiceTimeline.IsRunning)
        {
            var upcoming = _practiceSession.GetUpcomingTargets(_currentTimeUs);
            if (upcoming.Any(n => n.GameKey == obs.GameKey))
            {
                _practiceDriverKey = obs.GameKey;
                _practiceTimeline.Resume(obs.WallClockUs);
                targetGroup = _practiceSession.GetTargetsAt(_currentTimeUs);
            }
        }

        long actualTimeUs = _practiceTimeline.CurrentTimeUs(obs.WallClockUs);
        var feedback = _practiceSession.EvaluateInput(obs.GameKey, actualTimeUs);
        if (feedback.Kind != "correct")
        {
            int expected = targetGroup.FirstOrDefault()?.GameKey ?? _practiceSession.GetUpcomingTargets(_currentTimeUs).FirstOrDefault()?.GameKey ?? 0;
            PublishPracticeState(feedback.Kind == "early" ? "当前时间没有待练音符" : $"按错了，当前需要 键 {expected}", false);
            return;
        }
        if (!_immersionState.IsOwnedByAutomaticPlayback) StartAutomaticImmersion();
        var target = feedback.TargetNoteId is Guid id ? _currentChart?.Notes.FirstOrDefault(n => n.Id == id) : null;
        if (target != null)
        {
            _judgedNotes.TryAdd(target.Id, 0);
            PublishNoteFeedback(target, HitGrade.Perfect);
            var nextPosition = _practiceSession.GetUpcomingTargets(actualTimeUs).FirstOrDefault()?.Position;
            if (nextPosition is { } next && target.Position is { } current
                && (next.Page != current.Page || next.Slot != current.Slot))
                QueuePositionTo(next);
        }
        _currentTimeUs = actualTimeUs;
        PublishPracticeState("正确", true);
    }

    private void HandleRhythmKey(InputObservation obs)
    {
        var actualTimeUs = _rhythmSession.ToTimelineTimeUs(obs.WallClockUs, _speedPercent);
        long inputWindowUs = 160_000L * _speedPercent / 100;
        var notes = _currentChart?.Notes;
        if (notes == null || notes.Count == 0) return;
        long earliestUs = actualTimeUs - inputWindowUs;
        long latestUs = actualTimeUs + inputWindowUs;
        int low = 0, high = notes.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (notes[mid].StartUs < earliestUs) low = mid + 1;
            else high = mid;
        }
        Nte.GuitarChart.Core.ChartNote? note = null;
        long bestError = long.MaxValue;
        for (int i = low; i < notes.Count && notes[i].StartUs <= latestUs; i++)
        {
            var candidate = notes[i];
            if (candidate.GameKey != obs.GameKey || _judgedNotes.ContainsKey(candidate.Id)) continue;
            long error = Math.Abs(candidate.StartUs - actualTimeUs);
            if (error >= bestError) continue;
            note = candidate;
            bestError = error;
        }
        if (note == null) return;
        _judgedNotes.TryAdd(note.Id, 0);
        var grade = _rhythmSession.JudgeInput(note.StartUs, actualTimeUs);
        PublishNoteFeedback(note, grade);
    }

    private void PublishNoteFeedback(Nte.GuitarChart.Core.ChartNote note, HitGrade grade) =>
        _bridge?.Publish(new
        {
            type = "note.feedback",
            payload = new
            {
                noteId = note.Id,
                gameKey = note.GameKey,
                noteNumber = note.NoteNumber,
                velocity = note.Velocity,
                durationUs = note.DurationUs,
                grade = grade.ToString()
            }
        });

    private void StartSimulatedMidiPlayback(long startOffsetUs)
    {
        var chart = _currentChart;
        if (chart == null) return;
        double speedRatio = _speedPercent / 100d;
        var notes = chart.Notes.Select(note => new MidiAutoPlayer.Models.RawMidiNote
        {
            StartUs = (long)(note.StartUs / speedRatio),
            DurationUs = (long)(note.DurationUs / speedRatio),
            NoteNumber = note.NoteNumber,
            Velocity = note.Velocity,
            Channel = note.Channel,
            TrackId = note.TrackIndex,
            Program = note.Program
        }).ToArray();
        _simulatedMidiPlayer.Play(
            notes,
            (long)(startOffsetUs / speedRatio),
            (long)(chart.DurationUs / speedRatio));
    }

    private void OnPositionSwitchKeyPressed(long wallClockUs)
    {
        long inputTimeUs = _isPlaying ? _rhythmSession.ToTimelineTimeUs(wallClockUs, _speedPercent) : _currentTimeUs;
        if (_isPlaying && _currentMode == PlayMode.Practice && _manualPracticePosition)
        {
            var practiceSwitch = _currentChart?.PositionEvents.FirstOrDefault(e => inputTimeUs >= e.PromptTimeUs && inputTimeUs <= e.DeadlineTimeUs);
            if (practiceSwitch != null)
            {
                _bridge?.Publish(new { type = "position.active", payload = new { page = practiceSwitch.To.Page, slot = practiceSwitch.To.Slot, stateText = $"已手动确认 P{practiceSwitch.To.Page} · S{practiceSwitch.To.Slot}" } });
                _judgedSwitches.TryAdd(practiceSwitch.Id, 0);
            }
            return;
        }
        if (!_isPlaying || _currentMode != PlayMode.RhythmHell || _executionMode != PositionExecutionMode.FullManual) return;
        var activeEvent = _currentChart?.PositionEvents.FirstOrDefault(e => inputTimeUs >= e.PromptTimeUs && inputTimeUs <= e.DeadlineTimeUs);
        if (activeEvent == null || !_judgedSwitches.TryAdd(activeEvent.Id, 0)) return;
        Toast("success", $"换位成功：P{activeEvent.To.Page} · S{activeEvent.To.Slot}");
    }

    private void OnGlobalKeyStateChanged(int virtualKey, int scanCode, bool isDown)
    {
        int gameLane = NativeInputObserver.MapConfiguredKeyToGameLane(virtualKey, scanCode, _inputObserver.GameLaneBindings);
        if (gameLane is >= 1 and <= 6)
            _bridge?.Publish(new { type = "input.feedback", payload = new { lane = gameLane, pressed = isDown } });

        if (_inputObserver.MatchesPositionSwitchKey(virtualKey, scanCode))
            _bridge?.Publish(new { type = "input.feedback", payload = new { lane = 7, pressed = isDown } });
    }

    private void PublishPracticeState(string message, bool? lastInputCorrect = null)
    {
        var targets = _practiceSession?.GetTargetsAt(_currentTimeUs) ?? Array.Empty<Nte.GuitarChart.Core.ChartNote>();
        if (targets.Count == 0 && _practiceSession != null) targets = _practiceSession.GetUpcomingTargets(_currentTimeUs);
        var target = targets.FirstOrDefault();
        var requiredKeys = targets.Where(n => n.GameKey.HasValue).Select(n => n.GameKey!.Value).Distinct().Order().ToArray();
        _bridge?.Publish(new
        {
            type = "practice.state",
            payload = new
            {
                mode = "practice",
                currentPage = target?.Position?.Page ?? 0,
                currentSlot = target?.Position?.Slot ?? 0,
                currentRequiredKeys = requiredKeys,
                playing = _isPlaying,
                timelineRunning = _practiceTimeline.IsRunning,
                currentTimeUs = _currentTimeUs,
                message
            }
        });
    }

    private void PublishPlaybackTick(string text, bool playing)
    {
        long nowMs = Environment.TickCount64;
        long minimumIntervalMs = _currentMode == PlayMode.Practice ? 40 : 250;
        if (playing && _lastPublishedPlaying == true && nowMs - Interlocked.Read(ref _lastPublishedTickMs) < minimumIntervalMs) return;
        Interlocked.Exchange(ref _lastPublishedTickMs, nowMs);
        _lastPublishedPlaying = playing;
        _bridge?.Publish(new { type = "playback.tick", payload = new { currentTimeUs = _currentTimeUs, durationUs = _currentChart?.DurationUs ?? 0, playing } });
    }

    private void PracticeTimelineLoop()
    {
        var chart = _currentChart;
        if (chart == null) return;
        while (!_stopRequested && _isPlaying && _currentMode == PlayMode.Practice)
        {
            long nowUs = MonotonicTimeUs();
            _currentTimeUs = _practiceTimeline.CurrentTimeUs(nowUs);
            PublishPlaybackTick(_practiceTimeline.IsRunning ? "练习进行中" : "等待按住目标键", _practiceTimeline.IsRunning);
            if (_practiceTimeline.IsRunning && _currentTimeUs >= chart.DurationUs)
            {
                _practiceTimeline.Pause(nowUs);
                _practiceDriverKey = 0;
                long? recoveryTimeUs = _practiceSession?.GetTailRecoveryTimeUs(_currentTimeUs, chart.DurationUs);
                if (recoveryTimeUs.HasValue)
                {
                    _currentTimeUs = recoveryTimeUs.Value;
                    _practiceTimeline.Seek(_currentTimeUs, nowUs);
                }
                _isPlaying = false;
                EndAutomaticImmersion();
                foreach (int lane in _heldGameKeys.Keys)
                    _bridge?.Publish(new { type = "input.feedback", payload = new { lane, pressed = false } });
                _heldGameKeys.Clear();
                _inputObserver.Stop();
                string message = recoveryTimeUs.HasValue
                    ? "已到曲尾，剩余目标已回到可练位置，按住目标键继续"
                    : "本段完成";
                PublishPracticeState(message);
                PublishPlaybackTick(message, false);
                return;
            }
            Thread.Sleep(10);
        }
    }

    private static long MonotonicTimeUs() =>
        System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
    private void Toast(string level, string message) => _bridge?.Publish(new { type = "app.toast", payload = new { level, message } });

    public void Dispose()
    {
        StopPlayback();
        _positionThreadStop = true;
        _positionSignal.Set();
        if (_positionThread.IsAlive && _positionThread != Thread.CurrentThread) _positionThread.Join(500);
        _inputObserver.Dispose();
        _simulatedMidiPlayer.Dispose();
        _window.Dispose();
        _pauseEvent.Dispose();
        _positionSignal.Dispose();
    }
}
