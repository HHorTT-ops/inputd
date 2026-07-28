using System.Drawing.Drawing2D;

namespace InputExecutor.Dashboard;

internal sealed class CardPanel : Panel
{
    internal CardPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Padding = new Padding(16);
        Margin = new Padding(6);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rectangle = new RectangleF(1, 1, Width - 3, Height - 3);
        using var path = DashboardTheme.Rounded(rectangle, 12);
        using var shadow = new SolidBrush(Color.FromArgb(20, 31, 45, 61));
        using var fill = new SolidBrush(Color.White);
        e.Graphics.TranslateTransform(0, 1);
        e.Graphics.FillPath(shadow, path);
        e.Graphics.TranslateTransform(0, -1);
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(DashboardTheme.Border, 1);
        e.Graphics.DrawPath(border, path);
    }
}
