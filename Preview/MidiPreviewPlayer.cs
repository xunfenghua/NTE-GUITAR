using System.Diagnostics;
using System.Runtime.InteropServices;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Preview;

public sealed class MidiPreviewPlayer : IDisposable
{
    [DllImport("winmm.dll")]
    private static extern int midiOutOpen(out IntPtr lphMidiOut, uint uDeviceID, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll")]
    private static extern int midiOutClose(IntPtr hMidiOut);

    [DllImport("winmm.dll")]
    private static extern int midiOutShortMsg(IntPtr hMidiOut, uint dwMsg);

    [DllImport("winmm.dll")]
    private static extern int midiOutReset(IntPtr hMidiOut);

    private const uint MIDI_MAPPER = unchecked((uint)-1);

    private static readonly object s_deviceLock = new();
    private static IntPtr s_sharedMidiOut = IntPtr.Zero;
    private static int s_instanceCount = 0;
    private static MidiPreviewPlayer? s_activePlayer = null;

    private readonly object _lock = new();

    private Thread? _workerThread;
    private volatile bool _stopRequested;
    private volatile bool _isPaused;

    public bool IsAvailable
    {
        get
        {
            lock (s_deviceLock)
            {
                return s_sharedMidiOut != IntPtr.Zero;
            }
        }
    }

    public bool IsPlaying => _workerThread != null && _workerThread.IsAlive && !_isPaused;
    public bool IsPaused => _isPaused;

    public long CurrentPositionUs { get; private set; }
    public long TotalDurationUs { get; private set; }

    public event Action<long, long>? OnProgress;
    public event Action? OnPlaybackCompleted;

    private readonly SynchronizationContext? _syncContext;

    public MidiPreviewPlayer()
    {
        _syncContext = SynchronizationContext.Current;
        lock (s_deviceLock)
        {
            s_instanceCount++;
            EnsureDeviceOpenLocked();
        }
    }

    private static void EnsureDeviceOpenLocked()
    {
        if (s_sharedMidiOut == IntPtr.Zero)
        {
            try
            {
                int res = midiOutOpen(out s_sharedMidiOut, MIDI_MAPPER, IntPtr.Zero, IntPtr.Zero, 0);
                if (res != 0)
                {
                    s_sharedMidiOut = IntPtr.Zero;
                }
            }
            catch
            {
                s_sharedMidiOut = IntPtr.Zero;
            }
        }
    }

    public void Play(IReadOnlyList<RawMidiNote> rawNotes, long startOffsetUs = 0, long? explicitTotalDurationUs = null)
    {
        lock (s_deviceLock)
        {
            EnsureDeviceOpenLocked();
            if (s_sharedMidiOut == IntPtr.Zero || rawNotes.Count == 0) return;

            if (s_activePlayer != null && s_activePlayer != this)
            {
                try
                {
                    s_activePlayer.Stop();
                }
                catch { }
            }
            s_activePlayer = this;
        }

        Stop();

        // Build preview NoteOn/NoteOff timeline
        var events = new List<(long TimeUs, uint Msg)>();
        long maxDuration = 0;

        foreach (var n in rawNotes)
        {
            if (n.Velocity == 0) continue;

            uint noteOn = (uint)(0x90 | (n.Channel & 0x0F) | (n.NoteNumber << 8) | (n.Velocity << 16));
            uint noteOff = (uint)(0x80 | (n.Channel & 0x0F) | (n.NoteNumber << 8));

            events.Add((n.StartUs, noteOn));
            long endUs = n.StartUs + Math.Max(n.DurationUs, 30_000);
            events.Add((endUs, noteOff));

            if (endUs > maxDuration) maxDuration = endUs;
        }

        // Sort events by TimeUs ascending; for same timestamp, turn off previous note before turning on next
        events.Sort((a, b) =>
        {
            int cmp = a.TimeUs.CompareTo(b.TimeUs);
            if (cmp != 0) return cmp;
            byte statusA = (byte)(a.Msg & 0xF0);
            byte statusB = (byte)(b.Msg & 0xF0);
            return statusA.CompareTo(statusB);
        });

        long finalTotal = (explicitTotalDurationUs.HasValue && explicitTotalDurationUs.Value > maxDuration)
            ? explicitTotalDurationUs.Value
            : maxDuration;

        TotalDurationUs = finalTotal;
        CurrentPositionUs = Math.Clamp(startOffsetUs, 0, finalTotal);
        _stopRequested = false;
        _isPaused = false;

        _workerThread = new Thread(() => PlaybackWorker(events, startOffsetUs, finalTotal))
        {
            IsBackground = true,
            Name = "MidiPreviewPlayer_Thread"
        };
        _workerThread.Start();
    }

    public void Pause()
    {
        _isPaused = true;
        SilenceAll();
    }

    public void Resume()
    {
        _isPaused = false;
    }

    public void Stop()
    {
        _stopRequested = true;
        _isPaused = false;

        if (_workerThread != null && _workerThread.IsAlive)
        {
            _workerThread.Join(200);
            _workerThread = null;
        }

        SilenceAll();
    }

    public void SilenceAll()
    {
        lock (s_deviceLock)
        {
            if (s_sharedMidiOut == IntPtr.Zero) return;

            try
            {
                midiOutReset(s_sharedMidiOut);
                for (byte ch = 0; ch < 16; ch++)
                {
                    // CC 123 (All Notes Off)
                    midiOutShortMsg(s_sharedMidiOut, (uint)(0xB0 | ch | (123 << 8)));
                    // CC 120 (All Sound Off)
                    midiOutShortMsg(s_sharedMidiOut, (uint)(0xB0 | ch | (120 << 8)));
                }
            }
            catch { }
        }
    }

    private void PlaybackWorker(List<(long TimeUs, uint Msg)> events, long startOffsetUs, long totalDurationUs)
    {
        int eventIdx = 0;
        // Skip events before startOffsetUs
        while (eventIdx < events.Count && events[eventIdx].TimeUs < startOffsetUs)
        {
            eventIdx++;
        }

        var sw = Stopwatch.StartNew();
        long baseUs = startOffsetUs;
        long lastProgressReportSwMs = 0;

        while (!_stopRequested && eventIdx < events.Count)
        {
            if (_isPaused)
            {
                Thread.Sleep(20);
                sw.Restart();
                baseUs = CurrentPositionUs;
                continue;
            }

            long currentUs = baseUs + (sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
            CurrentPositionUs = currentUs;

            // Fire progress every ~40ms
            if (sw.ElapsedMilliseconds - lastProgressReportSwMs >= 40)
            {
                lastProgressReportSwMs = sw.ElapsedMilliseconds;
                NotifyProgress(currentUs, totalDurationUs);
            }

            // Play due events
            while (eventIdx < events.Count && events[eventIdx].TimeUs <= currentUs)
            {
                var ev = events[eventIdx++];
                lock (s_deviceLock)
                {
                    if (s_sharedMidiOut != IntPtr.Zero && !_stopRequested && !_isPaused)
                    {
                        midiOutShortMsg(s_sharedMidiOut, ev.Msg);
                    }
                }
            }

            // High precision hybrid wait to next event
            if (eventIdx < events.Count)
            {
                long nextEventUs = events[eventIdx].TimeUs;
                long waitUs = nextEventUs - currentUs;

                if (waitUs > 3000)
                {
                    Thread.Sleep(1);
                }
                else if (waitUs > 0)
                {
                    Thread.Yield();
                }
            }
        }

        SilenceAll();

        if (!_stopRequested)
        {
            CurrentPositionUs = totalDurationUs;
            NotifyProgress(totalDurationUs, totalDurationUs);
            NotifyCompleted();
        }
    }

    private void NotifyProgress(long curUs, long totUs)
    {
        if (OnProgress == null) return;
        if (_syncContext != null)
        {
            _syncContext.Post(_ => OnProgress?.Invoke(curUs, totUs), null);
        }
        else
        {
            OnProgress?.Invoke(curUs, totUs);
        }
    }

    private void NotifyCompleted()
    {
        if (OnPlaybackCompleted == null) return;
        if (_syncContext != null)
        {
            _syncContext.Post(_ => OnPlaybackCompleted?.Invoke(), null);
        }
        else
        {
            OnPlaybackCompleted?.Invoke();
        }
    }

    public void Dispose()
    {
        Stop();
        lock (s_deviceLock)
        {
            if (s_activePlayer == this)
            {
                s_activePlayer = null;
            }
            s_instanceCount--;
            if (s_instanceCount <= 0)
            {
                if (s_sharedMidiOut != IntPtr.Zero)
                {
                    try
                    {
                        midiOutReset(s_sharedMidiOut);
                        midiOutClose(s_sharedMidiOut);
                    }
                    catch { }
                    s_sharedMidiOut = IntPtr.Zero;
                }
                s_instanceCount = 0;
            }
        }
    }
}
