using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MidiAutoPlayer.UI;

public sealed record TopMostStateDto(bool Enabled);

public partial class MainForm : Form
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);
    }

    private bool _isTopMostNoActivate;

    public void InitializeTopMostPolicy()
    {
        if (_config.AlwaysOnTop)
        {
            ApplyTopMostPolicy(true);
        }
    }

    public TopMostStateDto SetTopMost(bool enabled)
    {
        if (InvokeRequired)
        {
            return (TopMostStateDto)Invoke(new Func<bool, TopMostStateDto>(SetTopMost), enabled);
        }

        ApplyTopMostPolicy(enabled);

        _config.AlwaysOnTop = enabled;
        SaveConfig();

        var dto = new TopMostStateDto(_isTopMostNoActivate);
        try
        {
            _eventPublisher.Publish("window.topMostChanged", dto);
        }
        catch { }

        return dto;
    }

    public TopMostStateDto GetTopMostState()
    {
        return new TopMostStateDto(_isTopMostNoActivate);
    }

    private void ApplyTopMostPolicy(bool enabled)
    {
        _isTopMostNoActivate = enabled;

        if (IsHandleCreated)
        {
            IntPtr exStyle = GetWindowLongPtr(Handle, GWL_EXSTYLE);
            long style = exStyle.ToInt64();

            if (enabled)
            {
                // 添加 WS_EX_NOACTIVATE（不抢占焦点）与 WS_EX_TOPMOST（顶层显示）
                style |= WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(style));

                TopMost = true;
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            else
            {
                // 移除 WS_EX_NOACTIVATE 与 WS_EX_TOPMOST
                style &= ~WS_EX_NOACTIVATE;
                style &= ~WS_EX_TOPMOST;
                SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(style));

                TopMost = false;
                SetWindowPos(Handle, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }
        else
        {
            TopMost = enabled;
        }
    }

    private bool HandleTopMostWndProc(ref Message m)
    {
        // 关键点：当启用无焦点防争夺置顶模式时，拦截 WM_MOUSEACTIVATE 并返回 MA_NOACTIVATE
        // 效果：鼠标点击本窗口内的任何区域（包括按钮/网页操作）都不会激活本窗口为 Windows 前台窗口，
        // 正在运行的游戏将始终保持前台活动状态与输入焦点，绝对不丢失鼠标焦点！
        if (_isTopMostNoActivate && m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return true;
        }

        return false;
    }
}
