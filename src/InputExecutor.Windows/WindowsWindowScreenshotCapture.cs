using System.Drawing;
using System.Drawing.Imaging;
using InputExecutor.Core.Abstractions;
using InputExecutor.Core.Execution;
using InputExecutor.Core.Protocol;

namespace InputExecutor.Windows;

public sealed class WindowsWindowScreenshotCapture : IWindowScreenshotCapture
{
    public ScreenshotFrame Capture(TargetBinding target)
    {
        if (!WindowsTargetGuard.TryParseHandle(target.WindowHandle, out var hwnd) || hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            throw new InputExecutorException("TARGET_WINDOW_CLOSED", "The target window is not available for capture.", 409);
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            throw new InputExecutorException("SCREENSHOT_FAILED", "GetWindowRect failed.", 500);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width is <= 0 or > 16_384 || height is <= 0 or > 16_384)
            throw new InputExecutorException("INVALID_CAPTURE_BOUNDS", $"Window capture bounds are {width}x{height}.", 409);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return new ScreenshotFrame
        {
            FrameId = Guid.NewGuid().ToString("N"),
            CapturedAtUtc = DateTimeOffset.UtcNow,
            OriginX = rect.Left,
            OriginY = rect.Top,
            Width = width,
            Height = height,
            PngBytes = stream.ToArray()
        };
    }
}
