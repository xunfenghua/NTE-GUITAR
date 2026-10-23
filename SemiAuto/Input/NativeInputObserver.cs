using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using Nte.GuitarChart.Core;

namespace MidiAutoPlayer.SemiAuto.Input;

public sealed class NativeInputObserver : IUserInputObserver, IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public event EventHandler<InputObservation>? InputObserved;
    public event Action<long>? PositionSwitchKeyPressed;
    public event Action<int, int, bool>? KeyStateChanged;
    public event Action<int, int, int>? LaneKeyCaptured;

    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;
    private readonly ConcurrentDictionary<int, byte> _downKeys = new();
    private readonly ConcurrentDictionary<int, byte> _captureSuppressedKeys = new();
    private readonly ConcurrentDictionary<int, byte> _immersionCaptureKeys = new();
    private readonly BlockingCollection<Action> _dispatchQueue = new(new ConcurrentQueue<Action>());
    private readonly Thread _dispatchThread;
    private PhysicalKeyBinding[] _gameLaneBindings = GameLaneKeyBinding.CreateDefaultGameLanes().ToArray();
    private int _captureLane;
    private int _captureImmersionHotkey;

    public IReadOnlyList<PhysicalKeyBinding> GameLaneBindings => Volatile.Read(ref _gameLaneBindings);
    public event Action<int, int, bool>? ImmersionHotkeyCaptured;

    public void BeginImmersionHotkeyCapture() => Interlocked.Exchange(ref _captureImmersionHotkey, 1);
    public void CancelImmersionHotkeyCapture() => Interlocked.Exchange(ref _captureImmersionHotkey, 0);

    public void SetGameLaneBindings(IReadOnlyList<PhysicalKeyBinding> bindings)
    {
        if (bindings.Count != 6 || !GameLaneKeyBinding.AreUnique(bindings))
            throw new ArgumentException("Exactly six unique game lane key bindings are required.", nameof(bindings));
        Volatile.Write(ref _gameLaneBindings, bindings.Select(k => new PhysicalKeyBinding(k.VirtualKey, k.ScanCode)).ToArray());
    }

    public bool BeginLaneKeyCapture(int lane)
    {
        if (lane is < 1 or > 7) return false;
        Volatile.Write(ref _captureLane, lane);
        return true;
    }

    public void CancelLaneKeyCapture() => Volatile.Write(ref _captureLane, 0);

    public int PositionSwitchVk { get; set; } = PositionSwitchKeyBinding.DefaultVirtualKey;
    public int PositionSwitchScanCode { get; set; }
    public bool MatchesPositionSwitchKey(int virtualKey, int scanCode) =>
        PositionSwitchScanCode > 0 ? PositionSwitchScanCode == scanCode : PositionSwitchVk == virtualKey;

    public NativeInputObserver()
    {
        _proc = HookCallback;
        _dispatchThread = new Thread(DispatchLoop) { IsBackground = true, Name = "NTE_InputDispatch" };
        _dispatchThread.Start();
    }

    public bool Start()
    {
        if (_hookId != IntPtr.Zero) return true;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule?.ModuleName), 0);
        return _hookId != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _downKeys.Clear();
        _captureSuppressedKeys.Clear();
        _immersionCaptureKeys.Clear();
        CancelImmersionHotkeyCapture();
        CancelLaneKeyCapture();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        bool suppressCapturedInput = false;
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool isDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
            bool isUp = (msg == WM_KEYUP || msg == WM_SYSKEYUP);

            if (isDown || isUp)
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                // Ignore SendInput events generated by this app (or other injectors).
                // They must not enter the physical-input state/capture pipeline.
                if ((kb.flags & LLKHF_INJECTED) != 0)
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);

                int vk = (int)kb.vkCode;
                long wallClockUs = Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;

                bool firstDown = isDown && _downKeys.TryAdd(-vk, 0);
                bool isModifier = IsModifierKey(vk);
                bool captureImmersionKey = firstDown && !isModifier
                    && Volatile.Read(ref _captureImmersionHotkey) != 0
                    && Interlocked.Exchange(ref _captureImmersionHotkey, 0) != 0;
                if (captureImmersionKey)
                {
                    bool hasModifiers = IsAnyModifierDown();
                    int scanCode = (int)kb.scanCode;
                    _immersionCaptureKeys.TryAdd(vk, 0);
                    _dispatchQueue.TryAdd(() => ImmersionHotkeyCaptured?.Invoke(vk, scanCode, hasModifiers));
                }

                bool isImmersionCaptureKey = _immersionCaptureKeys.ContainsKey(vk);
                int captureLane = Volatile.Read(ref _captureLane);
                if (!isImmersionCaptureKey && firstDown && captureLane is >= 1 and <= 7)
                {
                    _captureSuppressedKeys.TryAdd(vk, 0);
                    int scanCode = (int)kb.scanCode;
                    _dispatchQueue.TryAdd(() => LaneKeyCaptured?.Invoke(captureLane, vk, scanCode));
                }
                bool suppressForCapture = !isImmersionCaptureKey && _captureSuppressedKeys.ContainsKey(vk);
                if (isImmersionCaptureKey)
                {
                    if (isUp)
                    {
                        _immersionCaptureKeys.TryRemove(vk, out _);
                        _downKeys.TryRemove(-vk, out _);
                    }
                }
                else if (suppressForCapture)
                {
                    suppressCapturedInput = true;
                    if (isUp)
                    {
                        _captureSuppressedKeys.TryRemove(vk, out _);
                        _downKeys.TryRemove(-vk, out _);
                    }
                }
                else
                {
                    if (firstDown)
                    {
                        _dispatchQueue.TryAdd(() => KeyStateChanged?.Invoke(vk, (int)kb.scanCode, true));
                    }
                    else if (isUp)
                    {
                        _downKeys.TryRemove(-vk, out _);
                        _dispatchQueue.TryAdd(() => KeyStateChanged?.Invoke(vk, (int)kb.scanCode, false));
                    }

                    // Check position switch key
                    if (firstDown && MatchesPositionSwitchKey(vk, (int)kb.scanCode))
                    {
                        _dispatchQueue.TryAdd(() => PositionSwitchKeyPressed?.Invoke(wallClockUs));
                    }

                    int gameKey = GameLaneKeyBinding.MapPhysicalKeyToGameLane(vk, (int)kb.scanCode, GameLaneBindings);
                    if (gameKey >= 1 && gameKey <= 6)
                    {
                        bool isRepeat = isDown && !_downKeys.TryAdd(gameKey, 0);
                        if (isUp) _downKeys.TryRemove(gameKey, out _);
                        var observation = new InputObservation(gameKey, isDown, wallClockUs, isRepeat);
                        _dispatchQueue.TryAdd(() => InputObserved?.Invoke(this, observation));
                    }
                }
            }
        }

        if (suppressCapturedInput) return (IntPtr)1;
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool IsAnyModifierDown() =>
        _downKeys.ContainsKey(-0x10) || _downKeys.ContainsKey(-0x11) || _downKeys.ContainsKey(-0x12)
        || _downKeys.ContainsKey(-0x5B) || _downKeys.ContainsKey(-0x5C)
        || _downKeys.ContainsKey(-0xA0) || _downKeys.ContainsKey(-0xA1)
        || _downKeys.ContainsKey(-0xA2) || _downKeys.ContainsKey(-0xA3)
        || _downKeys.ContainsKey(-0xA4) || _downKeys.ContainsKey(-0xA5);

    private static bool IsModifierKey(int virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private void DispatchLoop()
    {
        foreach (var action in _dispatchQueue.GetConsumingEnumerable())
        {
            try { action(); } catch { /* An observer callback must never terminate input dispatch. */ }
        }
    }

    public static int MapVirtualKeyToGameLane(int vk, int scanCode)
    {
        // 1..6 on top number row (0x31..0x36)
        if (vk >= 0x31 && vk <= 0x36)
            return vk - 0x30;

        // 1..6 on Numpad (0x61..0x66)
        if (vk >= 0x61 && vk <= 0x66)
            return vk - 0x60;

        return scanCode switch
        {
            0x4F => 1, 0x50 => 2, 0x51 => 3,
            0x4B => 4, 0x4C => 5, 0x4D => 6,
            _ => 0
        };
    }

    public static int MapConfiguredKeyToGameLane(int vk, int scanCode, IReadOnlyList<PhysicalKeyBinding> bindings) =>
        GameLaneKeyBinding.MapPhysicalKeyToGameLane(vk, scanCode, bindings);

    public void Dispose()
    {
        Stop();
        _dispatchQueue.CompleteAdding();
        if (_dispatchThread.IsAlive && _dispatchThread != Thread.CurrentThread) _dispatchThread.Join(500);
        _dispatchQueue.Dispose();
    }
}
