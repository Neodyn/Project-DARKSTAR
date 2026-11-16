using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Darkstar.Wheel;

/// <summary>
/// The wheel itself: a borderless window over DCS that never takes the focus and lets every click
/// through, drawn with per-pixel transparency. It shows; it does not listen - keys and buttons are
/// read by <see cref="WheelApp"/>, because DCS keeps the focus the whole time.
///
/// DCS has to run windowed or borderless (the default) for anything to be drawn over it; an
/// exclusive full-screen DCS covers every other window, this one included.
/// </summary>
internal sealed class WheelWindow : Form
{
    private static readonly Color Accent = Color.FromArgb(242, 169, 59);    // the editor's amber
    private static readonly Color Segment = Color.FromArgb(16, 22, 26);
    private static readonly Color Hub = Color.FromArgb(10, 13, 16);
    private static readonly Color Ink = Color.FromArgb(231, 237, 239);
    private static readonly Color Dim = Color.FromArgb(140, 160, 168);
    private static readonly Color Dark = Color.FromArgb(18, 18, 18);

    private readonly IReadOnlyList<CallDefinition> _slices;
    private string _callsign = "";
    private string _hint = "";
    private string? _status;

    public WheelWindow(IReadOnlyList<CallDefinition> slices)
    {
        _slices = slices;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
    }

    /// <summary>The slice the hat or the arrow keys point at, or null for none.</summary>
    public int? Highlighted { get; private set; }

    // Never activated, never hit-tested: DCS keeps the keyboard, the mouse and the focus.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                          Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>Shows the wheel centred in <paramref name="area"/>, nothing highlighted.</summary>
    public void Open(Rectangle area, double share, string callsign, string hint)
    {
        var size = Math.Max(240, (int)(area.Height * share));
        Bounds = new Rectangle(area.X + (area.Width - size) / 2, area.Y + (area.Height - size) / 2, size, size);

        _callsign = callsign;
        _hint = hint;
        _status = null;
        Highlighted = null;

        if (!Visible) Show();
        Render();
    }

    public void CloseWheel()
    {
        _status = null;
        Highlighted = null;
        Hide();
    }

    public void Highlight(int? slice)
    {
        Highlighted = slice is >= 0 && slice < _slices.Count ? slice : null;
        Render();
    }

    /// <summary>A line in the hub, e.g. "Sent: Picture", shown until the wheel closes.</summary>
    public void ShowStatus(string status)
    {
        _status = status;
        Render();
    }

    private void Render()
    {
        if (!Visible || Width <= 0) return;

        var size = Width;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(96, 96);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            Draw(g, size);
        }

        var screenDc = Native.GetDC(IntPtr.Zero);
        var memoryDc = Native.CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var previous = Native.SelectObject(memoryDc, hBitmap);

        try
        {
            var destination = new Native.POINT { X = Left, Y = Top };
            var extent = new Native.SIZE { CX = size, CY = size };
            var source = new Native.POINT();
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };

            Native.UpdateLayeredWindow(Handle, screenDc, ref destination, ref extent, memoryDc, ref source,
                0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            Native.SelectObject(memoryDc, previous);
            Native.DeleteObject(hBitmap);
            Native.DeleteDC(memoryDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static PointF Polar(float centre, float radius, double degrees) => new(
        centre + radius * (float)Math.Cos(degrees * Math.PI / 180),
        centre + radius * (float)Math.Sin(degrees * Math.PI / 180));

    /// <summary>
    /// A ring of slices, the first at the top and the rest clockwise - the same order the hat uses
    /// (<see cref="RadioWheel.SliceForHat"/>), so "up" on the hat is slice 1.
    /// </summary>
    private void Draw(Graphics g, int size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float centre = size / 2f, outer = size * 0.49f, inner = size * 0.21f, textRadius = (outer + inner) / 2;
        var count = _slices.Count;
        var sweep = 360f / count;

        using var labelFont = new Font("Segoe UI", size / 30f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var numberFont = new Font("Segoe UI", size / 42f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var hubFont = new Font("Segoe UI", size / 24f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", size / 52f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var edge = new Pen(Color.FromArgb(110, 255, 255, 255), 1.2f);
        using var accentEdge = new Pen(Accent, 2f);

        for (var i = 0; i < count; i++)
        {
            var start = i * sweep - 90 - sweep / 2;
            var marked = Highlighted == i;

            using (var path = new GraphicsPath())
            {
                path.AddArc(centre - outer, centre - outer, 2 * outer, 2 * outer, start + 0.8f, sweep - 1.6f);
                path.AddArc(centre - inner, centre - inner, 2 * inner, 2 * inner, start + sweep - 1.6f, -(sweep - 3.2f));
                path.CloseFigure();

                using var fill = new SolidBrush(marked ? Color.FromArgb(230, Accent) : Color.FromArgb(225, Segment));
                g.FillPath(fill, path);
                g.DrawPath(marked ? accentEdge : edge, path);
            }

            var at = Polar(centre, textRadius, i * sweep - 90);
            var width = textRadius * (float)Math.Min(0.85, sweep * Math.PI / 180 * 0.95);
            var lineHeight = labelFont.GetHeight(g);

            using var ink = new SolidBrush(marked ? Dark : Ink);
            using var dim = new SolidBrush(marked ? Dark : Accent);

            g.DrawString((i + 1).ToString(), numberFont, dim,
                new RectangleF(at.X - width / 2, at.Y - lineHeight * 1.5f, width, lineHeight), centred);
            g.DrawString(_slices[i].MenuLabel, labelFont, ink,
                new RectangleF(at.X - width / 2, at.Y - lineHeight * 0.6f, width, lineHeight * 2.2f), centred);
        }

        // The hub: who the wheel talks to, and how to drive it - or what just happened.
        var hubRadius = inner - 6;
        using (var hub = new SolidBrush(Color.FromArgb(245, Hub)))
            g.FillEllipse(hub, centre - hubRadius, centre - hubRadius, 2 * hubRadius, 2 * hubRadius);
        using (var ring = new Pen(Color.FromArgb(200, Accent), 2.5f))
            g.DrawEllipse(ring, centre - hubRadius, centre - hubRadius, 2 * hubRadius, 2 * hubRadius);

        using var white = new SolidBrush(Ink);
        using var amber = new SolidBrush(Accent);
        using var grey = new SolidBrush(Dim);

        var box = new RectangleF(centre - hubRadius * 0.9f, centre - hubRadius * 0.55f, hubRadius * 1.8f, hubRadius * 0.6f);
        g.DrawString(_callsign.ToUpperInvariant(), hubFont, white, box, centred);

        var lower = new RectangleF(centre - hubRadius * 0.9f, centre + hubRadius * 0.1f, hubRadius * 1.8f, hubRadius * 0.6f);
        if (_status != null)
            g.DrawString(_status, numberFont, amber, lower, centred);
        else
            g.DrawString(_hint, smallFont, grey, lower, centred);
    }
}
