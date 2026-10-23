using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MidiAutoPlayer.UI;

namespace MidiAutoPlayer.Controls;

public class FlatTrackBar : Control
{
    private int _minimum = 0;
    private int _maximum = 1000;
    private int _value = 0;
    private bool _isDragging = false;
    private bool _isHovered = false;

    public int Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            if (_value < _minimum) _value = _minimum;
            Invalidate();
        }
    }

    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(_minimum + 1, value);
            if (_value > _maximum) _value = _maximum;
            Invalidate();
        }
    }

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, _minimum, _maximum);
            if (_value != clamped)
            {
                _value = clamped;
                ValueChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }
    }

    public bool IsDragging => _isDragging;

    public event EventHandler? ValueChanged;
    public event EventHandler? Scroll;

    public FlatTrackBar()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        Height = 24;
        Cursor = Cursors.Hand;
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
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _isDragging = true;
            UpdateValueFromMouse(e.X);
            Scroll?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging && e.Button == MouseButtons.Left)
        {
            UpdateValueFromMouse(e.X);
            Scroll?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isDragging)
        {
            _isDragging = false;
            Invalidate();
        }
    }

    private void UpdateValueFromMouse(int mouseX)
    {
        int thumbRadius = 6;
        int trackLeft = thumbRadius;
        int trackWidth = Width - (thumbRadius * 2);

        if (trackWidth <= 0) return;

        float ratio = (float)(mouseX - trackLeft) / trackWidth;
        ratio = Math.Clamp(ratio, 0f, 1f);
        Value = _minimum + (int)Math.Round(ratio * (_maximum - _minimum));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int thumbRadius = _isHovered || _isDragging ? 7 : 5;
        int trackHeight = 4;
        int trackY = (Height - trackHeight) / 2;
        int trackLeft = 8;
        int trackWidth = Width - 16;

        if (trackWidth <= 0) return;

        float ratio = (_maximum > _minimum) ? (float)(_value - _minimum) / (_maximum - _minimum) : 0f;
        int fillWidth = (int)Math.Round(ratio * trackWidth);

        // Track Background Groove
        var trackRect = new Rectangle(trackLeft, trackY, trackWidth, trackHeight);
        using (var groovePath = Theme.CreateRoundRect(trackRect, 2))
        using (var grooveBrush = new SolidBrush(Color.FromArgb(50, 53, 64)))
        {
            g.FillPath(grooveBrush, groovePath);
        }

        // Active Filled Progress
        if (fillWidth > 0)
        {
            var fillRect = new Rectangle(trackLeft, trackY, fillWidth, trackHeight);
            using var fillPath = Theme.CreateRoundRect(fillRect, 2);
            using var fillBrush = new SolidBrush(Theme.Accent);
            g.FillPath(fillBrush, fillPath);
        }

        // Thumb Handle
        int thumbX = trackLeft + fillWidth;
        int thumbY = Height / 2;
        using (var thumbBrush = new SolidBrush(Color.White))
        using (var thumbBorderPen = new Pen(_isDragging ? Theme.AccentHover : Theme.Accent, 2f))
        {
            g.FillEllipse(thumbBrush, thumbX - thumbRadius, thumbY - thumbRadius, thumbRadius * 2, thumbRadius * 2);
            g.DrawEllipse(thumbBorderPen, thumbX - thumbRadius, thumbY - thumbRadius, thumbRadius * 2, thumbRadius * 2);
        }
    }
}
