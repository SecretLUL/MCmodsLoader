using MCperfBooster.UI.Interop;

namespace MCperfBooster.UI;

/// <summary>Small drawing helpers so the paint code reads like layout, not like GDI.</summary>
internal static class Gdi
{
    /// <summary>
    /// Handles WM_PAINT through a memory bitmap that is blitted once, so nothing
    /// flickers. The DC and bitmap are released even when <paramref name="draw"/> throws.
    /// </summary>
    public static void PaintBuffered(nint hwnd, Action<nint, Native.RECT> draw)
    {
        nint hdc = Native.BeginPaint(hwnd, out var ps);
        nint memDc = 0, bitmap = 0, oldBitmap = 0;
        try
        {
            Native.GetClientRect(hwnd, out var client);

            memDc = Native.CreateCompatibleDC(hdc);
            bitmap = Native.CreateCompatibleBitmap(hdc, client.Width, client.Height);
            oldBitmap = Native.SelectObject(memDc, bitmap);

            draw(memDc, client);

            Native.BitBlt(hdc, 0, 0, client.Width, client.Height, memDc, 0, 0, Native.SRCCOPY);
        }
        finally
        {
            if (oldBitmap != 0) Native.SelectObject(memDc, oldBitmap);
            if (bitmap != 0) Native.DeleteObject(bitmap);
            if (memDc != 0) Native.DeleteDC(memDc);
            Native.EndPaint(hwnd, ref ps);
        }
    }

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

    /// <summary>Draws only the 1px border of a rounded rect; the inside is left as painted.</summary>
    public static void RoundRectOutline(nint hdc, Native.RECT r, int radius, uint border)
    {
        nint pen = Native.CreatePen(0, 1, border);
        // Stock objects are owned by the system and must not be deleted.
        nint oldBrush = Native.SelectObject(hdc, Native.GetStockObject(Native.NULL_BRUSH));
        nint oldPen = Native.SelectObject(hdc, pen);

        Native.RoundRect(hdc, r.Left, r.Top, r.Right, r.Bottom, radius, radius);

        Native.SelectObject(hdc, oldBrush);
        Native.SelectObject(hdc, oldPen);
        Native.DeleteObject(pen);
    }

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
        nint oldFont = Native.SelectObject(hdc, font);
        var r = new Native.RECT();
        Native.DrawTextW(hdc, text, text.Length, ref r, Native.DT_CALCRECT | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
        Native.SelectObject(hdc, oldFont);
        return r.Width;
    }

    /// <summary>Measures the height a string needs when it wraps at <paramref name="width"/>.</summary>
    public static int MeasureWrappedHeight(nint hdc, string text, nint font, int width)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        nint oldFont = Native.SelectObject(hdc, font);
        var r = new Native.RECT { Right = width };
        Native.DrawTextW(hdc, text, text.Length, ref r,
            Native.DT_CALCRECT | Native.DT_WORDBREAK | Native.DT_EDITCONTROL | Native.DT_NOPREFIX);
        Native.SelectObject(hdc, oldFont);
        return r.Height;
    }
}
