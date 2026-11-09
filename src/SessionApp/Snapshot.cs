using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SessionApp;

/// <summary>
/// What <c>--snapshot</c> asked for: where the picture goes, which element, which view, and
/// optionally a search to narrow the list to the row the slice is about.
///
/// Changelog slices kept failing to get taken. Capturing the window from outside
/// (<c>PrintWindow</c>) gets the title bar and a white client area whenever nothing is
/// composing the desktop — the screen locked, the display off, the operator at work — which is
/// exactly when an agent is doing a release. This draws the window from inside with
/// <see cref="RenderTargetBitmap"/>, WPF's software renderer, which needs no screen at all.
///
/// A snapshot instance is a <c>--multi</c> one placed far off-screen and never activated: it
/// scans, takes one live tick, draws, and exits. No keeper, no heartbeat, no sweep — it must
/// change nothing on the machine it is photographing.
/// </summary>
public sealed record SnapshotRequest(string Path, string? Element, bool Projects, string? Search = null)
{
    public static SnapshotRequest? From(string[] args)
    {
        var path = ValueAfter(args, "--snapshot");
        if (path is null) return null;
        return new SnapshotRequest(
            System.IO.Path.GetFullPath(path),
            ValueAfter(args, "--element"),
            string.Equals(ValueAfter(args, "--view"), "projects", StringComparison.OrdinalIgnoreCase),
            ValueAfter(args, "--search"));
    }

    private static string? ValueAfter(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// Draw <paramref name="target"/> into a PNG at the window's DPI, over the window's own
    /// background — an element with a transparent background would otherwise come out on
    /// nothing, which reads as black in most viewers.
    /// </summary>
    public static void Save(FrameworkElement target, Brush? background, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(target);
        var size = new Size(target.ActualWidth, target.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var rect = new Rect(size);
            dc.DrawRectangle(background ?? Brushes.White, null, rect);
            dc.DrawRectangle(new VisualBrush(target) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, rect);
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(size.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
