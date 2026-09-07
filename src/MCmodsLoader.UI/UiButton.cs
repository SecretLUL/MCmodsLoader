using System.Runtime.InteropServices;
using MCmodsLoader.UI.Interop;

namespace MCmodsLoader.UI;

internal enum ButtonKind
{
    /// <summary>The one call-to-action: filled green.</summary>
    Primary,
    /// <summary>Supporting actions: filled slate.</summary>
    Secondary,
    /// <summary>Drop-down field: sunken surface, left-aligned text, chevron on the right.</summary>
    Field,
}

/// <summary>
/// An owner-drawn push button. Windows reports pressed, disabled and focus state in
/// WM_DRAWITEM; hover is not reported that way, so each button subclasses itself to
/// track mouse enter and leave.
/// </summary>
internal sealed class UiButton
{
    private static readonly Dictionary<nint, UiButton> ByHandle = new();

    // Handed to native code, so it must stay rooted for the lifetime of the process
    // or the callback would be collected out from under Windows.
    private static readonly Native.SubclassProc HoverProc = OnHoverMessage;

    public nint Hwnd { get; private set; }
    public int Id { get; }
    public ButtonKind Kind { get; }
    public string Text { get; private set; }
    public bool IsHot { get; private set; }

    /// <summary>Set for the drop-down field so arrow keys reach it instead of moving focus.</summary>
    public bool WantsArrowKeys { get; init; }

    /// <summary>Returns true when the key was consumed.</summary>
    public Func<int, bool>? KeyDown { get; set; }

    /// <summary>
    /// Colour of the surface the button sits on. Rounded corners let the background
    /// show through, so the button has to clear to the right colour before drawing.
    /// </summary>
    public uint Surface { get; set; } = Theme.Card;

    public UiButton(int id, string text, ButtonKind kind)
    {
        Id = id;
        Text = text;
        Kind = kind;
    }

    public void Create(nint parent, nint instance)
    {
        Hwnd = Native.CreateWindowExW(0, "BUTTON", Text,
            Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_TABSTOP | Native.BS_OWNERDRAW,
            0, 0, 10, 10, parent, Id, instance, 0);

        ByHandle[Hwnd] = this;
        Native.SetWindowSubclass(Hwnd, HoverProc, (nuint)Id, 0);
    }

    public void Move(Native.RECT r) => Native.MoveWindow(Hwnd, r.Left, r.Top, r.Width, r.Height, true);

    public void SetEnabled(bool enabled)
    {
        Native.EnableWindow(Hwnd, enabled);
        Invalidate();
    }

    public void SetText(string text)
    {
        if (Text == text) return;
        Text = text;
        Invalidate();
    }

    public void Invalidate() => Native.InvalidateRect(Hwnd, 0, true);

    private static nint OnHoverMessage(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        if (ByHandle.TryGetValue(hwnd, out var button))
        {
            switch (msg)
            {
                case Native.WM_MOUSEMOVE when !button.IsHot:
                    button.IsHot = true;
                    var tme = new Native.TRACKMOUSEEVENT
                    {
                        CbSize = (uint)Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
                        DwFlags = Native.TME_LEAVE,
                        HwndTrack = hwnd,
                    };
                    Native.TrackMouseEvent(ref tme);
                    button.Invalidate();
                    break;

                case Native.WM_MOUSELEAVE:
                    button.IsHot = false;
                    button.Invalidate();
                    break;

                case Native.WM_GETDLGCODE when button.WantsArrowKeys:
                    return Native.DLGC_WANTARROWS | Native.DLGC_WANTCHARS;

                case Native.WM_KEYDOWN when button.KeyDown is not null:
                    if (button.KeyDown((int)wParam)) return 0;
                    break;

                case Native.WM_DESTROY:
                    ByHandle.Remove(hwnd);
                    break;
            }
        }

        return Native.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Draw(Theme theme, in Native.DRAWITEMSTRUCT item)
    {
        bool disabled = (item.ItemState & Native.ODS_DISABLED) != 0;
        bool pressed = (item.ItemState & Native.ODS_SELECTED) != 0;
        bool focused = (item.ItemState & Native.ODS_FOCUS) != 0;

        if (Kind == ButtonKind.Field)
        {
            DrawField(theme, item, disabled, focused);
            return;
        }

        uint fill = Kind switch
        {
            ButtonKind.Primary when disabled => Theme.Disabled,
            ButtonKind.Primary when pressed => Theme.AccentPressed,
            ButtonKind.Primary when IsHot => Theme.AccentHot,
            ButtonKind.Primary => Theme.Accent,
            _ when disabled => Theme.Disabled,
            _ when pressed => Theme.CardBorder,
            _ when IsHot => Theme.SecondaryHot,
            _ => Theme.Secondary,
        };

        uint textColor = disabled
            ? Theme.DisabledText
            : Kind == ButtonKind.Primary ? Theme.White : Theme.TextPrimary;

        nint surfaceBrush = Native.CreateSolidBrush(Surface);
        Gdi.Fill(item.Hdc, item.RcItem, surfaceBrush);
        Native.DeleteObject(surfaceBrush);

        int radius = theme.Scale(Kind == ButtonKind.Primary ? 10 : 8);
        Gdi.RoundRect(item.Hdc, item.RcItem, radius, fill);

        if (focused && !disabled)
        {
            uint ringColor = Kind == ButtonKind.Primary ? Theme.White : Theme.TextMuted;
            Gdi.RoundRect(item.Hdc, item.RcItem.Inflate(-theme.Scale(3), -theme.Scale(3)),
                radius, fill, ringColor);
        }

        nint font = Kind == ButtonKind.Primary ? theme.FontBodyBold : theme.FontSmallBold;
        Gdi.Text(item.Hdc, Text, item.RcItem, font, textColor,
            Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_END_ELLIPSIS);
    }

    private void DrawField(Theme theme, in Native.DRAWITEMSTRUCT item, bool disabled, bool focused)
    {
        nint surfaceBrush = Native.CreateSolidBrush(Surface);
        Gdi.Fill(item.Hdc, item.RcItem, surfaceBrush);
        Native.DeleteObject(surfaceBrush);

        uint border = disabled ? Theme.Disabled
            : focused || IsHot ? Theme.Accent
            : Theme.CardBorder;

        int radius = theme.Scale(6);
        Gdi.RoundRect(item.Hdc, item.RcItem, radius, Theme.Background, border);

        int padding = theme.Scale(10);
        int chevronBox = theme.Scale(18);

        var textRect = item.RcItem;
        textRect.Left += padding;
        textRect.Right -= padding + chevronBox;
        Gdi.Text(item.Hdc, Text, textRect, theme.FontBody,
            disabled ? Theme.DisabledText : Theme.TextPrimary,
            Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_END_ELLIPSIS);

        DrawChevron(theme, item, disabled ? Theme.DisabledText : Theme.TextMuted, padding, chevronBox);
    }

    private static void DrawChevron(Theme theme, in Native.DRAWITEMSTRUCT item, uint color, int padding, int box)
    {
        int w = theme.Scale(9), h = theme.Scale(5);
        int cx = item.RcItem.Right - padding - box / 2;
        int cy = (item.RcItem.Top + item.RcItem.Bottom) / 2;

        var points = new[]
        {
            new Native.POINT { X = cx - w / 2, Y = cy - h / 2 },
            new Native.POINT { X = cx + w / 2, Y = cy - h / 2 },
            new Native.POINT { X = cx, Y = cy + h / 2 + theme.Scale(1) },
        };

        nint brush = Native.CreateSolidBrush(color);
        nint pen = Native.CreatePen(0, 1, color);
        nint oldBrush = Native.SelectObject(item.Hdc, brush);
        nint oldPen = Native.SelectObject(item.Hdc, pen);

        Native.Polygon(item.Hdc, points, points.Length);

        Native.SelectObject(item.Hdc, oldBrush);
        Native.SelectObject(item.Hdc, oldPen);
        Native.DeleteObject(brush);
        Native.DeleteObject(pen);
    }

    public static UiButton? Find(nint hwnd) => ByHandle.TryGetValue(hwnd, out var b) ? b : null;
}
