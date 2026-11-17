using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Darkstar.Wheel;

/// <summary>
/// The wheel itself: a borderless window over DCS that never takes the focus and lets every click
/// through, drawn with per-pixel transparency. It shows; it does not listen - keys and buttons are
/// read by <see cref="WheelApp"/>, because DCS keeps the focus the whole time.
///
/// The look: compact, a picture and a short word per slice, the slices coloured by job (tactical
/// amber, airfield blue, radio grey) with a rim that groups them, and the hub showing what a
/// highlighted slice will send before it is sent.
///
/// DCS has to run windowed or borderless (the default) for anything to be drawn over it; an
/// exclusive full-screen DCS covers every other window, this one included.
/// </summary>
internal sealed class WheelWindow : Form
{
    private static readonly Color Tactical = Color.FromArgb(242, 169, 59);   // amber
    private static readonly Color Airfield = Color.FromArgb(78, 155, 224);   // blue, darker than the amber
    private static readonly Color Radio = Color.FromArgb(184, 196, 202);     // grey
    private static readonly Color Segment = Color.FromArgb(16, 22, 26);
    private static readonly Color Hub = Color.FromArgb(10, 13, 16);
    private static readonly Color Ink = Color.FromArgb(231, 237, 239);
    private static readonly Color Dim = Color.FromArgb(140, 160, 168);
    private static readonly Color Dark = Color.FromArgb(10, 13, 16);

    private IReadOnlyList<WheelItem> _items = Array.Empty<WheelItem>();
    private string _callsign = "";
    private string _marker = "»";
    private string? _title;
    private string _hint = "";
    private string? _status;

    public WheelWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
    }

    /// <summary>The slice the hat or the arrow keys point at, or null for none.</summary>
    public int? Highlighted { get; private set; }

    /// <summary>The sub-wheel's title, or null at the top.</summary>
    public string? Title => _title;

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

    /// <summary>Shows the wheel centred in <paramref name="area"/>.</summary>
    public void Open(Rectangle area, double share, WheelSettings settings, IReadOnlyList<WheelItem> items)
    {
        var size = Math.Max(260, (int)(area.Height * share));
        Bounds = new Rectangle(area.X + (area.Width - size) / 2, area.Y + (area.Height - size) / 2, size, size);

        _callsign = settings.Callsign;
        _marker = settings.Marker;
        if (!Visible) Show();
        ShowLevel(items, null);
    }

    /// <summary>Switches to a level of the wheel - the top, or a sub-wheel with its title in the hub.</summary>
    public void ShowLevel(IReadOnlyList<WheelItem> items, string? title)
    {
        _items = items;
        _title = title;
        _status = null;
        Highlighted = null;
        _hint = title == null
            ? $"1-{items.Count} · Enter · Esc"
            : $"1-{items.Count} · ⌫ back · Esc";
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
        Highlighted = slice is >= 0 && slice < _items.Count ? slice : null;
        Render();
    }

    /// <summary>A line in the hub, e.g. "Sent: Picture", shown until the wheel closes.</summary>
    public void ShowStatus(string status)
    {
        _status = status;
        Render();
    }

    private static Color ColourOf(WheelGroup group) => group switch
    {
        WheelGroup.Airfield => Airfield,
        WheelGroup.Radio => Radio,
        _ => Tactical,
    };

    // Dark ink on amber and grey, white on the blue: each passes 4.5:1 on its own fill.
    private static Color InkOn(WheelGroup group) => group == WheelGroup.Airfield ? Color.White : Dark;

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

    /// <summary>A ring segment from <paramref name="from"/> to <paramref name="to"/> degrees, clockwise.</summary>
    private static GraphicsPath Band(float centre, float outer, float inner, float from, float to)
    {
        var path = new GraphicsPath();
        path.AddArc(centre - outer, centre - outer, 2 * outer, 2 * outer, from, to - from);
        path.AddArc(centre - inner, centre - inner, 2 * inner, 2 * inner, to, -(to - from));
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Slices clockwise from the top - the same order the hat uses (<see cref="RadioWheel.SliceForHat"/>),
    /// so "up" on the hat is slice 1.
    /// </summary>
    private void Draw(Graphics g, int size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var count = Math.Max(1, _items.Count);
        float centre = size / 2f;
        float outer = size * 0.40f, inner = size * 0.18f;
        float rimOuter = size * 0.432f, rimInner = size * 0.418f;
        float badgeRadius = size * 0.468f, badgeSize = size * 0.026f;
        float iconRadius = (outer + inner) / 2;
        var sweep = 360f / count;

        using var labelFont = new Font("Segoe UI", size / 40f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var bigFont = new Font("Segoe UI", size / 22f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var badgeFont = new Font("Segoe UI", size / 44f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var hubFont = new Font("Segoe UI", size / 28f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var lineFont = new Font("Segoe UI", size / 42f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", size / 56f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var edge = new Pen(Color.FromArgb(90, 255, 255, 255), 1.2f);

        // The rim: one band per run of neighbouring slices of the same job.
        var runStart = 0;
        for (var i = 1; i <= _items.Count; i++)
        {
            if (i < _items.Count && _items[i].Group == _items[runStart].Group) continue;

            var from = runStart * sweep - 90 - sweep / 2 + 1.5f;
            var to = i * sweep - 90 - sweep / 2 - 1.5f;
            using (var rim = Band(centre, rimOuter, rimInner, from, to))
            using (var rimFill = new SolidBrush(Color.FromArgb(230, ColourOf(_items[runStart].Group))))
                g.FillPath(rimFill, rim);
            runStart = i;
        }

        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var colour = ColourOf(item.Group);
            var marked = Highlighted == i;
            var mid = i * sweep - 90;

            using (var path = Band(centre, outer, inner, mid - sweep / 2 + 0.8f, mid + sweep / 2 - 0.8f))
            using (var fill = new SolidBrush(marked ? Color.FromArgb(235, colour) : Color.FromArgb(225, Segment)))
            using (var border = new Pen(marked ? colour : Color.FromArgb(90, 255, 255, 255), marked ? 2f : 1.2f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            var ink = marked ? InkOn(item.Group) : Ink;
            var at = Polar(centre, iconRadius, mid);
            var width = iconRadius * (float)Math.Min(0.9, sweep * Math.PI / 180 * 0.95);

            using (var inkBrush = new SolidBrush(ink))
            {
                if (item.Icon == WheelIcon.None)
                {
                    // A distance: the number is the whole message, so it gets the room.
                    g.DrawString(item.Label, bigFont, inkBrush,
                        new RectangleF(at.X - width / 2, at.Y - size * 0.04f, width, size * 0.08f), centred);
                }
                else
                {
                    var iconSize = size * 0.06f;
                    using (var pen = new Pen(ink, Math.Max(1.6f, size / 400f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        DrawIcon(g, item.Icon, new PointF(at.X, at.Y - size * 0.022f), iconSize, pen);

                    var label = item.OpensSubWheel ? item.Label.ToUpperInvariant() + " ▸" : item.Label.ToUpperInvariant();
                    g.DrawString(label, labelFont, inkBrush,
                        new RectangleF(at.X - width / 2, at.Y + size * 0.022f, width, size * 0.05f), centred);
                }
            }

            // The number on the rim, outside the slice, where the eye goes for "which key".
            var badge = Polar(centre, badgeRadius, mid);
            using (var badgeFill = new SolidBrush(marked ? colour : Hub))
            using (var badgeEdge = new Pen(colour, 1.5f))
            using (var badgeInk = new SolidBrush(marked ? InkOn(item.Group) : colour))
            {
                g.FillEllipse(badgeFill, badge.X - badgeSize, badge.Y - badgeSize, 2 * badgeSize, 2 * badgeSize);
                g.DrawEllipse(badgeEdge, badge.X - badgeSize, badge.Y - badgeSize, 2 * badgeSize, 2 * badgeSize);
                g.DrawString((i + 1).ToString(), badgeFont, badgeInk,
                    new RectangleF(badge.X - badgeSize, badge.Y - badgeSize, 2 * badgeSize, 2 * badgeSize), centred);
            }
        }

        DrawHub(g, size, centre, inner - 5, hubFont, lineFont, smallFont, centred, edge);
    }

    /// <summary>
    /// The hub: who the wheel talks to, and - while a slice is highlighted - exactly what it is
    /// about to send and what the frequency will hear, so a pick is never a surprise.
    /// </summary>
    private void DrawHub(Graphics g, int size, float centre, float radius, Font hubFont, Font lineFont,
        Font smallFont, StringFormat centred, Pen edge)
    {
        using (var hub = new SolidBrush(Color.FromArgb(245, Hub)))
            g.FillEllipse(hub, centre - radius, centre - radius, 2 * radius, 2 * radius);
        g.DrawEllipse(edge, centre - radius, centre - radius, 2 * radius, 2 * radius);

        using var white = new SolidBrush(Ink);
        using var grey = new SolidBrush(Dim);

        var width = radius * 1.7f;
        RectangleF Row(float offset, float height) =>
            new(centre - width / 2, centre + radius * offset, width, radius * height);

        var title = _title?.ToUpperInvariant() ?? _callsign.ToUpperInvariant();
        g.DrawString(title, hubFont, white, Row(-0.72f, 0.42f), centred);

        if (_status != null)
        {
            var marked = Highlighted is { } s && s < _items.Count ? _items[s] : null;
            using var statusInk = new SolidBrush(marked != null ? ColourOf(marked.Group) : Tactical);
            g.DrawString(_status, lineFont, statusInk, Row(-0.18f, 0.4f), centred);
            return;
        }

        if (Highlighted is { } index && index < _items.Count)
        {
            var item = _items[index];
            using var colour = new SolidBrush(ColourOf(item.Group));

            if (item.OpensSubWheel)
            {
                g.DrawString($"{item.Label} ▸", lineFont, colour, Row(-0.18f, 0.36f), centred);
                g.DrawString("press again to open", smallFont, grey, Row(0.2f, 0.3f), centred);
            }
            else
            {
                g.DrawString($"{_marker} {_callsign}: {item.ChatText}", lineFont, colour, Row(-0.2f, 0.4f), centred);
                g.DrawString($"Pilot: „{_callsign}, …, {item.Spoken}.“", smallFont, grey, Row(0.2f, 0.32f), centred);
            }
            return;
        }

        g.DrawString(_hint, smallFont, grey, Row(-0.05f, 0.3f), centred);
    }

    /// <summary>
    /// The slice pictures, drawn on a 24-unit grid around <paramref name="centre"/> - lines and
    /// circles only, so they stay sharp at any wheel size and need no image files.
    /// </summary>
    private static void DrawIcon(Graphics g, WheelIcon icon, PointF centre, float size, Pen pen)
    {
        var u = size / 24f;
        var ox = centre.X - 12 * u;
        var oy = centre.Y - 12 * u;

        PointF P(float x, float y) => new(ox + x * u, oy + y * u);
        void Line(float x1, float y1, float x2, float y2) => g.DrawLine(pen, P(x1, y1), P(x2, y2));
        void Circle(float cx, float cy, float r) => g.DrawEllipse(pen, ox + (cx - r) * u, oy + (cy - r) * u, 2 * r * u, 2 * r * u);
        void Arc(float cx, float cy, float r, float start, float sweep) =>
            g.DrawArc(pen, ox + (cx - r) * u, oy + (cy - r) * u, 2 * r * u, 2 * r * u, start, sweep);
        void Shape(params float[] xy)
        {
            var points = new PointF[xy.Length / 2];
            for (var k = 0; k < points.Length; k++) points[k] = P(xy[2 * k], xy[2 * k + 1]);
            g.DrawPolygon(pen, points);
        }

        switch (icon)
        {
            case WheelIcon.Crosshair:
                Circle(12, 12, 6);
                Line(12, 3, 12, 8); Line(12, 16, 12, 21); Line(3, 12, 8, 12); Line(16, 12, 21, 12);
                break;
            case WheelIcon.Shield:
                Shape(12, 3, 19, 6, 19, 11, 12, 21, 5, 11, 5, 6);
                Line(12, 8, 12, 13); Line(12, 16, 12, 16.5f);
                break;
            case WheelIcon.Radar:
                Circle(12, 12, 9); Circle(12, 12, 4); Line(12, 12, 18.5f, 5.5f);
                break;
            case WheelIcon.Pin:
                Arc(12, 10, 6, 150, 240);
                Line(6.8f, 13, 12, 21); Line(17.2f, 13, 12, 21);
                Circle(12, 10, 2);
                break;
            case WheelIcon.ThreatCircle:
                Circle(12, 12, 9); Circle(12, 12, 1.5f); Line(13.5f, 12, 21, 12);
                break;
            case WheelIcon.Tower:
                Shape(6, 4, 18, 4, 16, 9, 8, 9);
                Line(10, 9, 10, 21); Line(14, 9, 14, 21); Line(6, 21, 18, 21);
                break;
            case WheelIcon.Runway:
                Line(8, 3, 6, 21); Line(16, 3, 18, 21);
                Line(12, 5, 12, 8); Line(12, 11, 12, 14); Line(12, 17, 12, 20);
                break;
            case WheelIcon.Windsock:
                Line(5, 3, 5, 21);
                Shape(5, 5, 19, 8, 19, 12, 5, 15);
                break;
            case WheelIcon.Turn:
                Line(6, 20, 6, 12); Arc(10, 12, 4, 180, 90); Line(10, 8, 19, 8);
                Line(15, 4, 19, 8); Line(19, 8, 15, 12);
                break;
            case WheelIcon.Radio:
                Line(12, 13, 12, 20); Line(9, 20, 15, 20);
                Arc(12, 13, 4, 225, 90); Arc(12, 13, 7.5f, 225, 90); Circle(12, 13, 0.8f);
                break;
            case WheelIcon.Off:
                Circle(12, 12, 9); Line(5.6f, 5.6f, 18.4f, 18.4f);
                break;
        }
    }
}
