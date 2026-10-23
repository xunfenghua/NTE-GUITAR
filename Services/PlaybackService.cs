using MidiAutoPlayer.Bridge;
using MidiAutoPlayer.Core.Compilation;
using MidiAutoPlayer.Midi;
using MidiAutoPlayer.Guitar;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Library;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Preview;
using MidiAutoPlayer.Utils;
using System.Diagnostics;

namespace MidiAutoPlayer.Services;

public sealed class PlaybackService : IDisposable
{
    private static readonly ushort[] DrumKeyCodesDf = [0x20, 0x21, 0x24, 0x25];
    private static readonly ushort[] DrumKeyCodesAsQuote = [0x1E, 0x1F, 0x27, 0x28];
    private readonly InputScheduler _inputScheduler;
    private readonly GuitarScheduler _guitarScheduler;
    private readonly MidiPreviewPlayer _auditionPlayer;
    private readonly WebEventPublisher _eventPublisher;
    private readonly WheelController _wheelController;
    private readonly AppConfig _config;
    private readonly SpeedControlManager _speedManager = new();

    private ConvertedSong? _currentSong;
    private List<InputBatch>? _compiledBatches;
    private List<GuitarAction>? _compiledGuitarActions;
    private GuitarCompilerOptions? _compiledGuitarOptions;
    private List<FretPlayEvent>? _compiledFretEvents;
    private List<ChordChangeEvent>? _compiledChordEvents;
    private List<RawMidiNote>? _compiledAuditionNotes;
    private PlaybackStartAdvice? _lastStartAdvice;

    private double _currentSpeed = 1.0;
    private bool _isAuditioning;
    private bool _isCountdownPaused;
    private IReadOnlyList<RawMidiNote>? _currentAuditionNotes;
    private long _currentPositionUs;
    private long _totalDurationUs;
    private readonly object _countdownSync = new();
    private System.Threading.Timer? _countdownTimer;
    private int _countdownGeneration;
    private int _countdownRemaining;
    private long _pendingStartOffsetUs;
    private GuitarPreparedStart? _preparedGuitarStart;
    private long _lastGuitarSimulationPublishTicks;

    public PlaybackService(
        AppConfig config,
        WebEventPublisher eventPublisher,
        WheelController wheelController)
    {
        _config = config;
        _eventPublisher = eventPublisher;
        _wheelController = wheelController;
        // Mouse clicks shorter than a game frame can be missed by frame-polled
        // input. Keep wheel and strum clicks down for at least 25 ms.
        _wheelController.MouseClickHoldMs = Math.Max(25, _config.MouseClickHoldMs);

        _inputScheduler = new InputScheduler();
        _guitarScheduler = new GuitarScheduler();
        _auditionPlayer = new MidiPreviewPlayer();

        _inputScheduler.OnProgressChanged += (cur, tot) => HandleProgress(cur, tot, "playing");
        _inputScheduler.OnPlaybackStopped += HandleStopped;

        _guitarScheduler.OnProgressChanged += (cur, tot) => HandleProgress(cur, tot, "playing");
        _guitarScheduler.OnPlaybackStopped += HandleStopped;
        _guitarScheduler.OnSimulationAction += PublishGuitarSimulationAction;

        _auditionPlayer.OnProgress += (cur, tot) => HandleProgress(cur, tot, "auditioning");
        _auditionPlayer.OnPlaybackCompleted += HandleAuditionCompleted;
    }

    public ConvertedSong? CurrentSong => _currentSong;
    public Func<PlaybackStartAdvice?, bool>? ConfirmGuitarStart { get; set; }
    public Action? CancelPendingGuitarStartConfirmation { get; set; }
    public event Action<ConvertedSong>? CurrentSongChanged;
    public bool IsPlaying => _inputScheduler.IsPlaying || _guitarScheduler.IsPlaying || _isAuditioning;
    public bool IsCountdown => _countdownTimer != null;
    public bool IsPaused => _isCountdownPaused
                         || (_inputScheduler.IsPlaying && _inputScheduler.IsPaused) 
                         || (_guitarScheduler.IsPlaying && _guitarScheduler.IsPaused)
                         || (_isAuditioning && _auditionPlayer.IsPaused);

    public object LoadSong(ConvertedSong song)
    {
        StopInternal(resetPosition: true, publishState: true);
        _currentSong = song;
        _totalDurationUs = (long)Math.Round(song.DurationUs / _currentSpeed);
        _currentPositionUs = 0;

        int inst = SongLibrary.ResolveInstrument(song);
        if (inst == 0)
        {
            if (_config.PlayMode != 4)
                _config.PlayMode = 0;
        }
        else if (_config.PlayMode == 0)
        {
            _config.PlayMode = 1;
        }

        if (inst == 1)
        {
            var rawNotes = _config.PlayMode == 4
                ? song.RawNotes ?? MidiRawCache.TryGet(song.SourceHash) ?? EffectiveSongNotes.ToRawNotes(song)
                : EffectiveSongNotes.ToRawNotes(song);
            var speedNotes = _config.PlayMode == 3 ? MidiPolyphonyReducer.ReduceForGuitar(rawNotes) : rawNotes;
            int recommended = MidiSpeedOptimizer.CalculateAutoSpeedPercent(speedNotes);
            _speedManager.ApplyAutoSpeedOnMidiImport(recommended);
            _currentSpeed = _speedManager.EffectiveSpeedPercent / 100.0;
            _totalDurationUs = (long)Math.Round(song.DurationUs / _currentSpeed);
        }

        RecompileSong();
        PublishState("stopped");
        PublishSimulationSnapshot();
        CurrentSongChanged?.Invoke(song);

        return GetPlaybackStateDto("stopped");
    }

    public SetSpeedResultDto SetPlaybackSpeed(double requestedMultiplier)
    {
        var validated = PlaybackSpeedSetting.Validate(requestedMultiplier);

        if (_inputScheduler.IsPlaying || _guitarScheduler.IsPlaying || _countdownTimer != null || _isAuditioning)
        {
            throw new InvalidOperationException("演奏或试听进行中禁止直接改速，请先暂停或停止后再调整速度。");
        }

        double oldSpeed = _currentSpeed;
        _currentSpeed = validated.Multiplier;
        _speedManager.SetUserManualSpeed((int)Math.Round(_currentSpeed * 100));

        ReachabilityReport? report = null;
        if (_currentSong != null)
        {
            long sourceUs = (long)Math.Round(_currentPositionUs * oldSpeed);
            _currentPositionUs = (long)Math.Round(sourceUs / _currentSpeed);
            report = RecompileSongWithReport();
        }

        long effectiveDurationMs = _currentSong != null
            ? (long)Math.Round(_currentSong.DurationUs / (_currentSpeed * 1000.0))
            : 0;

        PublishState(IsPaused ? "paused" : "stopped");

        return new SetSpeedResultDto(
            Speed: _currentSpeed,
            EffectiveDurationMs: effectiveDurationMs,
            IsFullyReachable: report?.IsFullyReachable ?? true,
            RecommendedMaxSpeed: report?.RecommendedMaxSpeed ?? _currentSpeed,
            IssueCount: report?.Issues.Count ?? 0,
            FirstIssueSummary: report?.Issues.FirstOrDefault()?.Message
        );
    }

    public SetSpeedResultDto ResetAutoPlaybackSpeed()
    {
        if (_currentSong == null || (SongLibrary.ResolveInstrument(_currentSong) != 1 && _config.PlayMode != 4))
            throw new InvalidOperationException("自动配速仅适用于已载入的吉他或鼓点 MIDI。");
        var speedNotes = _config.PlayMode == 4
            ? _currentSong.RawNotes ?? MidiRawCache.TryGet(_currentSong.SourceHash) ?? EffectiveSongNotes.ToRawNotes(_currentSong)
            : EffectiveSongNotes.ToRawNotes(_currentSong);
        _speedManager.ApplyAutoSpeedOnMidiImport(
            MidiSpeedOptimizer.CalculateAutoSpeedPercent(_config.PlayMode == 3
                ? MidiPolyphonyReducer.ReduceForGuitar(speedNotes)
                : speedNotes),
            resetUserOverride: false);
        var result = SetPlaybackSpeed(_speedManager.AutoRecommendedSpeedPercent / 100.0);
        _speedManager.ResetToAutoSpeed();
        PublishState(IsPaused ? "paused" : "stopped");
        return result;
    }

    public void RecompileSong()
    {
        RecompileSongWithReport();
    }

    public Task<ReachabilityReport?> RecompileSongAsync() =>
        Task.Run(RecompileSongWithReport);

    public ReachabilityReport? RecompileSongWithReport()
    {
        if (_currentSong == null) return null;

        var rawNotes = _config.PlayMode == 4
            ? _currentSong.RawNotes ?? MidiRawCache.TryGet(_currentSong.SourceHash) ?? EffectiveSongNotes.ToRawNotes(_currentSong)
            : EffectiveSongNotes.ToRawNotes(_currentSong);
        if (_config.PlayMode != 0 && _speedManager.IsAutoSpeed)
        {
            double previousSpeed = _currentSpeed;
            var speedNotes = _config.PlayMode == 3 ? MidiPolyphonyReducer.ReduceForGuitar(rawNotes) : rawNotes;
            _speedManager.ApplyAutoSpeedOnMidiImport(MidiSpeedOptimizer.CalculateAutoSpeedPercent(speedNotes),
                resetUserOverride: false);
            _currentSpeed = _speedManager.EffectiveSpeedPercent / 100.0;
            if (Math.Abs(previousSpeed - _currentSpeed) > 1e-9)
                _currentPositionUs = (long)Math.Round(_currentPositionUs * previousSpeed / _currentSpeed);
        }

        // 阶段 A：对原始 MIDI 音符时间轴按 1/speed 缩放 (保持微秒级绝对精度)
        var scaledNotes = PlaybackSpeedEngine.ScaleMidiNotes(
            rawNotes,
            _currentSpeed,
            minNoteHoldUs: Math.Max(5_000, _config.MinHoldMs * 1000)
        );

        _totalDurationUs = (long)Math.Round(_currentSong.DurationUs / _currentSpeed);

        int playMode = _config.PlayMode;
        ReachabilityReport? report = null;

        if (playMode == 4) // Four-key drum mode
        {
            IReadOnlyList<ushort> scanCodes = _config.DrumKeyLayout == "ASQuote"
                ? DrumKeyCodesAsQuote
                : DrumKeyCodesDf;
            _compiledBatches = DrumMidiConverter.Convert(scaledNotes, scanCodes);
            _compiledAuditionNotes = null;
            _compiledGuitarActions = null;
            _compiledGuitarOptions = null;
            _compiledFretEvents = null;
            _compiledChordEvents = null;
            _lastStartAdvice = null;
            report = new ReachabilityReport(true, _currentSpeed, _currentSpeed, Array.Empty<ReachabilityIssue>());
        }
        else if (playMode == 0) // Keyboard script / 36-key mode
        {
            var converted = MidiConverter.Convert(
                scaledNotes,
                _currentSong.SourceName,
                _currentSong.SourceHash,
                _currentSong.DisplayName,
                new ConvertOptions
                {
                    TargetInstrument = 0,
                    Transpose = _config.Transpose,
                    OutOfRangeMode = _currentSong.Options?.OutOfRangeMode ?? "OctaveFold",
                    IgnoreDrumChannel = true
                });
            var gameNotes = converted.Notes;
            _compiledAuditionNotes = PlaybackPitchProjection.FromPianoEvents(gameNotes);

            _compiledBatches = ActionCompiler.Compile(
                gameNotes,
                _config.KeyPreset,
                new CompilerOptions
                {
                    MinHoldUs = Math.Max(5_000, _config.MinHoldMs * 1000),
                    ReleaseGapUs = _config.ReleaseGapMs * 1000,
                    TapDurationUs = _config.TapDurationMs * 1000,
                    MinRetriggerIntervalUs = _config.MinRetriggerIntervalMs * 1000,
                    LatencyOffsetUs = _config.LatencyCompensationMs * 1000,
                    EnableDynamicSustain = _config.EnableSustain
                }
            );
            _lastStartAdvice = null;
            _compiledGuitarActions = null;
            _compiledGuitarOptions = null;
            _compiledFretEvents = null;
            _compiledChordEvents = null;
            report = new ReachabilityReport(true, _currentSpeed, _currentSpeed, Array.Empty<ReachabilityIssue>());
        }
        else // Guitar (fret, chord, or full hybrid performance)
        {
            _compiledBatches = null;
            if (playMode == 1) // Fret Mode
            {
                _compiledChordEvents = null;
                // The game starts on P1; the compiler navigates to P5 for open strings.
                _compiledFretEvents = FretConverter.Convert(
                    scaledNotes,
                    GuitarTuning.Standard,
                    new FretConverterOptions
                    {
                        PageKeyHoldUs = Math.Max(25_000, _config.PageKeyHoldMs * 1000L),
                        PageInterTapGapUs = Math.Max(15_000, _config.PageInterTapGapMs * 1000L),
                        PageInputSafetyUs = _config.PageInputSafetyMs * 1000,
                        WheelMouseSettleUs = Math.Max(55_000, _config.WheelMouseSettleMs * 1000L),
                        InitialPage = 1,
                        AvoidStrum = _config.AvoidStrum
                    }
                );
                _lastStartAdvice = PlaybackStartAdvisor.GetAdvice(
                    1,
                    _compiledFretEvents,
                    chordEvents: null,
                    recommendDynamicStartPage: _config.RecommendDynamicStartPage
                );

                // 实际游戏页没有自动识别能力，统一以游戏默认 P1 作为物理起点。
                // 编译器据此生成首个音符所需的 R/T 动作，避免把建议页误当成已在游戏中选中的页。
                _compiledGuitarOptions = CreateGuitarCompilerOptions(initialPage: 1);
                _compiledGuitarActions = GuitarCompiler.CompileFretEvents(
                    _compiledFretEvents, _config.WheelPreset, _compiledGuitarOptions);
                _compiledAuditionNotes = PlaybackPitchProjection.FromFretEvents(_compiledFretEvents);

                // 阶段 C：硬件物理可达性校验
                var guitarEvents = _compiledFretEvents.Select(f => new CompiledGuitarNoteEvent(
                    EffectiveStartUs: f.TimeUs,
                    EffectiveDurationUs: f.DurationUs,
                    Pitch: f.NoteNumber,
                    TargetPage: f.Page,
                    TargetSlot: f.Slot,
                    PhysicalString: f.StringNo,
                    GameKeyScanCode: GameScanCodes.ForGuitarString(f.StringNo)
                )).ToList();

                var hwTiming = new HardwareTimingConstraints(
                    PageKeyHoldUs: _config.UseVirtualGamepad
                        ? Math.Max(GamepadInputTiming.PageHoldUs, _config.PageKeyHoldMs * 1000L)
                        : Math.Max(25_000, _config.PageKeyHoldMs * 1000L),
                    PageSwitchCooldownUs: _config.UseVirtualGamepad
                        ? Math.Max(GamepadInputTiming.PageReleaseGapUs, _config.PageInterTapGapMs * 1000L)
                        : Math.Max(15_000, _config.PageInterTapGapMs * 1000L),
                    SlotSelectSettleUs: _config.UseVirtualGamepad
                        ? GamepadInputTiming.SlotSelectLeadUs
                        : Math.Max(55_000, _config.WheelMouseSettleMs * 1000L),
                    StringMinHoldUs: _config.UseVirtualGamepad
                        ? Math.Max(GamepadInputTiming.StringHoldUs, _config.MinHoldMs * 1000L)
                        : Math.Max(20_000, _config.MinHoldMs * 1000L),
                    SameKeyReleaseGapUs: _config.UseVirtualGamepad
                        ? Math.Max(GamepadInputTiming.SameStringReleaseUs, _config.ReleaseGapMs * 1000L)
                        : Math.Max(22_000, _config.ReleaseGapMs * 1000L),
                    SafetyMarginUs: 4_000
                );

                report = PlaybackSpeedEngine.ValidateGuitarTimeline(guitarEvents, _currentSpeed, hwTiming);
            }
            else if (playMode == 2) // Chord Mode
            {
                _compiledFretEvents = null;
                var detailedEvents = ChordAnalyzerV2.AnalyzeDetailed(scaledNotes, out int semitoneShift,
                    new ChordAnalyzerV2Options { Mode = _config.ChordMode,
                        AttackClusterUs = (long)Math.Round(30_000 / _currentSpeed) });
                var accompanimentEvents = AccompanimentEventFilter.Prepare(
                    rawNotes, detailedEvents, semitoneShift, _currentSpeed);
                _compiledChordEvents = accompanimentEvents.Where(e => e.Chord != ChordId.Unknown)
                    .Select(e => new ChordChangeEvent(e.StartUs, e.Chord, (float)e.Confidence, e.Page, e.Slot)).ToList();
                _compiledAuditionNotes = PlaybackPitchProjection.FromChordEvents(
                    accompanimentEvents,
                    _config.ChordVoicing,
                    _config.PlayStyle);

                _lastStartAdvice = PlaybackStartAdvisor.GetAdvice(
                    2,
                    fretEvents: null,
                    _compiledChordEvents,
                    recommendDynamicStartPage: _config.RecommendDynamicStartPage
                );

                _compiledGuitarOptions = CreateGuitarCompilerOptions(initialPage: 1);
                _compiledGuitarOptions.PlayStyle = _config.PlayStyle;
                _compiledGuitarOptions.AutoStrum = _config.AutoStrum;
                _compiledGuitarOptions.VoicingProfile = _config.ChordVoicing;
                _compiledGuitarActions = _config.EnableArrangedAccompaniment
                    ? ArrangedAccompanimentCompiler.Compile(rawNotes, detailedEvents, semitoneShift, _currentSpeed,
                        _config.WheelPreset, _compiledGuitarOptions)
                    : GuitarCompiler.CompileChordDetailedEvents(accompanimentEvents, _config.WheelPreset, _compiledGuitarOptions);

                report = new ReachabilityReport(true, _currentSpeed, _currentSpeed, Array.Empty<ReachabilityIssue>());
            }
            else // Full fret + chord performance (Mode 3)
            {
                _compiledFretEvents = null;
                _compiledChordEvents = null;
                _compiledGuitarOptions = CreateGuitarCompilerOptions(initialPage: 1);
                var hybridOptions = new HybridCompilerOptions
                {
                    InitialMode = GuitarPlayModeType.Fret,
                    InitialPage = 1,
                    ChordMode = _config.ChordMode,
                    PlayStyle = _config.PlayStyle,
                    AutoStrum = _config.AutoStrum,
                    AvoidStrum = _config.AvoidStrum,
                    PagePrevScanCode = _config.WheelPreset.PagePrevScanCode,
                    PageNextScanCode = _config.WheelPreset.PageNextScanCode,
                    MinKeyHoldUs = Math.Max(5_000, _config.MinHoldMs * 1000L),
                    StringTapDurationUs = Math.Max(25_000, _config.TapDurationMs * 1000L),
                    ReleaseGapUs = Math.Max(22_000, _config.ReleaseGapMs * 1000L),
                    ControlInterTapGapUs = Math.Max(8_000, _config.PageInterTapGapMs * 1000L),
                    WheelMouseSettleUs = Math.Max(50_000, _config.WheelMouseSettleMs * 1000L),
                    UpStrumMouseButton = _config.WheelPreset.UpStrumButton,
                    DownStrumMouseButton = _config.WheelPreset.DownStrumButton
                };
                _compiledGuitarOptions.MinHoldUs = hybridOptions.MinKeyHoldUs;
                _compiledGuitarOptions.PageKeyHoldUs = Math.Max(25_000, _compiledGuitarOptions.PageKeyHoldUs);
                _compiledGuitarOptions.PageInterTapGapUs = Math.Max(15_000, _compiledGuitarOptions.PageInterTapGapUs);
                _compiledGuitarOptions.WheelMouseSettleUs = Math.Max(55_000, _compiledGuitarOptions.WheelMouseSettleUs);
                _compiledGuitarOptions.MinRetriggerGapUs = hybridOptions.ReleaseGapUs;
                _compiledGuitarActions = HybridGuitarCompiler.CompileFullPerformance(
                    rawNotes, _speedManager, _config.WheelPreset, hybridOptions);
                scaledNotes = MidiSpeedOptimizer.ScaleMidiNotes(
                    MidiPolyphonyReducer.ReduceForGuitar(rawNotes),
                    _speedManager.TimeScaleFactor,
                    hybridOptions.MinKeyHoldUs);
                int shift = ChordAnalyzerV2.FindBestSemitoneTranspose(scaledNotes);
                _compiledAuditionNotes = HybridGuitarCompiler.AlignPlayableNotes(scaledNotes, shift);
                _lastStartAdvice = new PlaybackStartAdvice
                {
                    PlayMode = 3, InitialPage = 1, TargetPage = 1,
                    ModeName = "吉他完整混合演奏", PageName = PlaybackStartAdvisor.GetFretPageName(1),
                    FirstItemDescription = "从品格模式 P1 起弹，程序自动切换和弦模式",
                    SummaryText = "请让游戏处于品格模式 P1；程序会自动执行 Z 切模式及轮盘翻页。"
                };
                report = new ReachabilityReport(true, _currentSpeed, _currentSpeed, Array.Empty<ReachabilityIssue>());
            }
        }

        if (_config.UseVirtualGamepad && _compiledGuitarActions is { Count: > 0 })
        {
            _compiledGuitarActions = GamepadActionPlanner.StaggerDpad(
                _compiledGuitarActions, _compiledGuitarOptions?.WheelMouseSettleUs ?? GamepadInputTiming.SlotSelectLeadUs);
        }

        PublishMidiRollSnapshot(scaledNotes, playMode);
        return report;
    }

    private GuitarCompilerOptions CreateGuitarCompilerOptions(byte initialPage)
    {
        return new GuitarCompilerOptions
        {
            PageKeyHoldUs = Math.Max(25_000, _config.PageKeyHoldMs * 1000L),
            PageInterTapGapUs = Math.Max(15_000, _config.PageInterTapGapMs * 1000L),
            PageInputSafetyUs = _config.PageInputSafetyMs * 1000,
            WheelMouseSettleUs = Math.Max(55_000, _config.WheelMouseSettleMs * 1000L),
            StringTapDurationUs = Math.Max(25_000, _config.TapDurationMs * 1000L),
            MinHoldUs = Math.Max(20_000, _config.MinHoldMs * 1000L),
            ReleaseGapUs = Math.Max(22_000, _config.ReleaseGapMs * 1000L),
            MinRetriggerGapUs = Math.Max(25_000, _config.MinRetriggerIntervalMs * 1000L),
            EnableSustain = _config.EnableSustain,
            InitialPage = initialPage
        };
    }

    private IReadOnlyList<RawMidiNote> GetAuditionNotes(IReadOnlyList<RawMidiNote> fallback)
    {
        return _compiledAuditionNotes is { Count: > 0 } ? _compiledAuditionNotes : fallback;
    }

    private void PublishMidiRollSnapshot(IReadOnlyList<RawMidiNote> scaledNotes, int playMode)
    {
        if (_currentSong == null) return;

        string modeLabel = playMode switch
        {
            0 => "36键常规",
            1 => "吉他独奏",
            2 => "和弦伴奏",
            3 => "品格与和弦混合",
            4 => "四键鼓点",
            _ => "实时音轨"
        };

        object[] noteDtos;
        if (playMode == 1 && _compiledFretEvents != null && _compiledFretEvents.Count > 0)
        {
            noteDtos = _compiledFretEvents.Select(f => new
            {
                startUs = f.TimeUs,
                durationUs = f.DurationUs,
                pitch = (int)f.NoteNumber,
                velocity = (int)f.Velocity,
                label = $"P{f.Page}·{f.StringNo}弦{f.Fret}品"
            }).Cast<object>().ToArray();
        }
        else
        {
            var displayNotes = playMode is 2 or 3 && _compiledAuditionNotes is { Count: > 0 }
                ? _compiledAuditionNotes
                : scaledNotes;
            noteDtos = displayNotes.Select(n => new
            {
                startUs = n.StartUs,
                durationUs = n.DurationUs,
                pitch = (int)n.NoteNumber,
                velocity = (int)n.Velocity,
                label = (string?)null
            }).Cast<object>().ToArray();
        }

        _eventPublisher.Publish("midi.roll.loaded", new
        {
            songId = _currentSong.Id,
          modeLabel = $"{(_currentSong.SourceFormat == "GTP" ? "GTP · " : "")}{_currentSong.DisplayName} ({modeLabel} {_currentSpeed:0.00}×)",
            notes = noteDtos
        });
    }

    public void StartPlay(long startOffsetMs = 0)
    {
        if (_currentSong == null) return;
        // The input transport is a runtime setting, so rebuild guitar action timings
        // from the saved mode before each playback start.
        if (_config.PlayMode is 1 or 2 or 3) RecompileSong();
        if (_config.PlayMode == 4 && _compiledBatches is not { Count: > 0 })
        {
            PublishState("stopped", "这首 MIDI 没有可播放的鼓点。");
            return;
        }
        if (_config.PlayMode is 1 or 2 or 3 && ConfirmGuitarStart != null && !ConfirmGuitarStart(_lastStartAdvice))
        {
            PublishState("stopped", "已取消吉他演奏；请将游戏切回起点界面后再开始。");
            return;
        }

        long offsetUs = startOffsetMs > 0 ? startOffsetMs * 1000 : _currentPositionUs;
        StopInternal(resetPosition: false, publishState: false);
        _currentPositionUs = offsetUs;
        if (offsetUs >= _totalDurationUs && _totalDurationUs > 0)
        {
            offsetUs = 0;
            _currentPositionUs = 0;
        }

        int preRollMs = _config.PlayMode is 1 or 2 or 3
            ? Math.Max(3_000, _config.PreRollMs)
            : _config.PreRollMs;
        _preparedGuitarStart = null;
        if (preRollMs > 0)
        {
            _countdownRemaining = (int)Math.Ceiling(preRollMs / 1000.0);
            _pendingStartOffsetUs = offsetUs;
            _isCountdownPaused = false;
            PublishState("countdown");

            lock (_countdownSync)
            {
                int generation = ++_countdownGeneration;
                _countdownTimer?.Dispose();
                _countdownTimer = new System.Threading.Timer(CountdownTick, generation, 1000, 1000);
            }
        }
        else
        {
            ExecuteStartPlay(offsetUs);
        }
    }

    private void CountdownTick(object? state)
    {
        if (state is not int generation) return;
        lock (_countdownSync)
        {
            if (generation != _countdownGeneration || _countdownTimer == null || _isCountdownPaused) return;

            _countdownRemaining--;
            if (_countdownRemaining > 0 && _countdownRemaining <= 2 &&
                (_config.PlayMode is 1 or 2 or 3) &&
                _preparedGuitarStart == null)
            {
                _preparedGuitarStart = PrepareGuitarStart(_pendingStartOffsetUs);
            }
            if (_countdownRemaining <= 0)
            {
                _countdownTimer.Dispose();
                _countdownTimer = null;
                // Keep the lock through startup so Stop() cannot race between the
                // final generation check and dispatching the first input actions.
                ExecuteStartPlay(_pendingStartOffsetUs);
            }
            else
            {
                string? countdownMessage = _config.PlayMode is 1 or 2 or 3
                    ? _preparedGuitarStart != null
                        ? "吉他界面已预切换，继续倒计时"
                        : _countdownRemaining <= 2 ? "请切回游戏窗口，正在准备吉他界面" : null
                    : null;
                PublishState("countdown", countdownMessage);
            }
        }
    }

    private void ExecuteStartPlay(long offsetUs)
    {
        if (_currentSong == null) return;

        int playMode = _config.PlayMode;
        if (playMode is 0 or 4 && _compiledBatches != null)
        {
            _inputScheduler.TargetWindow = _config.TargetWindowHandle;
            _inputScheduler.EnforceForegroundCheck = _config.AutoStopOnFocusLoss;
            _inputScheduler.Start(_compiledBatches, _config.KeyPreset, _totalDurationUs, offsetUs);
        }
        else if (playMode >= 1 && _compiledGuitarActions != null)
        {
            _guitarScheduler.TargetWindow = _config.TargetWindowHandle;
            _guitarScheduler.EnforceForegroundCheck = _config.AutoStopOnFocusLoss;
            _guitarScheduler.UseVirtualGamepad = _config.UseVirtualGamepad;
            var preparedStart = _preparedGuitarStart ?? PrepareGuitarStart(offsetUs);
            if (preparedStart == null)
            {
                _currentPositionUs = offsetUs;
                string reason = _config.UseVirtualGamepad && _guitarScheduler.GamepadError is { Length: > 0 } gamepadError
                    ? $"虚拟手柄初始化失败：{gamepadError}"
                    : "吉他校准未完成：请将游戏窗口切到前台后重新开始。";
                PublishState("stopped", reason);
                return;
            }
            _guitarScheduler.Start(
                _compiledGuitarActions,
                _wheelController,
                _totalDurationUs,
                offsetUs,
                compilerOptions: _compiledGuitarOptions,
                playMode: playMode,
                preparedStart: preparedStart
            );
            _preparedGuitarStart = null;
        }

        PublishState("playing");
    }

    private GuitarPreparedStart? PrepareGuitarStart(long offsetUs)
    {
        if (_compiledGuitarActions == null || _compiledGuitarOptions == null || _config.PlayMode is < 1 or > 3)
            return null;
        _guitarScheduler.TargetWindow = _config.TargetWindowHandle;
        _guitarScheduler.EnforceForegroundCheck = _config.AutoStopOnFocusLoss;
        _guitarScheduler.UseVirtualGamepad = _config.UseVirtualGamepad;
        _wheelController.MouseClickHoldMs = Math.Max(25, _config.MouseClickHoldMs);
        return _guitarScheduler.PrepareInitialState(
            _compiledGuitarActions, _wheelController, _config.PlayMode, _compiledGuitarOptions, offsetUs);
    }

    public void Pause()
    {
        bool pausedCountdown = false;
        lock (_countdownSync)
        {
            if (_countdownTimer != null)
            {
                ++_countdownGeneration;
                _countdownTimer.Dispose();
                _countdownTimer = null;
                _isCountdownPaused = true;
                pausedCountdown = true;
            }
        }
        if (pausedCountdown)
        {
            PublishState("paused", "倒计时已暂停");
            return;
        }

        if (_isAuditioning)
        {
            _auditionPlayer.Pause();
            PublishState("paused", "监听已暂停");
            return;
        }

        if (_inputScheduler.IsPlaying)
        {
            _inputScheduler.Pause();
            PublishState("paused", "演奏已暂停");
        }
        else if (_guitarScheduler.IsPlaying)
        {
            _guitarScheduler.Pause();
            PublishState("paused", "演奏已暂停");
        }
    }

    public void Resume()
    {
        bool resumedCountdown = false;
        lock (_countdownSync)
        {
            if (_isCountdownPaused)
            {
                _isCountdownPaused = false;
                resumedCountdown = true;
                if (_countdownRemaining > 0)
                {
                    int generation = ++_countdownGeneration;
                    _countdownTimer?.Dispose();
                    _countdownTimer = new System.Threading.Timer(CountdownTick, generation, 1000, 1000);
                }
                else
                {
                    ExecuteStartPlay(_pendingStartOffsetUs);
                }
            }
        }
        if (resumedCountdown)
        {
            if (_countdownRemaining > 0)
                PublishState("countdown", $"准备中: 倒计时 {_countdownRemaining}s");
            return;
        }

        if (_isAuditioning)
        {
            _auditionPlayer.Resume();
            PublishState("auditioning", "继续监听");
            return;
        }

        if (_inputScheduler.IsPaused)
        {
            _inputScheduler.Resume();
            PublishState("playing", "恢复演奏");
        }
        else if (_guitarScheduler.IsPaused)
        {
            _guitarScheduler.Resume();
            PublishState("playing", "恢复演奏");
        }
    }

    public void Stop()
    {
        StopInternal(resetPosition: true, publishState: true);
    }

    private void StopInternal(bool resetPosition, bool publishState)
    {
        CancelPendingGuitarStartConfirmation?.Invoke();
        lock (_countdownSync)
        {
            ++_countdownGeneration;
            _countdownTimer?.Dispose();
            _countdownTimer = null;
        }
        _isCountdownPaused = false;
        _preparedGuitarStart = null;

        if (_isAuditioning)
        {
            _isAuditioning = false;
            _auditionPlayer.Stop();
        }

        _inputScheduler.Stop();
        _guitarScheduler.Stop();
        _guitarScheduler.Dispose();
        if (resetPosition) _currentPositionUs = 0;
        if (publishState)
        {
            PublishState("stopped");
            PublishSimulationSnapshot();
        }
    }

    public void Seek(long positionMs)
    {
        long targetUs = Math.Clamp(positionMs * 1000, 0, _totalDurationUs);
        _currentPositionUs = targetUs;

        bool wasCountdown = _countdownTimer != null || _isCountdownPaused;
        bool wasPlaying = _inputScheduler.IsPlaying || _guitarScheduler.IsPlaying;
        bool wasAuditioning = _isAuditioning;
        bool wasPaused = IsPaused;

        _countdownTimer?.Dispose();
        _countdownTimer = null;
        _isCountdownPaused = false;

        if (wasAuditioning)
        {
            _auditionPlayer.Stop();
            if (wasPaused)
            {
                // Stay paused at target offset
                PublishState("paused", $"监听定位至 {TimeFormatter.FormatUs(targetUs)}");
            }
            else
            {
                var notes = _currentAuditionNotes;
                if (notes == null && _currentSong != null)
                {
                    notes = _currentSong.RawNotes ?? MidiRawCache.TryGet(_currentSong.SourceHash);
                }
                if (notes != null && notes.Count > 0)
                {
                    _isAuditioning = true;
                    _auditionPlayer.Play(notes, targetUs, _totalDurationUs);
                    PublishState("auditioning");
                }
                else
                {
                    _isAuditioning = false;
                    PublishState("stopped");
                }
            }
            return;
        }

        _inputScheduler.Stop();
        _guitarScheduler.Stop();

        if (wasPlaying || wasCountdown)
        {
            if (wasPaused)
            {
                PublishSimulationSnapshot();
                PublishState("paused", $"定位至 {TimeFormatter.FormatUs(targetUs)} (暂停)");
            }
            else
            {
                ExecuteStartPlay(targetUs);
            }
        }
        else
        {
            PublishState("stopped");
            PublishSimulationSnapshot();
        }
    }

    public void RecoverCurrentState()
    {
        PublishSimulationSnapshot();
        string curState = IsPaused ? "paused" : (IsPlaying ? "playing" : "stopped");
        PublishState(curState, "已执行状态对齐恢复");
    }

    public void StartAudition(long startOffsetMs = 0)
    {
        if (_currentSong == null) return;

        // The audition button is a start/stop toggle; play/pause stays on the main control.
        if (_isAuditioning)
        {
            Stop();
            return;
        }

        long offsetUs = startOffsetMs > 0 ? startOffsetMs * 1000 : _currentPositionUs;
        StopInternal(resetPosition: false, publishState: false);

        _isAuditioning = true;
        _currentPositionUs = offsetUs;
        if (offsetUs >= _totalDurationUs && _totalDurationUs > 0)
        {
            offsetUs = 0;
            _currentPositionUs = 0;
        }

        var rawNotes = EffectiveSongNotes.ToRawNotes(_currentSong);
        var scaledNotes = PlaybackSpeedEngine.ScaleMidiNotes(
            rawNotes,
            _currentSpeed,
            minNoteHoldUs: Math.Max(5_000, _config.MinHoldMs * 1000)
        );
        var auditionNotes = GetAuditionNotes(scaledNotes);

        _currentAuditionNotes = auditionNotes;
        _auditionPlayer.Play(auditionNotes, offsetUs, _totalDurationUs);
        PublishState("auditioning", "开始电脑监听");
    }

    public void StartAuditionNotes(IReadOnlyList<RawMidiNote> notes, long totalDurationUs = 0)
    {
        if (notes == null || notes.Count == 0) return;
        StopInternal(resetPosition: true, publishState: false);

        _isAuditioning = true;
        _currentAuditionNotes = notes;
        long durUs = totalDurationUs > 0 ? totalDurationUs : notes.Max(n => n.StartUs + n.DurationUs);
        _totalDurationUs = durUs;
        _currentPositionUs = 0;

        _auditionPlayer.Play(notes, 0, durUs);
        PublishState("auditioning", "开始试听提取结果");
    }

    public void TogglePlay()
    {
        if (IsPaused)
        {
            Resume();
        }
        else if (IsPlaying || IsCountdown)
        {
            Pause();
        }
        else
        {
            StartPlay();
        }
    }

    private void HandleProgress(long currentUs, long totalUs, string state)
    {
        // Progress callbacks can already be queued on the UI synchronization context
        // when Stop() is called. Ignore callbacks that belong to a playback that ended.
        if (state == "auditioning" && !_isAuditioning) return;
        if (state == "playing" && !_inputScheduler.IsPlaying && !_guitarScheduler.IsPlaying) return;

        _currentPositionUs = currentUs;
        _totalDurationUs = totalUs;
        PublishState(state);

        // Active guitar scheduler actions publish their own throttled simulator
        // events. Rebuilding a full note snapshot on every 50 ms progress tick
        // duplicates that work and scans the entire compiled chart repeatedly.
        if (state != "playing" && state != "auditioning")
            PublishSimulationSnapshot();
    }

    private void HandleAuditionCompleted()
    {
        // Ignore a completion notification that was queued just before an explicit stop.
        if (!_isAuditioning) return;
        _currentPositionUs = _totalDurationUs;
        HandleStopped(PlaybackStopReason.Completed, "试听播放完成");
    }

    private void HandleStopped(PlaybackStopReason reason, string message)
    {
        if (reason == PlaybackStopReason.Completed) _currentPositionUs = _totalDurationUs;
        _isAuditioning = false;
        string state = reason == PlaybackStopReason.Completed ? "completed" : "stopped";
        PublishState(state, message);
        PublishSimulationSnapshot();
    }

    private void PublishState(string state, string? message = null)
    {
        var dto = GetPlaybackStateDto(state, message);
        _eventPublisher.Publish("playback.stateChanged", dto);
    }

    public object GetPlaybackStateDto(string? overrideState = null, string? message = null)
    {
        string state;
        if (overrideState != null)
        {
            state = overrideState;
        }
        else if (_isCountdownPaused)
        {
            state = "paused";
        }
        else if (_countdownTimer != null)
        {
            state = "countdown";
        }
        else if (_isAuditioning)
        {
            state = _auditionPlayer.IsPaused ? "paused" : "auditioning";
        }
        else if (_inputScheduler.IsPlaying || _guitarScheduler.IsPlaying)
        {
            state = (_inputScheduler.IsPaused || _guitarScheduler.IsPaused) ? "paused" : "playing";
        }
        else
        {
            state = "stopped";
        }

        int playMode = _config.PlayMode;
        string instName = playMode switch { 0 => "键盘钢琴", 4 => "鼓组", _ => "吉他" };
        string modeName = playMode switch
        {
            0 => "36键常规",
            1 => "品格独奏",
            2 => "和弦伴奏",
            3 => "完整混合演奏",
            4 => "四键鼓点",
            _ => "常规模式"
        };

        return new
        {
            state,
            isAudition = _isAuditioning,
            message,
            positionMs = _currentPositionUs / 1000,
            positionUs = _currentPositionUs,
            durationMs = _totalDurationUs / 1000,
            durationUs = _totalDurationUs,
            speed = _currentSpeed,
            isAutoSpeed = _speedManager.IsAutoSpeed && playMode != 0,
            autoRecommendedSpeedPercent = _speedManager.AutoRecommendedSpeedPercent,
            positionFormatted = TimeFormatter.FormatUs(_currentPositionUs),
            durationFormatted = TimeFormatter.FormatUs(_totalDurationUs),
            countdownSeconds = _countdownRemaining,
            song = _currentSong != null ? new
            {
                id = _currentSong.Id,
                name = _currentSong.DisplayName,
                instrument = instName,
                mode = modeName,
                sourceFormat = _currentSong.SourceFormat,
                transpose = _config.Transpose,
                noteCount = _currentSong.NoteCount
            } : null,
            advice = _lastStartAdvice != null ? new
            {
                summary = _lastStartAdvice.SummaryText,
                pageName = _lastStartAdvice.PageName,
                targetPage = _lastStartAdvice.TargetPage,
                modeName = _lastStartAdvice.ModeName,
                initialModeName = _lastStartAdvice.InitialModeName,
                firstItem = _lastStartAdvice.FirstItemDescription,
                initialPage = _lastStartAdvice.InitialPage,
                playMode = _lastStartAdvice.PlayMode
            } : null
        };
    }

    public void PublishSimulationSnapshot()
    {
        long nowMs = _currentPositionUs / 1000;
        bool isAnyPlaying = _inputScheduler.IsPlaying || _guitarScheduler.IsPlaying || _isAuditioning;
        string playState = isAnyPlaying ? "playing" : "stopped";

        if (_currentSong == null || _config.PlayMode is 0 or 4)
        {
            var emptySnap = new Bridge.GuitarSimulationActionDto(
                Mode: "piano",
                CurrentPage: "P1",
                CurrentSlot: 1,
                ActionType: "idle",
                StringNo: 0,
                Fret: 0,
                ChordName: null,
                GameKey: "",
                Message: _currentSong == null ? "未选择曲目" : _config.PlayMode == 4 ? "四键鼓点模式：按键 D/F/J/K 或 A/S/;/' 演奏" : "键盘脚本模式（36 键）: 物理按键 A~', Q~], 1~= 演奏",
                TimestampMs: nowMs,
                ActiveNotes: Array.Empty<GuitarNoteMarker>(),
                PlaybackState: playState
            );
            _eventPublisher.Publish("guitar.simulationUpdated", emptySnap);
            return;
        }

        string mode = _config.PlayMode == 2 ? "chord" : "fret";
        string currentPage = "P1";
        if (_lastStartAdvice != null)
        {
            currentPage = $"P{_lastStartAdvice.InitialPage}";
        }
        int currentSlot = 1;
        string? currentChord = null;
        var activeList = new List<GuitarNoteMarker>();

        long nowUs = _currentPositionUs;

        // 若吉他调度器正在播放，以调度器的物理实机状态为准，绝不在休止/空隙时弹回 P1 造成闪烁！
        if (_guitarScheduler.IsPlaying)
        {
            if (_config.PlayMode == 3) mode = _guitarScheduler.CurrentMode == 2 ? "chord" : "fret";
            currentPage = $"P{_guitarScheduler.CurrentPage}";
            currentSlot = _guitarScheduler.CurrentSlot > 0 ? _guitarScheduler.CurrentSlot : 1;
            if (_config.PlayMode == 2 || (_config.PlayMode == 3 && _guitarScheduler.CurrentMode == 2))
            {
                currentChord = GuitarScheduler.ResolveSlotLabel(2, _guitarScheduler.CurrentPage, (byte)currentSlot);
            }
        }
        else
        {
            // 非调度器播放时（如暂停、进度条拖动定位），跟踪当前时间轴位置最近的音符/和弦页码与槽位
            if (_config.PlayMode == 3 && _compiledGuitarActions is { Count: > 0 })
            {
                var expected = GuitarScheduler.InspectExpectedHybridStateAt(_compiledGuitarActions, nowUs);
                mode = expected.Mode == 2 ? "chord" : "fret";
                currentPage = $"P{expected.Page}";
                currentSlot = expected.Slot > 0 ? expected.Slot : 1;
                if (expected.Mode == 2)
                    currentChord = GuitarScheduler.ResolveSlotLabel(2, expected.Page, (byte)currentSlot);
            }
            else if (_config.PlayMode == 1 && _compiledFretEvents != null && _compiledFretEvents.Count > 0)
            {
                var curEv = _compiledFretEvents.LastOrDefault(e => e.TimeUs <= nowUs) ?? _compiledFretEvents.FirstOrDefault();
                if (curEv != null)
                {
                    currentPage = $"P{curEv.Page}";
                    currentSlot = curEv.Slot > 0 ? curEv.Slot : (((curEv.Fret - 1) % 6) + 1);
                }
            }
            else if (_config.PlayMode == 2 && _compiledChordEvents != null && _compiledChordEvents.Count > 0)
            {
                var curChord = _compiledChordEvents.LastOrDefault(c => c.TimeUs <= nowUs) ?? _compiledChordEvents.FirstOrDefault();
                if (curChord != null)
                {
                    currentPage = $"P{curChord.Page}";
                    currentChord = curChord.Chord.ToString();
                    currentSlot = curChord.Slot > 0 ? curChord.Slot : 1;
                }
            }
        }

        if (_config.PlayMode == 1 && _compiledFretEvents != null)
        {
            var active = _compiledFretEvents.Where(e => nowUs >= e.TimeUs && nowUs <= e.TimeUs + e.DurationUs).ToList();
            if (active.Count > 0 && !_guitarScheduler.IsPlaying)
            {
                var first = active[0];
                currentPage = $"P{first.Page}";
                currentSlot = first.Slot > 0 ? first.Slot : (((first.Fret - 1) % 6) + 1);
            }

            foreach (var ev in active)
            {
                activeList.Add(new GuitarNoteMarker(
                    MidiPitch: ev.NoteNumber,
                    GameKey: GameScanCodes.GetGameDigit(ev.StringNo),
                    PhysicalString: ev.StringNo,
                    Fret: ev.Fret,
                    StartMs: ev.TimeUs / 1000,
                    DurationMs: ev.DurationUs / 1000,
                    IsPageChangeRequired: false
                ));
            }
        }
        else if (_config.PlayMode == 2 && _compiledChordEvents != null && !_guitarScheduler.IsPlaying)
        {
            var cur = _compiledChordEvents.LastOrDefault(c => c.TimeUs <= nowUs);
            if (cur != null)
            {
                currentPage = $"P{cur.Page}";
                currentChord = cur.Chord.ToString();
                currentSlot = cur.Slot > 0 ? cur.Slot : 1;
            }
        }

        string actionType = "idle";
        int stringNo = 0;
        int fret = 0;
        object gameKey = "";

        if (!isAnyPlaying)
        {
            actionType = "idle";
            if (_config.PlayMode == 1 && _compiledFretEvents != null && _compiledFretEvents.Count > 0)
            {
                var firstEv = _compiledFretEvents.FirstOrDefault(e => !e.IsTimingConflict) ?? _compiledFretEvents[0];
                currentSlot = firstEv.Slot > 0 ? firstEv.Slot : (((firstEv.Fret - 1) % 6) + 1);
            }
            else if (_config.PlayMode == 2 && _compiledChordEvents != null && _compiledChordEvents.Count > 0)
            {
                var firstChord = _compiledChordEvents.FirstOrDefault(c => c.Chord != ChordId.Unknown) ?? _compiledChordEvents[0];
                currentSlot = firstChord.Slot > 0 ? firstChord.Slot : 1;
                currentChord = firstChord.Chord.ToString();
            }

        }
        else if (activeList.Count > 0)
        {
            actionType = "pluck";
            stringNo = activeList[0].PhysicalString ?? 1;
            fret = activeList[0].Fret ?? 0;
            gameKey = activeList[0].GameKey;
        }
        else if (!string.IsNullOrEmpty(currentChord))
        {
            actionType = "slot";
        }

        var snapshot = new Bridge.GuitarSimulationActionDto(
            Mode: mode,
            CurrentPage: currentPage,
            CurrentSlot: currentSlot,
            ActionType: actionType,
            StringNo: stringNo,
            Fret: fret,
            ChordName: currentChord,
            GameKey: gameKey,
            Message: string.Empty,
            TimestampMs: nowMs,
            ActiveNotes: activeList,
            PlaybackState: playState
        );

        _eventPublisher.Publish("guitar.simulationUpdated", snapshot);
    }

    private void PublishGuitarSimulationAction(Bridge.GuitarSimulationActionDto dto)
    {
        // This event only animates the UI. Coalesce dense note bursts to at most
        // 30 updates/second; actual keyboard and mouse actions remain unthrottled.
        long now = Stopwatch.GetTimestamp();
        long minInterval = Math.Max(1, Stopwatch.Frequency / 30);
        while (true)
        {
            long last = Interlocked.Read(ref _lastGuitarSimulationPublishTicks);
            if (now - last < minInterval) return;
            if (Interlocked.CompareExchange(ref _lastGuitarSimulationPublishTicks, now, last) == last)
            {
                _eventPublisher.Publish("guitar.simulationUpdated", dto);
                return;
            }
        }
    }

    public void Dispose()
    {
        _countdownTimer?.Dispose();
        _inputScheduler.Stop();
        _guitarScheduler.Stop();
        _auditionPlayer.Dispose();
    }
}
