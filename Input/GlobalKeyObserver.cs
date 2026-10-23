using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MidiAutoPlayer.Input;

/// <summary>
/// Observes one global key without consuming it, so the foreground application still receives it.
/// </summary>
public sealed class GlobalKeyObserver : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;
    private int _virtualKey;
    private int _isDown;

    public event Action<int>? KeyPressed;

    public GlobalKeyObserver(int virtualKey)
    {
        _virtualKey = virtualKey;
        _callback = HookCallback;
    }

    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(module?.ModuleName), 0);
        return _hook != IntPtr.Zero;
    }

    public void SetVirtualKey(int virtualKey)
    {
        Interlocked.Exchange(ref _virtualKey, virtualKey);
        Interlocked.Exchange(ref _isDown, 0);
    }

    private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int messageId = message.ToInt32();
            bool isDown = messageId is WmKeyDown or WmSysKeyDown;
            bool isUp = messageId is WmKeyUp or WmSysKeyUp;
            if (isDown || isUp)
            {
                int virtualKey = (int)Marshal.PtrToStructure<KeyboardHookData>(data).VirtualKey;
                if (virtualKey == Volatile.Read(ref _virtualKey))
                {
                    if (isUp)
                        Interlocked.Exchange(ref _isDown, 0);
                    else if (Interlocked.Exchange(ref _isDown, 1) == 0)
                        KeyPressed?.Invoke(virtualKey);
                }
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
