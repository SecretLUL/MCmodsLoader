using MCmodsLoader.UI.Interop;

namespace MCmodsLoader.UI;

/// <summary>
/// The palette carried over from the previous WPF UI, plus the GDI objects that
/// render it. Owns every brush, pen and font, and releases them on dispose.
/// </summary>
internal sealed class Theme : IDisposable
{
    // Palette (same hex values the WPF version used).
    public static readonly uint Background = Native.FromHex(0x0F172A);
    public static readonly uint Card = Native.FromHex(0x1E293B);
    public static readonly uint CardBorder = Native.FromHex(0x334155);
    public static readonly uint TextPrimary = Native.FromHex(0xF8FAFC);
    public static readonly uint TextMuted = Native.FromHex(0x94A3B8);
    public static readonly uint Accent = Native.FromHex(0x10B981);
    public static readonly uint AccentHot = Native.FromHex(0x34D399);
    public static readonly uint AccentPressed = Native.FromHex(0x059669);
    public static readonly uint Secondary = Native.FromHex(0x334155);
    public static readonly uint SecondaryHot = Native.FromHex(0x475569);
    public static readonly uint Success = Native.FromHex(0x34D399);
    public static readonly uint Warning = Native.FromHex(0xFBBF24);
    public static readonly uint Danger = Native.FromHex(0xF87171);
    public static readonly uint White = Native.FromHex(0xFFFFFF);
    public static readonly uint BannerBack = Native.FromHex(0x064E3B);
    public static readonly uint BannerBorder = Native.FromHex(0x059669);
    public static readonly uint BannerTitle = Native.FromHex(0xECFDF5);
    public static readonly uint BannerText = Native.FromHex(0xA7F3D0);
    public static readonly uint Badge = Native.FromHex(0x0F172A);
    public static readonly uint Disabled = Native.FromHex(0x475569);
    public static readonly uint DisabledText = Native.FromHex(0x64748B);

    private readonly List<nint> _objects = new();

    public uint Dpi { get; }

    public nint BackgroundBrush { get; }
    public nint CardBrush { get; }
    public nint BadgeBrush { get; }
    public nint BannerBrush { get; }
    public nint CardBorderPen { get; }
    public nint BannerBorderPen { get; }
    public nint FocusPen { get; }

    public nint FontBody { get; }
    public nint FontBodyBold { get; }
    public nint FontSmall { get; }
    public nint FontSmallBold { get; }
    public nint FontLabel { get; }
    public nint FontTitle { get; }
    public nint FontHeading { get; }

    public Theme(uint dpi)
    {
        Dpi = dpi;

        BackgroundBrush = Track(Native.CreateSolidBrush(Background));
        CardBrush = Track(Native.CreateSolidBrush(Card));
        BadgeBrush = Track(Native.CreateSolidBrush(Badge));
        BannerBrush = Track(Native.CreateSolidBrush(BannerBack));
        CardBorderPen = Track(Native.CreatePen(0, 1, CardBorder));
        BannerBorderPen = Track(Native.CreatePen(0, 1, BannerBorder));
        FocusPen = Track(Native.CreatePen(0, Scale(2), AccentHot));

        FontBody = Track(CreateFont(12, 400));
        FontBodyBold = Track(CreateFont(12, 600));
        FontSmall = Track(CreateFont(11, 400));
        FontSmallBold = Track(CreateFont(11, 700));
        FontLabel = Track(CreateFont(10, 700));
        FontTitle = Track(CreateFont(14, 700));
        FontHeading = Track(CreateFont(17, 700));
    }

    /// <summary>Scales a 96-DPI design value to the current monitor DPI.</summary>
    public int Scale(int value96) => (int)Math.Round(value96 * Dpi / 96.0);

    private nint CreateFont(int pt, int weight)
    {
        var lf = new Native.LOGFONT
        {
            // Negative height means "character height" rather than "cell height".
            LfHeight = -(int)Math.Round(pt * Dpi / 72.0),
            LfWeight = weight,
            LfCharSet = 1,      // DEFAULT_CHARSET
            LfQuality = 5,      // CLEARTYPE_QUALITY
            LfFaceName = "Segoe UI",
        };
        return Native.CreateFontIndirectW(ref lf);
    }

    private nint Track(nint handle)
    {
        if (handle != 0) _objects.Add(handle);
        return handle;
    }

    public void Dispose()
    {
        foreach (var o in _objects) Native.DeleteObject(o);
        _objects.Clear();
    }
}
