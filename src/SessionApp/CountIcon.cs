using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SessionApp;

/// <summary>
/// Draws the tray glyph: the number of sessions still on the hook, in the pink of the
/// app icon's cloud.
///
/// A cloud at 16 pixels says only "Sky is running", which you knew. The number is the
/// one thing worth reading from across the desk, so the tray icon <em>is</em> the number
/// — the same count the window title carries.
///
/// One colour serves both themes. #FF3D8B clears 3:1 against a light taskbar and 4.5:1
/// against a dark one, so unlike the window icon there is no day/night pair to swap and
/// no system-theme key to watch.
///
/// The glyph is scaled to fill its box rather than set at a fixed point size: "7" wants
/// the whole height, "17" is limited by width, and picking one font size for both leaves
/// the single digit small for no reason. The scale stays uniform — squeezing two digits
/// taller buys height at the cost of looking like a different typeface.
/// </summary>
internal static class CountIcon
{
    private static readonly SolidColorBrush Ink = Freeze(Color.FromRgb(0xFF, 0x3D, 0x8B));

    private static readonly Typeface Face = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>
    /// Renders <paramref name="count"/> as an icon the caller owns and must
    /// <see cref="DestroyIcon"/>. Returns <see cref="IntPtr.Zero"/> if GDI refuses.
    /// </summary>
    public static IntPtr Render(int count)
    {
        // The shell asks for a small-icon-sized bitmap and scales anything else. 16 is the
        // floor because a metric of 0 (locked-down policy hives report one) would render nothing.
        int size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));

        // Three glyphs at this size are a smear; the exact number is in the tooltip.
        string label = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);

        var text = new FormattedText(
            label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face,
            emSize: 64, Ink, pixelsPerDip: 1.0);

        var geometry = text.BuildGeometry(new Point(0, 0));
        var ink = geometry.Bounds;
        if (ink.IsEmpty || ink.Width <= 0 || ink.Height <= 0) return IntPtr.Zero;

        double box = size - 1;                       // a hair of margin, so nothing clips
        double scale = Math.Min(box / ink.Width, box / ink.Height);

        var place = new TransformGroup();
        place.Children.Add(new TranslateTransform(-ink.X, -ink.Y));
        place.Children.Add(new ScaleTransform(scale, scale));
        place.Children.Add(new TranslateTransform(
            (size - ink.Width * scale) / 2, (size - ink.Height * scale) / 2));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(place);
            dc.DrawGeometry(Ink, null, geometry);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return ToIcon(bitmap, size);
    }

    /// <summary>
    /// Wraps a rendered bitmap as an HICON. Pbgra32 is premultiplied, which is exactly what
    /// a 32-bit icon's colour plane wants, so the pixels go across untouched.
    /// </summary>
    private static IntPtr ToIcon(RenderTargetBitmap bitmap, int size)
    {
        int stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,          // negative: top-down, the order CopyPixels hands back
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };

        IntPtr colour = CreateDIBSection(IntPtr.Zero, ref header, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (colour == IntPtr.Zero) return IntPtr.Zero;
        Marshal.Copy(pixels, 0, bits, pixels.Length);

        // An all-zero AND mask leaves every pixel of the colour plane opaque, and the
        // plane's own alpha shapes the glyph. Zeroed explicitly: CreateBitmap with a null
        // pointer leaves the bits undefined, which shows up as confetti around the digits.
        int maskStride = (size + 31) / 32 * 4;
        IntPtr mask = CreateBitmap(size, size, 1, 1, new byte[maskStride * size]);

        var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = colour };
        IntPtr icon = CreateIconIndirect(ref info);

        DeleteObject(colour);
        DeleteObject(mask);
        return icon;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private const int SM_CXSMICON = 49;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
