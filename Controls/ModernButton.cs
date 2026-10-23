using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MidiAutoPlayer.UI;

namespace MidiAutoPlayer.Controls;

public class ModernButton : Button
{
    private bool _isHovered;
    private bool _isPressed;

    public int CornerRadius { get; set; } = 8;
    public bool Primary { get; set; }
    public Color AccentColor { get; set; } = Theme.Accent;
    public Color? CustomNormalColor { get; set; }
    public Color? CustomBorderColor { get; set; }
    public Color? CustomTextColor { get; set; }

    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Font = Theme.Body(9.5f, FontStyle.Regular);
        Cursor = Cursors.Hand;
        Height = 36;
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isHovered = false;
        _isPressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _isPressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _isPressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = ClientRectangle;
        if (rect.Width <= 2 || rect.Height <= 2) return;
        rect.Width -= 1;
        rect.Height -= 1;

        Color bg;
        Color fg = CustomTextColor ?? (Primary ? Color.White : Theme.Text);
        Color border = CustomBorderColor ?? (Primary ? Theme.Accent : Theme.Border);

        if (!Enabled)
        {
            bg = Color.FromArgb(35, 37, 44);
            fg = Theme.Muted;
            border = Color.FromArgb(48, 50, 60);
        }
        else if (Primary)
        {
            bg = _isPressed ? Theme.AccentPressed : (_isHovered ? Theme.AccentHover : AccentColor);
            border = bg;
        }
        else if (CustomNormalColor.HasValue)
        {
            var baseColor = CustomNormalColor.Value;
            bg = _isPressed ? ControlPaint.Dark(baseColor, 0.15f)
                : (_isHovered ? ControlPaint.Light(baseColor, 0.1f) : baseColor);
        }
        else
        {
            bg = _isPressed ? Theme.SurfaceActive : (_isHovered ? Theme.SurfaceHover : Theme.Surface);
        }

        using var path = Theme.CreateRoundRect(rect, CornerRadius);
        using var brush = new SolidBrush(bg);
        g.FillPath(brush, path);

        using (var pen = new Pen(border, 1f))
        {
            g.DrawPath(pen, path);
        }

        // Draw Text
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            ClientRectangle,
            fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
        );
    }
}
