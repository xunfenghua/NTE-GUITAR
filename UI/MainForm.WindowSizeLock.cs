using System;
using System.Drawing;
using System.Windows.Forms;

namespace MidiAutoPlayer.UI;

public sealed record WindowSizeStateDto(bool Locked, int Width, int Height);

public partial class MainForm : Form
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private bool _isSizeLocked;
    private FormBorderStyle _initialBorderStyle;
    private readonly Size _minFreeSize = new(760, 480);
    private readonly Size _defaultWindowSize = new(1080, 700);

    public void InitializeWindowSizingPolicy()
    {
        _initialBorderStyle = FormBorderStyle;
        MinimumSize = _minFreeSize;
        if (WindowState != FormWindowState.Normal)
        {
            WindowState = FormWindowState.Normal;
        }

        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int targetW = Math.Clamp(_defaultWindowSize.Width, _minFreeSize.Width, workArea.Width - 20);
        int targetH = Math.Clamp(_defaultWindowSize.Height, _minFreeSize.Height, workArea.Height - 20);
        Size = new Size(targetW, targetH);
        CenterToScreen();
    }

    public WindowSizeStateDto SetWindowSizeLock(bool locked)
    {
        if (InvokeRequired)
        {
            return (WindowSizeStateDto)Invoke(new Func<bool, WindowSizeStateDto>(SetWindowSizeLock), locked);
        }

        if (locked)
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
            }

            var lockSize = Size;
            MinimumSize = lockSize;
            MaximumSize = lockSize;
            MaximizeBox = false;

            // 仅当原本就是系统原生边框时才切换 FixedSingle；若为 None 无边框则保持 None
            if (_initialBorderStyle != FormBorderStyle.None)
            {
                FormBorderStyle = FormBorderStyle.FixedSingle;
            }
        }
        else
        {
            MaximumSize = Size.Empty;
            MinimumSize = _minFreeSize;
            MaximizeBox = true;

            if (_initialBorderStyle != FormBorderStyle.None)
            {
                FormBorderStyle = FormBorderStyle.Sizable;
            }
        }

        _isSizeLocked = locked;
        var dto = new WindowSizeStateDto(_isSizeLocked, Width, Height);
        try
        {
            _eventPublisher.Publish("window.sizeModeChanged", dto);
        }
        catch { }
        return dto;
    }

    public WindowSizeStateDto RestoreDefaultWindowSize()
    {
        if (InvokeRequired)
        {
            return (WindowSizeStateDto)Invoke(new Func<WindowSizeStateDto>(RestoreDefaultWindowSize));
        }

        if (WindowState != FormWindowState.Normal)
        {
            WindowState = FormWindowState.Normal;
        }

        SetWindowSizeLock(false);

        var workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int targetW = Math.Clamp(_defaultWindowSize.Width, _minFreeSize.Width, workArea.Width - 20);
        int targetH = Math.Clamp(_defaultWindowSize.Height, _minFreeSize.Height, workArea.Height - 20);
        Size = new Size(targetW, targetH);
        CenterToScreen();

        var dto = new WindowSizeStateDto(_isSizeLocked, Width, Height);
        try
        {
            _eventPublisher.Publish("window.sizeModeChanged", dto);
        }
        catch { }
        return dto;
    }

    private void HandleWindowSizeLockWndProc(ref Message m)
    {
        // 若处于“固定窗口”模式，拦截所有 8 个边缘/角落的鼠标缩放命中结果
        if (_isSizeLocked && m.Msg == WM_NCHITTEST)
        {
            int hit = m.Result.ToInt32();
            if (hit >= HTLEFT && hit <= HTBOTTOMRIGHT)
            {
                m.Result = (IntPtr)HTCLIENT;
            }
        }
    }
}
