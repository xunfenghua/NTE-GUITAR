using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.WinForms;
using MidiAutoPlayer.Input;

namespace Nte.GuitarChart.WinForms;

/// <summary>
/// Top-most translucent overlay window for semi-auto guitar playing.
/// Supports no-activate focus protection, DPI scaling, and non-destructive click-through toggling.
/// </summary>
public sealed class OverlayWindow : Form
{
    private const int GWL_EXSTYLE = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : (IntPtr)GetWindowLong32(hWnd, nIndex);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : (IntPtr)SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32());

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private GlobalKeyObserver? _immersionHotkey;
    private int _immersionHotkeyVk;
    private int _overlayOpacity = 92;
    private bool _clickThrough;
    private bool _immersive;
    private Color _normalBackColor;
    private bool _constrainingBounds;
    private bool _nativeDragActive;
    private string? _activeScreenDeviceName;
    private bool _wideLayout;

    public OverlayWindow(bool wideLayout = false)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        _wideLayout = wideLayout;
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens.FirstOrDefault();
        if (screen != null) ApplyLayoutBounds(screen, alignRight: true);
        else ApplyLayoutBounds(new Rectangle(0, 0, 1920, 1080), alignRight: true);
        BackColor = Color.FromArgb(14, 18, 28);
        _normalBackColor = BackColor;
        _webView.DefaultBackgroundColor = Color.Transparent;
        Controls.Add(_webView);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow | WsExNoActivate | WsExLayered;
            if (_clickThrough || _immersive) cp.ExStyle |= WsExTransparent | WsExLayered;
            return cp;
        }
    }

    public WebView2 WebView => _webView;
    public bool IsWideLayout => _wideLayout;
    public bool IsImmersive => _immersive;
    public event Action? ImmersionHotkeyPressed;

    public bool TrySetImmersionHotkey(int virtualKey)
    {
        if (InvokeRequired)
        {
            bool? result = null;
            Invoke((Action)(() => result = TrySetImmersionHotkey(virtualKey)));
            return result == true;
        }

        var candidate = new GlobalKeyObserver(virtualKey);
        candidate.KeyPressed += _ => ImmersionHotkeyPressed?.Invoke();
        if (!candidate.Start())
        {
            candidate.Dispose();
            return false;
        }
        var previous = _immersionHotkey;
        _immersionHotkey = candidate;
        _immersionHotkeyVk = virtualKey;
        previous?.Dispose();
        return true;
    }

    public int ImmersionHotkeyVk => _immersionHotkeyVk;

    public void SetImmersive(bool enabled)
    {
        if (InvokeRequired)
        {
            BeginInvoke((Action)(() => SetImmersive(enabled)));
            return;
        }
        if (_immersive == enabled) return;
        _immersive = enabled;
        if (enabled)
        {
            _normalBackColor = BackColor;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            Opacity = 1d;
        }
        else
        {
            TransparencyKey = Color.Empty;
            BackColor = _normalBackColor;
            Opacity = _overlayOpacity / 100d;
        }
        ApplyClickThroughStyle();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _immersionHotkey?.Dispose();
            _immersionHotkey = null;
        }
        base.Dispose(disposing);
    }

    public void SetWideLayout(bool wideLayout)
    {
        if (_wideLayout == wideLayout && IsHandleCreated) return;
        _wideLayout = wideLayout;
        var screen = Screen.FromRectangle(Bounds);
        _constrainingBounds = true;
        try { ApplyLayoutBounds(screen, alignRight: !IsHandleCreated); }
        finally { _constrainingBounds = false; }
    }

    private void ApplyLayoutBounds(Screen screen, bool alignRight)
    {
        _activeScreenDeviceName = screen.DeviceName;
        ApplyLayoutBounds(screen.WorkingArea, alignRight);
    }

    private void ApplyLayoutBounds(Rectangle workArea, bool alignRight)
    {
        int availableWidth = Math.Max(1, workArea.Width - 24);
        int availableHeight = Math.Max(1, workArea.Height - 24);
        double dpiScale = Math.Max(1d, DeviceDpi / 96d);
        int width, height;
        if (_wideLayout)
        {
            int minWidth = Math.Min((int)Math.Round(760 * dpiScale), availableWidth);
            int maxWidth = Math.Min((int)Math.Round(1400 * dpiScale), availableWidth);
            width = Math.Clamp((int)Math.Round(workArea.Width * .72), minWidth, Math.Max(minWidth, maxWidth));
            int minHeight = Math.Min((int)Math.Round(520 * dpiScale), availableHeight);
            int maxHeight = Math.Min((int)Math.Round(850 * dpiScale), availableHeight);
            height = Math.Clamp((int)Math.Round(workArea.Height * .66), minHeight, Math.Max(minHeight, maxHeight));
            MinimumSize = new Size(width, Math.Min(height, minHeight));
            MaximumSize = new Size(width, availableHeight);
        }
        else
        {
            height = Math.Min(availableHeight, Math.Clamp((int)Math.Round(workArea.Height * .86), (int)Math.Round(620 * dpiScale), (int)Math.Round(1360 * dpiScale)));
            int minWidth = Math.Min((int)Math.Round(380 * dpiScale), availableWidth);
            int maxWidth = Math.Min((int)Math.Round(500 * dpiScale), availableWidth);
            width = Math.Clamp((int)Math.Round(height * .52), minWidth, Math.Max(minWidth, maxWidth));
            // Keep the portrait layout narrow while allowing its existing vertical sizing behavior.
            MinimumSize = new Size(width, Math.Min(560, availableHeight));
            MaximumSize = new Size(width, availableHeight);
        }
        int x = alignRight ? workArea.Right - width - 12 : Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        int y = alignRight
            ? (_wideLayout ? workArea.Top + 12 : workArea.Top + (workArea.Height - height) / 2)
            : Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        Bounds = new Rectangle(x, y, width, height);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_constrainingBounds || _nativeDragActive || !IsHandleCreated) return;
        ConstrainToCurrentScreen();
    }

    private void ConstrainToCurrentScreen(Screen? targetScreen = null)
    {
        if (_constrainingBounds || !IsHandleCreated) return;
        // During a cross-monitor native drag, the release point is the clearest
        // indication of the user's intended destination (a straddling window
        // can otherwise be assigned to the monitor with the largest overlap).
        var screen = targetScreen ?? Screen.FromRectangle(Bounds);
        if (_activeScreenDeviceName != screen.DeviceName)
        {
            _constrainingBounds = true;
            try { ApplyLayoutBounds(screen, alignRight: false); }
            finally { _constrainingBounds = false; }
            return;
        }
        var work = screen.WorkingArea;
        int width = Math.Min(Width, work.Width), height = Math.Min(Height, work.Height);
        int x = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - width));
        int y = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - height));
        if (x == Left && y == Top && width == Width && height == Height) return;
        _constrainingBounds = true;
        try { Bounds = new Rectangle(x, y, width, height); }
        finally { _constrainingBounds = false; }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        var screen = Screen.FromRectangle(Bounds);
        _constrainingBounds = true;
        try { ApplyLayoutBounds(screen, alignRight: false); }
        finally { _constrainingBounds = false; }
    }

    public bool BeginNativeDrag(bool locked)
    {
        if (locked || !IsHandleCreated) return false;
        ReleaseCapture();
        _nativeDragActive = true;
        try { SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero); }
        finally
        {
            _nativeDragActive = false;
            ConstrainToCurrentScreen(Screen.FromPoint(Cursor.Position));
        }
        return true;
    }

    public int SetOverlayOpacity(int percent)
    {
        percent = Math.Clamp(percent, 60, 100);
        _overlayOpacity = percent;
        if (!_immersive) Opacity = percent / 100d;
        return percent;
    }

    /// <summary>
    /// Enable pass-through while playing to prevent mouse interception.
    /// Uses Win32 SetWindowLongPtr to dynamically modify styles without disrupting WebView2 lifecycle.
    /// </summary>
    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        ApplyClickThroughStyle();
    }

    private void ApplyClickThroughStyle()
    {
        if (IsHandleCreated)
        {
            try
            {
                var curStyle = (long)GetWindowLongPtr(Handle, GWL_EXSTYLE);
                if (_clickThrough || _immersive)
                {
                    curStyle |= (WsExTransparent | WsExLayered);
                }
                else
                {
                    curStyle &= ~WsExTransparent;
                }
                SetWindowLongPtr(Handle, GWL_EXSTYLE, (IntPtr)curStyle);
                SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            }
            catch
            {
                // Fallback to RecreateHandle if SetWindowLongPtr is unsupported
                RecreateHandle();
            }
        }
    }
}
