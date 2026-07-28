using System.Drawing.Drawing2D;

namespace InputExecutor.Dashboard;

internal static class DashboardTheme
{
    internal static readonly Color Window = Color.FromArgb(242, 245, 249);
    internal static readonly Color Ink = Color.FromArgb(20, 29, 45);
    internal static readonly Color Muted = Color.FromArgb(100, 113, 133);
    internal static readonly Color Border = Color.FromArgb(215, 222, 232);
    internal static readonly Color Accent = Color.FromArgb(42, 101, 242);
    internal static readonly Color Success = Color.FromArgb(22, 163, 74);
    internal static readonly Color Danger = Color.FromArgb(220, 38, 38);
    internal static readonly Color Header = Color.FromArgb(15, 23, 42);

    internal static GraphicsPath Rounded(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static Button Button(string text, Color color)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(126, 40),
            BackColor = color,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(5, 0, 5, 0)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}
