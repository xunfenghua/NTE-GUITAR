using System.Diagnostics;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Input;

public enum PlaybackStopReason
{
    Completed,
    UserStopped,
    FocusLost,
    Error
}

public sealed class InputScheduler : IDisposable
{
    private readonly object _stateLock = new();
    private Thread? _workerThread;
    private volatile bool _stopRequested;
    private readonly ManualResetEventSlim _pauseEvent = new(true);

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public IntPtr TargetWindow { get; set; } = IntPtr.Zero;
    public bool EnforceForegroundCheck { get; set; } = true;
    public Func<IReadOnlyList<KeyAction>, bool>? BatchSender { get; set; }

    public event Action<long, long>? OnProgressChanged;
    public event Action<PlaybackStopReason, string>? OnPlaybackStopped;

    private IReadOnlyList<KeyAction> _currentReleaseActions = Array.Empty<KeyAction>();

    public void Start(IReadOnlyList<InputBatch> batches, KeyPreset preset, long totalDurationUs, long startOffsetUs = 0)
    {
        Stop();

        lock (_stateLock)
        {
            _stopRequested = false;
            _pauseEvent.Set();
            IsPlaying = true;
            IsPaused = false;
            _currentReleaseActions = CreateReleaseActions(preset, batches);

            _workerThread = new Thread(() => PlaybackLoop(batches, preset, totalDurationUs, startOffsetUs))
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "NTE-GUITAR_Scheduler"
            };
            _workerThread.Start();
        }
    }

    public void Pause()
    {
        lock (_stateLock)
        {
            if (!IsPlaying || IsPaused) return;
            IsPaused = true;
            _pauseEvent.Reset();
        }

        // 状态恢复：暂停时立即释放所有物理按键，防止悬挂长按与吞键
        if (_currentReleaseActions.Count > 0)
        {
            TrySendBatch(_currentReleaseActions);
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
            if (!IsPlaying && _workerThread == null) return;
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
    }

    public void Dispose()
    {
        Stop();
        _pauseEvent.Dispose();
    }

    private void PlaybackLoop(IReadOnlyList<InputBatch> batches, KeyPreset preset, long totalDurationUs, long startOffsetUs)
    {
        PlaybackStopReason stopReason = PlaybackStopReason.Completed;
        string stopMessage = "播放完成";

        try
        {
            long frequency = Stopwatch.Frequency;
            var holdGuard = new KeyboardHoldGuard(frequency);
            long startTimestamp = Stopwatch.GetTimestamp();
            long offsetTicks = (startOffsetUs * frequency) / 1_000_000;
            long baseTimestamp = startTimestamp - offsetTicks;

            // Find first batch index
            int startIndex = 0;
            while (startIndex < batches.Count && batches[startIndex].TimeUs < startOffsetUs)
            {
                startIndex++;
            }

            long lastUiUpdateTimestamp = 0;
            long uiUpdateIntervalTicks = (frequency * 50) / 1000; // 50ms UI update
            long lastFocusCheckTimestamp = startTimestamp;
            long focusCheckIntervalTicks = (frequency * 20) / 1000;

            for (int i = startIndex; i < batches.Count; i++)
            {
                if (_stopRequested)
                {
                    stopReason = PlaybackStopReason.UserStopped;
                    stopMessage = "用户手动停止";
                    break;
                }

                // Handle pause
                if (IsPaused)
                {
                    holdGuard.Reset();
                    long pauseStartTimestamp = Stopwatch.GetTimestamp();
                    _pauseEvent.Wait();
                    if (_stopRequested)
                    {
                        stopReason = PlaybackStopReason.UserStopped;
                        stopMessage = "用户手动停止";
                        break;
                    }
                    long pausedDurationTicks = Stopwatch.GetTimestamp() - pauseStartTimestamp;
                    baseTimestamp += pausedDurationTicks;
                }

                // Check foreground window lock
                if (TargetWindow == IntPtr.Zero ||
                    (EnforceForegroundCheck && KeyboardSender.GetForegroundWindow() != TargetWindow))
                {
                    stopReason = PlaybackStopReason.FocusLost;
                    stopMessage = TargetWindow == IntPtr.Zero
                        ? "尚未绑定游戏窗口，已安全停止演奏"
                        : "目标窗口失去焦点，已自动安全停止";
                    break;
                }

                var batch = batches[i];
                long scheduledTicks = baseTimestamp + (batch.TimeUs * frequency) / 1_000_000;
                long targetTicks = holdGuard.EarliestDispatchTicks(batch.Actions, scheduledTicks);

                // High precision two-stage wait
                while (true)
                {
                    if (_stopRequested || IsPaused) break;

                    long currentTicks = Stopwatch.GetTimestamp();
                    if (TargetWindow == IntPtr.Zero ||
                        (EnforceForegroundCheck && currentTicks - lastFocusCheckTimestamp >= focusCheckIntervalTicks))
                    {
                        lastFocusCheckTimestamp = currentTicks;
                        if (TargetWindow == IntPtr.Zero || KeyboardSender.GetForegroundWindow() != TargetWindow)
                        {
                            stopReason = PlaybackStopReason.FocusLost;
                            stopMessage = TargetWindow == IntPtr.Zero
                                ? "尚未绑定游戏窗口，已安全停止演奏"
                                : "目标窗口失去焦点，已自动安全停止";
                            break;
                        }
                    }

                    long remainingTicks = targetTicks - currentTicks;
                    if (remainingTicks <= 0) break;

                    long remainingMs = (remainingTicks * 1000) / frequency;

                    // Periodic UI progress update while waiting
                    if (currentTicks - lastUiUpdateTimestamp >= uiUpdateIntervalTicks)
                    {
                        lastUiUpdateTimestamp = currentTicks;
                        long currentUs = ((currentTicks - baseTimestamp) * 1_000_000) / frequency;
                        OnProgressChanged?.Invoke(Math.Min(currentUs, totalDurationUs), totalDurationUs);
                    }

                    if (remainingMs > 3)
                    {
                        // Sleep for part of the remaining time
                        int sleepMs = (int)(remainingMs - 2);
                        if (sleepMs > 0)
                            Thread.Sleep(sleepMs > 15 ? 10 : 1);
                    }
                    else if (remainingMs > 1)
                    {
                        Thread.Yield();
                    }
                    else
                    {
                        // SpinWait for sub-millisecond precision
                        Thread.SpinWait(20);
                    }
                }

                if (stopReason == PlaybackStopReason.FocusLost)
                    break;

                if (_stopRequested)
                {
                    stopReason = PlaybackStopReason.UserStopped;
                    stopMessage = "用户手动停止";
                    break;
                }

                if (IsPaused)
                {
                    if (_currentReleaseActions.Count > 0)
                    {
                        TrySendBatch(_currentReleaseActions);
                    }
                    holdGuard.Reset();
                    long pauseStartTimestamp = Stopwatch.GetTimestamp();
                    _pauseEvent.Wait();
                    if (_stopRequested)
                    {
                        stopReason = PlaybackStopReason.UserStopped;
                        stopMessage = "用户手动停止";
                        break;
                    }
                    long pausedDurationTicks = Stopwatch.GetTimestamp() - pauseStartTimestamp;
                    baseTimestamp += pausedDurationTicks;
                    i--;
                    continue;
                }

                // Send the batch
                if (!TrySendBatch(batch.Actions))
                    throw new InvalidOperationException("键盘输入发送失败，请检查游戏窗口与输入权限。");
                holdGuard.RecordSent(batch.Actions, Stopwatch.GetTimestamp());

                // Progress update
                long nowTicks = Stopwatch.GetTimestamp();
                if (nowTicks - lastUiUpdateTimestamp >= uiUpdateIntervalTicks)
                {
                    lastUiUpdateTimestamp = nowTicks;
                    long currentUs = ((nowTicks - baseTimestamp) * 1_000_000) / frequency;
                    OnProgressChanged?.Invoke(Math.Min(currentUs, totalDurationUs), totalDurationUs);
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
            // Always release all keys safely on completion, stop or exception
            TrySendBatch(CreateReleaseActions(preset, batches));

            lock (_stateLock)
            {
                IsPlaying = false;
                IsPaused = false;
                _currentReleaseActions = Array.Empty<KeyAction>();
            }

            OnPlaybackStopped?.Invoke(stopReason, stopMessage);
        }
    }

    private bool TrySendBatch(IReadOnlyList<KeyAction> actions) =>
        BatchSender?.Invoke(actions) ?? KeyboardSender.TrySendBatch(actions);

    private static IReadOnlyList<KeyAction> CreateReleaseActions(KeyPreset preset, IReadOnlyList<InputBatch>? batches = null) => preset.Keys
        .Select(mapping => (mapping.ScanCode, mapping.Extended))
        .Concat((batches ?? Array.Empty<InputBatch>())
            .SelectMany(batch => batch.Actions)
            .Select(action => (action.ScanCode, action.Extended)))
        .Distinct()
        .Select(key => new KeyAction(0, key.ScanCode, isDown: false, extended: key.Extended))
        .DistinctBy(action => (action.ScanCode, action.Extended))
        .ToArray();
}
