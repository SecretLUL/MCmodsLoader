using MCmodsLoader.UI.Interop;

namespace MCmodsLoader.UI;

/// <summary>Small drawing helpers so the paint code reads like layout, not like GDI.</summary>
internal static class Gdi
{
    /// <summary>Fills a rounded rect with a 1px border in <paramref name="border"/>.</summary>
    public static void RoundRect(nint hdc, Native.RECT r, int radius, uint fill, uint border)
    {
        nint brush = Native.CreateSolidBrush(fill);
        nint pen = Native.CreatePen(0, 1, border);
        nint oldBrush = Native.SelectObject(hdc, brush);
        nint oldPen = Native.SelectObject(hdc, pen);

        Native.RoundRect(hdc, r.Left, r.Top, r.Right, r.Bottom, radius, radius);

        Native.SelectObject(hdc, oldBrush);
        Native.SelectObject(hdc, oldPen);
        Native.DeleteObject(brush);
        Native.DeleteObject(pen);
    }

    /// <summary>Fills a rounded rect in a single colour, with no contrasting border.</summary>
    public static void RoundRect(nint hdc, Native.RECT r, int radius, uint fill)
        => RoundRect(hdc, r, radius, fill, fill);

    public static void Fill(nint hdc, Native.RECT r, nint brush)
    {
        var rect = r;
        Native.FillRect(hdc, ref rect, brush);
    }

    public static void Text(nint hdc, string text, Native.RECT r, nint font, uint color, uint format)
    {
        if (string.IsNullOrEmpty(text)) return;
        nint oldFont = Native.SelectObject(hdc, font);
        Native.SetBkMode(hdc, Native.TRANSPARENT);
        Native.SetTextColor(hdc, color);
        var rect = r;
        Native.DrawTextW(hdc, text, text.Length, ref rect, format | Native.DT_NOPREFIX);
        Native.SelectObject(hdc, oldFont);
    }

    /// <summary>Measures the width a string needs with the given font.</summary>
    public static int MeasureWidth(nint hdc, string text, nint font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        const uint DT_CALCRECT = 0x0400;
        nint oldFont = Native.SelectObject(hdc, font);
        var r = new Native.RECT();
        Native.DrawTextW(hdc, text, text.Length, ref r, DT_CALCRECT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
        Native.SelectObject(hdc, oldFont);
        return r.Width;
    }
}
