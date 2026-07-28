using System.Drawing.Drawing2D;

namespace InputExecutor.Dashboard;

internal sealed class KeyboardView : Control
{
    private sealed record KeySpec(string Code, string Label, float Width = 1f);

    private static readonly KeySpec[][] Rows =
    [
        [new("Escape", "Esc", 1.2f), new("F1", "F1"), new("F2", "F2"), new("F3", "F3"), new("F4", "F4"), new("F5", "F5"), new("F6", "F6"), new("F7", "F7"), new("F8", "F8"), new("F9", "F9"), new("F10", "F10"), new("F11", "F11"), new("F12", "F12")],
        [new("Backquote", "`"), new("Digit1", "1"), new("Digit2", "2"), new("Digit3", "3"), new("Digit4", "4"), new("Digit5", "5"), new("Digit6", "6"), new("Digit7", "7"), new("Digit8", "8"), new("Digit9", "9"), new("Digit0", "0"), new("Minus", "-"), new("Equal", "="), new("Backspace", "Backspace", 2f)],
        [new("Tab", "Tab", 1.55f), new("KeyQ", "Q"), new("KeyW", "W"), new("KeyE", "E"), new("KeyR", "R"), new("KeyT", "T"), new("KeyY", "Y"), new("KeyU", "U"), new("KeyI", "I"), new("KeyO", "O"), new("KeyP", "P"), new("BracketLeft", "["), new("BracketRight", "]"), new("Backslash", "\\", 1.45f)],
        [new("CapsLock", "Caps", 1.85f), new("KeyA", "A"), new("KeyS", "S"), new("KeyD", "D"), new("KeyF", "F"), new("KeyG", "G"), new("KeyH", "H"), new("KeyJ", "J"), new("KeyK", "K"), new("KeyL", "L"), new("Semicolon", ";"), new("Quote", "'"), new("Enter", "Enter", 2.15f)],
        [new("ShiftLeft", "Shift", 2.4f), new("KeyZ", "Z"), new("KeyX", "X"), new("KeyC", "C"), new("KeyV", "V"), new("KeyB", "B"), new("KeyN", "N"), new("KeyM", "M"), new("Comma", ","), new("Period", "."), new("Slash", "/"), new("ShiftRight", "Shift", 2.45f)],
        [new("ControlLeft", "Ctrl", 1.35f), new("MetaLeft", "Win", 1.25f), new("AltLeft", "Alt", 1.25f), new("Space", "Space", 6.3f), new("AltRight", "Alt", 1.25f), new("MetaRight", "Win", 1.25f), new("ContextMenu", "Menu", 1.25f), new("ControlRight", "Ctrl", 1.35f)]
    ];

    private HashSet<string> _pressed = new(StringComparer.OrdinalIgnoreCase);

    internal KeyboardView()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        MinimumSize = new Size(700, 330);
    }

    internal void SetPressed(IEnumerable<string> codes)
    {
        _pressed = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var padding = 18f;
        var gap = 7f;
        var availableWidth = ClientSize.Width - padding * 2;
        var rowHeight = Math.Max(42f, (ClientSize.Height - padding * 2 - gap * (Rows.Length - 1)) / Rows.Length);
        using var keyFont = new Font("Segoe UI", Math.Clamp(rowHeight * 0.23f, 9f, 13f), FontStyle.Bold);

        for (var rowIndex = 0; rowIndex < Rows.Length; rowIndex++)
        {
            var row = Rows[rowIndex];
            var units = row.Sum(key => key.Width);
            var unit = (availableWidth - gap * (row.Length - 1)) / units;
            var x = padding;
            var y = padding + rowIndex * (rowHeight + gap);
            foreach (var key in row)
            {
                var width = unit * key.Width;
                var rectangle = new RectangleF(x, y, width, rowHeight);
                var active = _pressed.Contains(key.Code);
                using var path = DashboardTheme.Rounded(rectangle, 7f);
                using var fill = new SolidBrush(active ? Color.Black : Color.FromArgb(252, 253, 255));
                using var border = new Pen(active ? Color.Black : DashboardTheme.Border, active ? 2f : 1.25f);
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
                TextRenderer.DrawText(e.Graphics, key.Label, keyFont, Rectangle.Round(rectangle), active ? Color.White : DashboardTheme.Ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                x += width + gap;
            }
        }
    }
}
