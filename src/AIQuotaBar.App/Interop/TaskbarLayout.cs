namespace AIQuotaBar.App.Interop;

// Physical screen pixels; only the native host converts to parent client coordinates.
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

internal static class TaskbarLayout
{
    public static PixelRect Place(PixelRect taskbar, PixelRect? tray, double widthDip, double heightDip, double scale)
    {
        var width = Math.Min(taskbar.Width, Math.Max(1, (int)Math.Round(widthDip * scale)));
        var height = Math.Min(taskbar.Height, Math.Max(1, (int)Math.Round(heightDip * scale)));
        var gap = Math.Max(1, (int)Math.Round(6 * scale));
        int x, y;
        if (taskbar.Width >= taskbar.Height)
        {
            x = (tray?.Left ?? taskbar.Right - (int)Math.Round(80 * scale)) - gap - width;
            y = taskbar.Top + (taskbar.Height - height) / 2;
        }
        else
        {
            x = taskbar.Left + (taskbar.Width - width) / 2;
            y = (tray?.Top ?? taskbar.Bottom - (int)Math.Round(80 * scale)) - gap - height;
        }
        x = Math.Clamp(x, taskbar.Left, taskbar.Right - width);
        y = Math.Clamp(y, taskbar.Top, taskbar.Bottom - height);
        return new(x, y, x + width, y + height);
    }

    public static PixelRect PlacePopup(PixelRect anchor, PixelRect work, int width, int height, int gap)
    {
        width = Math.Min(width, work.Width);
        height = Math.Min(height, work.Height);
        var x = anchor.Left + (anchor.Width - width) / 2;
        var y = anchor.Top - height - gap;
        if (y < work.Top) y = anchor.Bottom + gap;
        x = Math.Clamp(x, work.Left, work.Right - width);
        y = Math.Clamp(y, work.Top, work.Bottom - height);
        return new(x, y, x + width, y + height);
    }
}
