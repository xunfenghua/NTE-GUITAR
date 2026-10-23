using System.Drawing;
using System.Runtime.InteropServices;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Input;

public static class MouseSender
{
    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

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
        public MOUSEINPUT mi;
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

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

    public static Point GetCurrentCursorPos()
    {
        GetCursorPos(out var pt);
        return new Point(pt.X, pt.Y);
    }

    /// <summary>
    /// 读取窗口在屏幕上的客户区原点、尺寸以及外边框，支持负坐标副屏
    /// </summary>
    public static bool GetWindowScreenInfo(IntPtr hWnd, out Point clientOrigin, out Size clientSize, out Rectangle windowRect)
    {
        clientOrigin = Point.Empty;
        clientSize = Size.Empty;
        windowRect = Rectangle.Empty;

        if (hWnd == IntPtr.Zero) return false;

        if (GetClientRect(hWnd, out var cr))
        {
            clientSize = new Size(cr.Width, cr.Height);
        }

        var pt = new POINT(0, 0);
        if (ClientToScreen(hWnd, ref pt))
        {
            clientOrigin = new Point(pt.X, pt.Y);
        }

        if (GetWindowRect(hWnd, out var wr))
        {
            windowRect = new Rectangle(wr.Left, wr.Top, wr.Width, wr.Height);
        }

        return clientSize.Width > 0 && clientSize.Height > 0;
    }

    /// <summary>
    /// 移动鼠标到指定屏幕物理坐标 (支持主屏、副屏、负坐标扩展屏以及 DirectInput/RawInput 游戏)
    /// </summary>
    public static bool SetPos(int screenX, int screenY)
    {
        bool cursorMoved = SetCursorPos(screenX, screenY);

        // 同步发送绝对虚拟桌面移动事件，确保跨屏 (多显示器/负坐标) 以及 DirectInput/RawInput 游戏窗口完美响应
        int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        bool inputMoved = false;
        if (vWidth > 1 && vHeight > 1)
        {
            int normX = (int)Math.Round((screenX - vLeft) * 65535.0 / (vWidth - 1));
            int normY = (int)Math.Round((screenY - vTop) * 65535.0 / (vHeight - 1));

            var inputs = new INPUT[1];
            inputs[0] = new INPUT
            {
                type = INPUT_MOUSE,
                mkhi = new MOUSEKEYBDHARDWAREINPUT
                {
                    mi = new MOUSEINPUT
                    {
                        dx = normX,
                        dy = normY,
                        mouseData = 0,
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            inputMoved = SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
        }

        return cursorMoved && inputMoved;
    }

    public static bool SendMouseClick(MouseButton button, int holdMs = 25)
    {
        bool downSent = SendButtonAction(button, isDown: true);
        Thread.Sleep(Math.Clamp(holdMs, 5, 80));
        bool upSent = SendButtonAction(button, isDown: false);
        return downSent && upSent;
    }

    public static bool SendButtonAction(MouseButton button, bool isDown)
    {
        uint flag = button switch
        {
            MouseButton.Right => isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            _ => isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
        };

        var inputs = new INPUT[1];
        inputs[0] = new INPUT
        {
            type = INPUT_MOUSE,
            mkhi = new MOUSEKEYBDHARDWAREINPUT
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = flag,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        return SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
    }

    /// <summary>
    /// 将归一化客户区相对坐标 (nx, ny) 转换为屏幕物理坐标 (支持主屏、副屏、负坐标扩展屏)
    /// </summary>
    public static Point NormalizedClientToScreen(IntPtr hWnd, double nx, double ny)
    {
        if (hWnd == IntPtr.Zero) return Point.Empty;

        if (!GetClientRect(hWnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return Point.Empty;

        var origin = new POINT(0, 0);
        if (!ClientToScreen(hWnd, ref origin))
            return Point.Empty;

        int screenX = origin.X + (int)Math.Round(nx * rect.Width);
        int screenY = origin.Y + (int)Math.Round(ny * rect.Height);

        return new Point(screenX, screenY);
    }

    /// <summary>
    /// 将屏幕物理坐标转换为归一化客户区相对坐标 (nx, ny)
    /// 若 clamp=false 且点击点在窗口外部，则安全返回 null，防止异常 0.0000 录入
    /// </summary>
    public static (double nx, double ny)? ScreenToNormalizedClient(IntPtr hWnd, Point screenPoint, bool clamp = false)
    {
        if (hWnd == IntPtr.Zero) return null;

        if (!GetClientRect(hWnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            return null;

        var origin = new POINT(0, 0);
        if (!ClientToScreen(hWnd, ref origin))
            return null;

        int clientX = screenPoint.X - origin.X;
        int clientY = screenPoint.Y - origin.Y;

        if (!clamp)
        {
            if (clientX < 0 || clientX >= rect.Width || clientY < 0 || clientY >= rect.Height)
                return null;
        }

        double nx = Math.Clamp(clientX / (double)rect.Width, 0.0, 1.0);
        double ny = Math.Clamp(clientY / (double)rect.Height, 0.0, 1.0);
        return (nx, ny);
    }
}
