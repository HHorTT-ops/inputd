using System.Drawing.Drawing2D;

namespace InputExecutor.Dashboard;

internal sealed class MouseView : Control
{
    private HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);
    private int _dx;
    private int _dy;
    private int _wheelX;
    private int _wheelY;
    private Point _cursor;

    internal MouseView()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        MinimumSize = new Size(330, 330);
    }

    internal void UpdateState(IEnumerable<string> held, int dx, int dy, int wheelX, int wheelY, Point cursor)
    {
        _held = new HashSet<string>(held, StringComparer.OrdinalIgnoreCase);
        if (dx != 0 || dy != 0)
        {
            _dx = dx;
            _dy = dy;
        }
        if (wheelX != 0 || wheelY != 0)
        {
            _wheelX = wheelX;
            _wheelY = wheelY;
        }
        _cursor = cursor;
        Invalidate();
    }

    internal void ClearState(Point cursor)
    {
        _held.Clear();
        _dx = _dy = _wheelX = _wheelY = 0;
        _cursor = cursor;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var centerX = ClientSize.Width / 2f;
        var mouse = new RectangleF(centerX - 90, 30, 180, 238);
        using var outline = DashboardTheme.Rounded(mouse, 72);
        using var body = new SolidBrush(Color.FromArgb(250, 251, 253));
        using var border = new Pen(DashboardTheme.Ink, 3);
        e.Graphics.FillPath(body, outline);
        e.Graphics.DrawPath(border, outline);

        var left = new RectangleF(mouse.Left + 3, mouse.Top + 3, mouse.Width / 2 - 5, 92);
        var right = new RectangleF(centerX + 2, mouse.Top + 3, mouse.Width / 2 - 5, 92);
        DrawButton(e.Graphics, left, "left", "LEFT");
        DrawButton(e.Graphics, right, "right", "RIGHT");
        using var divider = new Pen(DashboardTheme.Border, 2);
        e.Graphics.DrawLine(divider, centerX, mouse.Top + 4, centerX, mouse.Top + 98);
        e.Graphics.DrawLine(divider, mouse.Left + 3, mouse.Top + 99, mouse.Right - 3, mouse.Top + 99);

        var wheelRect = new RectangleF(centerX - 12, mouse.Top + 34, 24, 55);
        using var wheelPath = DashboardTheme.Rounded(wheelRect, 10);
        using var wheelFill = new SolidBrush(_held.Contains("middle") ? Color.Black : Color.White);
        using var wheelBorder = new Pen(DashboardTheme.Ink, 2);
        e.Graphics.FillPath(wheelFill, wheelPath);
        e.Graphics.DrawPath(wheelBorder, wheelPath);

        var arrowOrigin = new PointF(centerX, mouse.Bottom - 72);
        var scale = Math.Min(48f, MathF.Sqrt(_dx * _dx + _dy * _dy));
        if (scale > 0)
        {
            var magnitude = MathF.Max(1, MathF.Sqrt(_dx * _dx + _dy * _dy));
            var end = new PointF(arrowOrigin.X + _dx / magnitude * scale, arrowOrigin.Y + _dy / magnitude * scale);
            using var arrow = new Pen(DashboardTheme.Accent, 4) { EndCap = LineCap.ArrowAnchor };
            e.Graphics.DrawLine(arrow, arrowOrigin, end);
        }

        using var valueFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        using var smallFont = new Font("Segoe UI", 9f);
        var infoTop = (int)mouse.Bottom + 18;
        TextRenderer.DrawText(e.Graphics, $"位置  {_cursor.X}, {_cursor.Y}", valueFont, new Rectangle(10, infoTop, ClientSize.Width - 20, 24), DashboardTheme.Ink, TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(e.Graphics, $"移动  ΔX {_dx:+#;-#;0}   ΔY {_dy:+#;-#;0}", smallFont, new Rectangle(10, infoTop + 28, ClientSize.Width - 20, 22), DashboardTheme.Muted, TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(e.Graphics, $"滚轮  X {_wheelX:+#;-#;0}   Y {_wheelY:+#;-#;0}", smallFont, new Rectangle(10, infoTop + 50, ClientSize.Width - 20, 22), DashboardTheme.Muted, TextFormatFlags.HorizontalCenter);
    }

    private void DrawButton(Graphics graphics, RectangleF rectangle, string code, string label)
    {
        using var path = DashboardTheme.Rounded(rectangle, 35);
        using var fill = new SolidBrush(_held.Contains(code) ? Color.Black : Color.White);
        graphics.FillPath(fill, path);
        using var font = new Font("Segoe UI", 8f, FontStyle.Bold);
        TextRenderer.DrawText(graphics, label, font, Rectangle.Round(rectangle), _held.Contains(code) ? Color.White : DashboardTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
    }
}
