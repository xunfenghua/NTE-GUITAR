using System.Runtime.InteropServices;
using System.Text;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Input;

public static class KeyboardSender
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VSC_TO_VK_EX = 3;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEKEYBDHARDWAREINPUT mkhi;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct MOUSEKEYBDHARDWAREINPUT
    {
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public static string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "(None)";
        var sb = new StringBuilder(256);
        if (GetWindowText(hWnd, sb, sb.Capacity) > 0)
            return sb.ToString();
        return "(Unnamed Window)";
    }

    public static bool IsForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        return GetForegroundWindow() == hWnd;
    }

    public static bool IsForeground(IntPtr hWnd) => IsForegroundWindow(hWnd);

    public static void SendBatch(IReadOnlyList<KeyAction> actions)
    {
        TrySendBatch(actions);
    }

    public static bool TrySendBatch(IReadOnlyList<KeyAction> actions)
    {
        if (actions == null || actions.Count == 0) return true;

        var inputs = new INPUT[actions.Count];
        int structSize = Marshal.SizeOf<INPUT>();

        for (int i = 0; i < actions.Count; i++)
        {
            var act = actions[i];
            uint flags = KEYEVENTF_SCANCODE;
            if (!act.IsDown) flags |= KEYEVENTF_KEYUP;
            if (act.Extended) flags |= KEYEVENTF_EXTENDEDKEY;

            inputs[i] = new INPUT
            {
                type = INPUT_KEYBOARD,
                mkhi = new MOUSEKEYBDHARDWAREINPUT
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = act.ScanCode,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        return SendInput((uint)inputs.Length, inputs, structSize) == (uint)inputs.Length;
    }

    public static bool TryPostBatch(IntPtr hWnd, IReadOnlyList<KeyAction> actions)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (actions == null || actions.Count == 0) return true;

        foreach (KeyAction action in actions)
        {
            uint scanCode = action.ScanCode | (action.Extended ? 0xE000u : 0u);
            uint virtualKey = MapVirtualKey(scanCode, MAPVK_VSC_TO_VK_EX);
            if (virtualKey == 0) return false;

            uint lParam = 1u | ((uint)action.ScanCode << 16);
            if (action.Extended) lParam |= 1u << 24;
            if (!action.IsDown) lParam |= (1u << 30) | (1u << 31);

            uint message = action.IsDown ? WM_KEYDOWN : WM_KEYUP;
            if (!PostMessage(hWnd, message, (IntPtr)virtualKey, (IntPtr)(long)lParam))
                return false;
        }

        return true;
    }

    public static void ReleaseAllKeys(KeyPreset? preset = null)
    {
        var keysToRelease = new HashSet<(ushort ScanCode, bool Extended)>();

        if (preset != null)
        {
            foreach (var mapping in preset.Keys)
            {
                keysToRelease.Add((mapping.ScanCode, mapping.Extended));
            }
        }
        else
        {
            foreach (var item in PresetManager.DefaultMappingData)
            {
                keysToRelease.Add((item.ScanCode, item.Ext));
            }
        }

        var actions = new List<KeyAction>(keysToRelease.Count);
        foreach (var (scanCode, extended) in keysToRelease)
        {
            actions.Add(new KeyAction(0, scanCode, isDown: false, extended: extended));
        }

        SendBatch(actions);
    }
}
