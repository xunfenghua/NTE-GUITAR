using System.Runtime.InteropServices;

namespace MidiAutoPlayer.Input;

public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WH_KEYBOARD_LL = 13;
    private const int DefaultHotkeyId = 0xBEEF;

    private readonly int _hotkeyId;
    private readonly IntPtr _hWnd;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly SynchronizationContext? _synchronizationContext;
    private uint _fallbackVirtualKey;
    private IntPtr _keyboardHook;
    private bool _fallbackKeyDown;
    private bool _registered;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelKeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    public event Action? HotkeyPressed;

    /// <summary>Windows error from the most recent failed RegisterHotKey attempt.</summary>
    public int RegistrationErrorCode { get; private set; }

    /// <summary>True when the low-level keyboard hook is handling this hotkey.</summary>
    public bool IsUsingKeyboardHookFallback => _keyboardHook != IntPtr.Zero;

    public GlobalHotkey(IntPtr hWnd, int hotkeyId = DefaultHotkeyId)
    {
        _hWnd = hWnd;
        _hotkeyId = hotkeyId;
        _keyboardProc = HandleKeyboardHook;
        _synchronizationContext = SynchronizationContext.Current;
    }

    public bool Register(uint vk, uint fsModifiers = 0)
    {
        Unregister();
        RegistrationErrorCode = 0;
        _registered = RegisterHotKey(_hWnd, _hotkeyId, fsModifiers, vk);
        if (_registered) return true;

        RegistrationErrorCode = Marshal.GetLastWin32Error();

        // RegisterHotKey can fail when another process owns the same key.
        // Keep the key usable through a non-blocking global keyboard hook.
        if (fsModifiers != 0 || vk == 0) return false;
        _fallbackVirtualKey = vk;
        _fallbackKeyDown = false;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, IntPtr.Zero, 0);
        return _keyboardHook != IntPtr.Zero;
    }

    public void Unregister()
    {
        if (_registered && _hWnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hWnd, _hotkeyId);
            _registered = false;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        _fallbackVirtualKey = 0;
        _fallbackKeyDown = false;
    }

    private IntPtr HandleKeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _fallbackVirtualKey != 0)
        {
            int message = wParam.ToInt32();
            var keyData = Marshal.PtrToStructure<LowLevelKeyboardData>(lParam);
            if (keyData.VirtualKey == _fallbackVirtualKey)
            {
                if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    if (!_fallbackKeyDown)
                    {
                        _fallbackKeyDown = true;
                        if (_synchronizationContext is not null)
                        {
                            _synchronizationContext.Post(_ => HotkeyPressed?.Invoke(), null);
                        }
                        else
                        {
                            HotkeyPressed?.Invoke();
                        }
                    }
                }
                else if (message is WM_KEYUP or WM_SYSKEYUP)
                {
                    _fallbackKeyDown = false;
                }
            }
        }

        // Do not consume input: the game and other applications still receive the key.
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    public bool ProcessMessage(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == _hotkeyId)
        {
            HotkeyPressed?.Invoke();
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        Unregister();
    }
}
