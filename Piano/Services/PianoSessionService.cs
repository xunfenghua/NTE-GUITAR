using MidiAutoPlayer.Bridge;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Piano.Compiler;
using MidiAutoPlayer.Piano.Models;

namespace MidiAutoPlayer.Piano.Services;

public sealed class PianoSessionService : IDisposable
{
    private readonly AppConfig _config;
    private readonly WebEventPublisher _events;
    private readonly InputScheduler _scheduler = new();
    private readonly object _stateLock = new();
    private PianoCompileResult? _lastCompile;
    private PianoLayoutProfile? _lastProfile;
    private IReadOnlyList<InputBatch>? _pendingBatches;
    private IntPtr _pendingTargetWindow;
    private string _status = "stopped";
    private long _currentUs;
    private long _totalUs;
    private System.Threading.Timer? _countdownTimer;
    private int _countdownRemaining;
    private int _countdownGeneration;
    private bool _countdownPaused;
    private bool _disposed;

    public PianoSessionService(AppConfig config, WebEventPublisher events)
    {
        _config = config;
        _events = events;
        _scheduler.OnProgressChanged += OnProgressChanged;
        _scheduler.OnPlaybackStopped += OnPlaybackStopped;
        _scheduler.EnforceForegroundCheck = true;
    }

    public bool IsPlaying => _scheduler.IsPlaying;
    public bool IsPaused
    {
        get { lock (_stateLock) return _countdownPaused || (_scheduler.IsPlaying && _scheduler.IsPaused); }
    }
    public bool IsActive
    {
        get { lock (_stateLock) return (_status is "countdown" or "playing" or "paused") || _scheduler.IsPlaying; }
    }

    public PianoCompileResult CompilePreview(
        IReadOnlyList<RawMidiNote> notes,
        string sourceHash,
        PianoLayoutProfile profile,
        PianoCompileOptions options,
        string? requestId = null)
    {
        EnsureNotDisposed();
        if (IsActive) throw new InvalidOperationException("游戏钢琴正在演奏，请先停止后再编译预览。");
        options.ModeId = "GamePiano";
        options.SourceHash = sourceHash;
        var result = PianoMidiCompiler.Compile(notes, profile, options, requestId);
        lock (_stateLock)
        {
            _lastCompile = result;
            _lastProfile = profile;
            _totalUs = result.Actions.Count == 0 ? 0 : result.Actions.Max(action => action.TimeUs);
            _currentUs = 0;
            _status = "preview";
        }
        PublishState();
        return result;
    }

    public object Start(
        IReadOnlyList<RawMidiNote> notes,
        string sourceHash,
        PianoLayoutProfile profile,
        PianoCompileOptions options)
    {
        EnsureNotDisposed();
        if (IsActive) throw new InvalidOperationException("游戏钢琴已在演奏或准备中。");
        if (_config.TargetWindowHandle == IntPtr.Zero)
            throw new InvalidOperationException("请先在“游戏校准”中绑定游戏窗口，再启动游戏钢琴。");
        options.ModeId = "GamePiano";
        options.SourceHash = sourceHash;
        var result = PianoMidiCompiler.Compile(notes, profile, options);
        if (!result.CanStart)
            throw new InvalidOperationException(result.Issues.FirstOrDefault(issue => issue.Severity == PianoIssueSeverity.Error)?.Message
                ?? "这份 MIDI 没有可演奏的有效音符。");
        if (result.Actions.Count == 0)
            throw new InvalidOperationException("这份 MIDI 没有可演奏的有效音符。");

        var batches = result.Actions
            .GroupBy(action => action.TimeUs)
            .OrderBy(group => group.Key)
            .Select(group => new InputBatch(group.Key, group.Select(action => new KeyAction(
                action.TimeUs,
                action.ScanCode,
                action.Type is PianoActionType.KeyDown or PianoActionType.ModifierDown,
                action.Extended)).ToList()))
            .ToList();
        long durationUs = result.Actions.Max(action => action.TimeUs);
        if (durationUs <= 0) throw new InvalidOperationException("钢琴动作时长无效。");

        lock (_stateLock)
        {
            _lastCompile = result;
            _lastProfile = profile;
            _pendingBatches = batches;
            _pendingTargetWindow = _config.TargetWindowHandle;
            _totalUs = durationUs;
            _currentUs = 0;
            _countdownRemaining = 3;
            _countdownPaused = false;
            _status = "countdown";
            int generation = ++_countdownGeneration;
            IntPtr targetWindow = _pendingTargetWindow;
            _countdownTimer = new System.Threading.Timer(_ => CountdownTick(batches, profile, durationUs, targetWindow, generation), null, 1000, 1000);
        }
        PublishMidiRollSnapshot(notes, result, options.FilterDrumChannel10);
        PublishState();
        return GetState();
    }

    private void CountdownTick(IReadOnlyList<InputBatch> batches, PianoLayoutProfile profile, long durationUs, IntPtr targetWindow, int generation)
    {
        string? message = null;
        lock (_stateLock)
        {
            if (_status != "countdown" || generation != _countdownGeneration) return;
            _countdownRemaining--;
            if (_countdownRemaining == 0)
            {
                _countdownTimer?.Dispose();
                _countdownTimer = null;
                if (_config.TargetWindowHandle != targetWindow || !KeyboardSender.IsForeground(targetWindow))
                {
                    _status = "stopped";
                    _pendingBatches = null;
                    _pendingTargetWindow = IntPtr.Zero;
                    _countdownRemaining = 0;
                    message = "游戏窗口未保持在前台或绑定已改变，钢琴演奏未启动。";
                }
                else
                {
                    _scheduler.TargetWindow = targetWindow;
                    _scheduler.EnforceForegroundCheck = true;
                    _scheduler.BatchSender = actions => KeyboardSender.TryPostBatch(targetWindow, actions);
                    _status = "playing";
                    _scheduler.Start(batches, BuildReleasePreset(profile), durationUs);
                }
            }
        }
        PublishState(message);
    }

    public object Pause()
    {
        lock (_stateLock)
        {
            if (_status == "countdown")
            {
                _countdownGeneration++;
                _countdownTimer?.Dispose();
                _countdownTimer = null;
                _countdownPaused = true;
                _status = "paused";
                PublishState();
                return GetState();
            }
        }
        if (_scheduler.IsPlaying && !_scheduler.IsPaused)
        {
            _scheduler.Pause();
            lock (_stateLock) _status = "paused";
            PublishState();
        }
        return GetState();
    }

    public object Resume()
    {
        lock (_stateLock)
        {
            if (_countdownPaused)
            {
                _countdownPaused = false;
                _status = "countdown";
                int generation = ++_countdownGeneration;
                var batches = _pendingBatches!;
                var profile = _lastProfile!;
                long durationUs = _totalUs;
                IntPtr targetWindow = _pendingTargetWindow;
                _countdownTimer = new System.Threading.Timer(_ => CountdownTick(batches, profile, durationUs, targetWindow, generation), null, 1000, 1000);
                PublishState();
                return GetState();
            }
        }
        if (_scheduler.IsPlaying && _scheduler.IsPaused)
        {
            _scheduler.Resume();
            lock (_stateLock) _status = "playing";
            PublishState();
        }
        return GetState();
    }

    public object TogglePause()
    {
        if (!IsActive) return GetState();
        return IsPaused ? Resume() : Pause();
    }

    public object Stop()
    {
        lock (_stateLock)
        {
            _countdownGeneration++;
            _countdownTimer?.Dispose();
            _countdownTimer = null;
            _countdownPaused = false;
            _countdownRemaining = 0;
            _pendingBatches = null;
            _pendingTargetWindow = IntPtr.Zero;
            _status = "stopped";
            _currentUs = 0;
        }
        if (_scheduler.IsPlaying)
        {
            _scheduler.Stop();
        }
        PublishState();
        return GetState();
    }

    public object GetState()
    {
        lock (_stateLock)
        {
            return new
            {
                modeId = "GamePiano",
                status = _status,
                isPlaying = _scheduler.IsPlaying,
                isPaused = _countdownPaused || _scheduler.IsPaused,
                countdownSeconds = _countdownRemaining,
                currentUs = _currentUs,
                totalUs = _totalUs,
                progress = _totalUs <= 0 ? 0 : Math.Clamp((double)_currentUs / _totalUs, 0, 1),
                compileFingerprint = _lastCompile?.CompileFingerprint,
                canStart = _lastCompile?.CanStart ?? false,
                requiresUserConfirmation = _lastCompile?.RequiresUserConfirmation ?? false
            };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_stateLock)
        {
            _countdownGeneration++;
            _countdownTimer?.Dispose();
            _countdownTimer = null;
            _status = "stopped";
        }
        _scheduler.Stop();
        _scheduler.OnProgressChanged -= OnProgressChanged;
        _scheduler.OnPlaybackStopped -= OnPlaybackStopped;
        _scheduler.Dispose();
    }

    private void OnProgressChanged(long currentUs, long totalUs)
    {
        lock (_stateLock)
        {
            if (_status != "playing") return;
            _currentUs = currentUs;
            _totalUs = totalUs;
            _status = "playing";
        }
        PublishState(includeActiveNotes: true);
    }

    private void OnPlaybackStopped(PlaybackStopReason reason, string message)
    {
        lock (_stateLock)
        {
            _status = "stopped";
            _pendingBatches = null;
            _pendingTargetWindow = IntPtr.Zero;
            _currentUs = reason == PlaybackStopReason.Completed ? _totalUs : 0;
        }
        PublishState(message);
    }

    private void PublishState(string? message = null, bool includeActiveNotes = false)
    {
        var state = GetState();
        _events.Publish("piano.playbackState", new
        {
            state,
            message,
            activeNotes = includeActiveNotes ? GetActiveNotes() : Array.Empty<object>()
        });
    }

    private void PublishMidiRollSnapshot(IReadOnlyList<RawMidiNote> notes, PianoCompileResult result, bool filterDrumChannel10)
    {
        int speedPercent = Math.Clamp(result.SpeedPercent, 25, 200);
        var noteDtos = notes
            .Where(note => !filterDrumChannel10 || note.Channel != 9)
            .Select(note => new
            {
                startUs = Math.Max(0, note.StartUs) * 100 / speedPercent,
                durationUs = Math.Max(15_000, note.DurationUs) * 100 / speedPercent,
                pitch = Math.Clamp(note.NoteNumber + result.EffectiveTransposeSemitones, 0, 127),
                velocity = (int)note.Velocity
            })
            .ToArray();

        _events.Publish("midi.roll.loaded", new
        {
            modeLabel = "游戏钢琴",
            notes = noteDtos
        });
    }

    private IReadOnlyList<object> GetActiveNotes()
    {
        lock (_stateLock)
        {
            if (_lastCompile is null || _lastProfile is null || _status != "playing") return Array.Empty<object>();
            return _lastCompile.Actions
                .Where(action => action.Type == PianoActionType.KeyDown && action.TimeUs <= _currentUs)
                .Select(action =>
                {
                    var end = _lastCompile.Actions.FirstOrDefault(candidate =>
                        candidate.Type == PianoActionType.KeyUp &&
                        candidate.ScanCode == action.ScanCode && candidate.Extended == action.Extended && candidate.TimeUs >= action.TimeUs);
                    if (end is null || _currentUs >= end.TimeUs) return null;
                    var binding = _lastProfile.Bindings.FirstOrDefault(candidate =>
                        candidate.ScanCode == action.ScanCode && candidate.Extended == action.Extended);
                    return (object)new
                    {
                        action.MidiPitch,
                        row = binding?.Row.ToString(),
                        position = binding?.Position,
                        scanCode = action.ScanCode
                    };
                })
                .Where(note => note is not null)
                .Cast<object>()
                .ToArray();
        }
    }

    private static KeyPreset BuildReleasePreset(PianoLayoutProfile profile)
    {
        var scans = profile.Bindings
            .Where(binding => binding.ScanCode is > 0)
            .Select(binding => (ScanCode: binding.ScanCode!.Value, binding.Extended, binding.DisplayKey, binding.BaseMidiPitch))
            .ToList();
        if (profile.ModifierProfile is { } modifiers)
        {
            if (modifiers.Shift.ScanCode is > 0) scans.Add((modifiers.Shift.ScanCode.Value, modifiers.Shift.Extended, "Shift", null));
            if (modifiers.Control.ScanCode is > 0) scans.Add((modifiers.Control.ScanCode.Value, modifiers.Control.Extended, "Ctrl", null));
        }

        var unique = scans.DistinctBy(item => (item.ScanCode, item.Extended)).ToList();
        return new KeyPreset
        {
            Name = "GamePianoReleaseOnly",
            Version = 1,
            Keys = unique.Select((item, index) => new KeyMapping
            {
                NoteIndex = (byte)index,
                NoteName = item.BaseMidiPitch is int pitch ? $"MIDI {pitch}" : "Piano",
                MidiNumber = (byte)Math.Clamp(item.BaseMidiPitch ?? 0, 0, 127),
                KeyName = item.DisplayKey,
                ScanCode = item.ScanCode,
                Extended = item.Extended
            }).ToList()
        };
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PianoSessionService));
    }
}
