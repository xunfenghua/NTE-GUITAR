using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MidiAutoPlayer.UI;

namespace MidiAutoPlayer.Controls;

public enum BadgeStatus
{
    Success,
    Warning,
    Error,
    Info,
    Unknown
}

public class StatusBadge : Control
{
    private BadgeStatus _status = BadgeStatus.Unknown;
    private string _badgeText = "未检测";

    public BadgeStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            Invalidate();
        }
    }

    public string BadgeText
    {
        get => _badgeText;
        set
        {
            _badgeText = value;
            Invalidate();
        }
    }

    public StatusBadge()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        Font = Theme.Small(8.5f, FontStyle.Regular);
        Size = new Size(130, 26);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = ClientRectangle;
        if (rect.Width <= 2 || rect.Height <= 2) return;
        rect.Width -= 1;
        rect.Height -= 1;

        Color dotColor;
        Color bgColor;
        Color borderColor;

        switch (_status)
        {
            case BadgeStatus.Success:
                dotColor = Theme.Success;
                bgColor = Color.FromArgb(20, 92, 190, 139);
                borderColor = Color.FromArgb(70, 92, 190, 139);
                break;
            case BadgeStatus.Warning:
                dotColor = Theme.Warning;
                bgColor = Color.FromArgb(20, 239, 185, 87);
                borderColor = Color.FromArgb(70, 239, 185, 87);
                break;
            case BadgeStatus.Error:
                dotColor = Theme.Error;
                bgColor = Color.FromArgb(25, 231, 105, 115);
                borderColor = Color.FromArgb(80, 231, 105, 115);
                break;
            case BadgeStatus.Info:
                dotColor = Theme.Accent;
                bgColor = Color.FromArgb(25, 126, 111, 235);
                borderColor = Color.FromArgb(70, 126, 111, 235);
                break;
            case BadgeStatus.Unknown:
            default:
                dotColor = Theme.Muted;
                bgColor = Color.FromArgb(25, 163, 166, 180);
                borderColor = Color.FromArgb(50, 163, 166, 180);
                break;
        }

        int radius = rect.Height / 2;
        using (var path = Theme.CreateRoundRect(rect, radius))
        {
            using var bgBrush = new SolidBrush(bgColor);
            g.FillPath(bgBrush, path);

            using var borderPen = new Pen(borderColor, 1f);
            g.DrawPath(borderPen, path);
        }

        // Draw Dot
        int dotSize = 7;
        int dotY = (rect.Height - dotSize) / 2;
        int dotX = 9;
        using (var dotBrush = new SolidBrush(dotColor))
        {
            g.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
        }

        // Draw Text
        var textRect = new Rectangle(dotX + dotSize + 6, 0, rect.Width - (dotX + dotSize + 10), rect.Height);
        TextRenderer.DrawText(
            g,
            _badgeText,
            Font,
            textRect,
            Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
        );
    }
}
