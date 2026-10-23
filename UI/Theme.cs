using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MidiAutoPlayer.UI;

public static class Theme
{
    // ==================== Unified Dark Glass Theme Palette ====================
    public static readonly Color Window = Color.FromArgb(22, 23, 28);
    public static readonly Color Surface = Color.FromArgb(31, 33, 40);
    public static readonly Color SurfaceHover = Color.FromArgb(43, 44, 54);
    public static readonly Color SurfaceActive = Color.FromArgb(50, 52, 64);
    public static readonly Color SurfaceCard = Color.FromArgb(28, 30, 37);
    public static readonly Color Border = Color.FromArgb(57, 59, 70);
    public static readonly Color BorderLight = Color.FromArgb(75, 78, 92);
    public static readonly Color Text = Color.FromArgb(239, 240, 246);
    public static readonly Color Muted = Color.FromArgb(163, 166, 180);
    public static readonly Color Accent = Color.FromArgb(126, 111, 235);
    public static readonly Color AccentHover = Color.FromArgb(144, 131, 242);
    public static readonly Color AccentPressed = Color.FromArgb(110, 95, 215);
    public static readonly Color Success = Color.FromArgb(92, 190, 139);
    public static readonly Color Warning = Color.FromArgb(239, 185, 87);
    public static readonly Color Error = Color.FromArgb(231, 105, 115);
    public static readonly Color Selection = Color.FromArgb(50, 48, 75);

    // ==================== Font Helpers ====================
    private const string FontFamilyName = "Microsoft YaHei UI";

    public static Font Body(float size = 9.5f, FontStyle style = FontStyle.Regular)
        => new(FontFamilyName, size, style, GraphicsUnit.Point);

    public static Font Title(float size = 12f, FontStyle style = FontStyle.Bold)
        => new(FontFamilyName, size, style, GraphicsUnit.Point);

    public static Font Small(float size = 8.5f, FontStyle style = FontStyle.Regular)
        => new(FontFamilyName, size, style, GraphicsUnit.Point);

    public static Font Monospace(float size = 9f, FontStyle style = FontStyle.Regular)
        => new("Consolas", size, style, GraphicsUnit.Point);

    // ==================== GDI+ Helpers ====================
    public static GraphicsPath CreateRoundRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static GraphicsPath CreateRoundRect(RectangleF r, float radius)
    {
        float d = Math.Max(2f, radius * 2f);
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ==================== Win32 DWM Immersive Dark Titlebar ====================
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static void EnableImmersiveDarkMode(IntPtr handle)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) return;

        try
        {
            int darkMode = 1;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // Windows 11 rounded window corners
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int cornerPreference = DWMWCP_ROUND;
                DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
            }
        }
        catch
        {
            // Fallback gracefully if DWM API is unsupported on current OS/display driver
        }
    }
}
