using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MidiAutoPlayer.UI;

namespace MidiAutoPlayer.Controls;

public class RoundedPanel : Panel
{
    public int CornerRadius { get; set; } = 10;
    public Color FillColor { get; set; } = Theme.Surface;
    public Color BorderColor { get; set; } = Theme.Border;
    public float BorderWidth { get; set; } = 1f;

    public RoundedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = ClientRectangle;
        if (rect.Width <= 2 || rect.Height <= 2) return;

        rect.Width -= 1;
        rect.Height -= 1;

        using var path = Theme.CreateRoundRect(rect, CornerRadius);
        using var fill = new SolidBrush(FillColor);
        e.Graphics.FillPath(fill, path);

        if (BorderWidth > 0 && BorderColor != Color.Transparent)
        {
            using var pen = new Pen(BorderColor, BorderWidth);
            e.Graphics.DrawPath(pen, path);
        }
    }
}
