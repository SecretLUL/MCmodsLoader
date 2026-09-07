using System.Runtime.InteropServices;
using MCmodsLoader.UI.Interop;

namespace MCmodsLoader.UI;

/// <summary>
/// A drop-down list drawn entirely by this app.
///
/// The stock COMBOBOX was tried first and rejected: its list and scrollbar are drawn
/// by the system in the light theme, and making them dark needs undocumented uxtheme
/// ordinals that may change between Windows builds. Drawing the popup here keeps the
/// control consistent on every machine and uses only documented API.
///
/// The field itself is an owner-drawn button, so focus, tabbing and hover come from
/// <see cref="UiButton"/>.
/// </summary>
internal sealed class VersionPicker
{
    private const string PopupClassName = "MCmodsLoaderVersionList";
    private const int MaxVisibleItems = 12;
    private const int ItemHeight96 = 24;

    private static readonly WndProcDelegate PopupProcedure = StaticPopupProc;
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);
    private static VersionPicker? _openPicker;
    private static bool _classRegistered;

    private readonly List<string> _items = new();
    private Theme _theme;
    private nint _popup;
    private nint _ownerWindow;
    private int _selected = -1;
    private int _hover = -1;
    private int _scroll;

    public UiButton Field { get; }

    /// <summary>Raised when the user picks a different entry.</summary>
    public Action<string>? SelectionChanged { get; set; }

    public VersionPicker(int fieldId, Theme theme)
    {
        _theme = theme;
        Field = new UiButton(fieldId, string.Empty, ButtonKind.Field)
        {
            WantsArrowKeys = true,
        };
        Field.KeyDown = OnFieldKeyDown;
    }

    public string? SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;
    public bool IsOpen => _popup != 0 && _openPicker == this;

    public void UpdateTheme(Theme theme) => _theme = theme;

    public void SetItems(IEnumerable<string> items, int selectedIndex)
    {
        _items.Clear();
        _items.AddRange(items);
        _selected = _items.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, _items.Count - 1);
        Field.SetText(SelectedItem ?? string.Empty);
    }

    /// <summary>Changes the selection without notifying, e.g. while repopulating.</summary>
    public void Select(int index, bool notify)
    {
        if (index < 0 || index >= _items.Count || index == _selected) return;

        _selected = index;
        Field.SetText(_items[index]);
        if (notify) SelectionChanged?.Invoke(_items[index]);
    }

    // ---- Field behaviour -------------------------------------------------

    public void Toggle(nint ownerWindow)
    {
        if (IsOpen) Close();
        else Open(ownerWindow);
    }

    private bool OnFieldKeyDown(int virtualKey)
    {
        switch (virtualKey)
        {
            case Native.VK_DOWN when !IsOpen:
                Select(_selected + 1, notify: true);
                return true;
            case Native.VK_UP when !IsOpen:
                Select(_selected - 1, notify: true);
                return true;

            case Native.VK_DOWN: MoveHover(1); return true;
            case Native.VK_UP: MoveHover(-1); return true;
            case Native.VK_NEXT: MoveHover(MaxVisibleItems); return true;
            case Native.VK_PRIOR: MoveHover(-MaxVisibleItems); return true;
            case Native.VK_HOME: SetHover(0); return true;
            case Native.VK_END: SetHover(_items.Count - 1); return true;

            case Native.VK_RETURN or Native.VK_SPACE when IsOpen:
                Commit(_hover);
                return true;

            case Native.VK_ESCAPE when IsOpen:
                Close();
                return true;
        }

        return false;
    }

    private void MoveHover(int delta) => SetHover(Math.Clamp(_hover + delta, 0, _items.Count - 1));

    private void SetHover(int index)
    {
        if (!IsOpen || _items.Count == 0) return;

        _hover = Math.Clamp(index, 0, _items.Count - 1);
        EnsureVisible(_hover);
        Native.InvalidateRect(_popup, 0, false);
    }

    private void EnsureVisible(int index)
    {
        int visible = VisibleCount();
        if (index < _scroll) _scroll = index;
        else if (index >= _scroll + visible) _scroll = index - visible + 1;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _items.Count - visible));
    }

    private int VisibleCount() => Math.Min(_items.Count, MaxVisibleItems);

    // ---- Popup -----------------------------------------------------------

    private void Open(nint ownerWindow)
    {
        if (_items.Count == 0) return;

        _ownerWindow = ownerWindow;
        EnsurePopupWindow();

        _hover = _selected;
        _scroll = 0;
        if (_hover >= 0) EnsureVisible(_hover);

        Native.GetWindowRect(Field.Hwnd, out var field);
        int itemH = _theme.Scale(ItemHeight96);
        int height = VisibleCount() * itemH + 2;
        int width = field.Width;

        // Flip above the field when there is no room below.
        nint monitor = Native.MonitorFromWindow(Field.Hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { CbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        int bottomLimit = Native.GetMonitorInfoW(monitor, ref info) ? info.RcWork.Bottom : field.Bottom + height;
        int top = field.Bottom + height <= bottomLimit ? field.Bottom : field.Top - height;

        Native.SetWindowPos(_popup, unchecked((nint)(-1)) /* HWND_TOPMOST */, field.Left, top, width, height, 0);
        Native.ShowWindow(_popup, Native.SW_SHOWNA);

        _openPicker = this;
        Native.SetCapture(_popup);
        // The popup is shown without activation, so keyboard input keeps going to the
        // field; make sure that is where the focus actually is.
        Native.SetFocus(Field.Hwnd);
        Field.Invalidate();
    }

    public void Close()
    {
        if (_popup == 0) return;

        if (_openPicker == this)
        {
            _openPicker = null;
            Native.ReleaseCapture();
        }

        Native.ShowWindow(_popup, Native.SW_HIDE);
        Field.Invalidate();
    }

    private void Commit(int index)
    {
        Close();
        if (index >= 0 && index != _selected) Select(index, notify: true);
    }

    private void EnsurePopupWindow()
    {
        if (_popup != 0) return;

        nint instance = Native.GetModuleHandleW(null);

        if (!_classRegistered)
        {
            var wc = new Native.WNDCLASSEX
            {
                CbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                LpfnWndProc = Marshal.GetFunctionPointerForDelegate(PopupProcedure),
                HInstance = instance,
                HCursor = Native.LoadCursorW(0, Native.IDC_ARROW),
                LpszClassName = Marshal.StringToHGlobalUni(PopupClassName),
            };
            Native.RegisterClassExW(ref wc);
            _classRegistered = true;
        }

        _popup = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST,
            PopupClassName, null, Native.WS_POPUP, 0, 0, 10, 10, _ownerWindow, 0, instance, 0);
    }

    private static nint StaticPopupProc(nint hwnd, uint msg, nint wParam, nint lParam)
        => _openPicker is { } picker
            ? picker.PopupProc(hwnd, msg, wParam, lParam)
            : Native.DefWindowProcW(hwnd, msg, wParam, lParam);

    private nint PopupProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Native.WM_ERASEBKGND:
                return 1;

            case Native.WM_PAINT:
                PaintPopup(hwnd);
                return 0;

            case Native.WM_MOUSEMOVE:
            {
                int index = IndexFromPoint(hwnd, Native.LoWord(lParam), Native.HiWord(lParam));
                if (index != _hover)
                {
                    _hover = index;
                    Native.InvalidateRect(hwnd, 0, false);
                }
                return 0;
            }

            case Native.WM_LBUTTONDOWN:
            {
                int index = IndexFromPoint(hwnd, Native.LoWord(lParam), Native.HiWord(lParam));
                // With the mouse captured, a click anywhere lands here; outside the
                // list it means "dismiss".
                if (index >= 0) Commit(index);
                else Close();
                return 0;
            }

            case Native.WM_MOUSEWHEEL:
            {
                int delta = Native.HiWord(wParam);
                Scroll(delta > 0 ? -3 : 3);
                return 0;
            }

            case Native.WM_CAPTURECHANGED:
                if (_openPicker == this)
                {
                    _openPicker = null;
                    Native.ShowWindow(_popup, Native.SW_HIDE);
                    Field.Invalidate();
                }
                return 0;
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Scroll(int rows)
    {
        int max = Math.Max(0, _items.Count - VisibleCount());
        int updated = Math.Clamp(_scroll + rows, 0, max);
        if (updated == _scroll) return;

        _scroll = updated;
        Native.InvalidateRect(_popup, 0, false);
    }

    /// <summary>Item index under a client point, or -1 when the point is outside the list.</summary>
    private int IndexFromPoint(nint hwnd, int x, int y)
    {
        Native.GetClientRect(hwnd, out var client);
        if (x < 0 || y < 0 || x >= client.Width || y >= client.Height) return -1;

        int index = _scroll + (y - 1) / _theme.Scale(ItemHeight96);
        return index >= 0 && index < _items.Count ? index : -1;
    }

    private void PaintPopup(nint hwnd)
    {
        nint hdc = Native.BeginPaint(hwnd, out var ps);
        try
        {
            Native.GetClientRect(hwnd, out var client);

            nint memDc = Native.CreateCompatibleDC(hdc);
            nint bitmap = Native.CreateCompatibleBitmap(hdc, client.Width, client.Height);
            nint oldBitmap = Native.SelectObject(memDc, bitmap);

            Gdi.RoundRect(memDc, client, 0, Theme.Card, Theme.CardBorder);

            int itemH = _theme.Scale(ItemHeight96);
            int padding = _theme.Scale(10);
            int visible = VisibleCount();

            for (int row = 0; row < visible; row++)
            {
                int index = _scroll + row;
                if (index >= _items.Count) break;

                var itemRect = Native.RECT.Xywh(1, 1 + row * itemH, client.Width - 2, itemH);

                if (index == _hover)
                {
                    nint brush = Native.CreateSolidBrush(Theme.CardBorder);
                    Gdi.Fill(memDc, itemRect, brush);
                    Native.DeleteObject(brush);
                }

                var textRect = itemRect;
                textRect.Left += padding;
                textRect.Right -= padding;

                Gdi.Text(memDc, _items[index], textRect, _theme.FontBody,
                    index == _selected ? Theme.Accent : Theme.TextPrimary,
                    Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_END_ELLIPSIS);
            }

            PaintScrollIndicator(memDc, client, visible);

            Native.BitBlt(hdc, 0, 0, client.Width, client.Height, memDc, 0, 0, Native.SRCCOPY);

            Native.SelectObject(memDc, oldBitmap);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memDc);
        }
        finally
        {
            Native.EndPaint(hwnd, ref ps);
        }
    }

    /// <summary>A slim thumb on the right edge; the system scrollbar would be light.</summary>
    private void PaintScrollIndicator(nint dc, Native.RECT client, int visible)
    {
        if (_items.Count <= visible) return;

        int barW = _theme.Scale(4);
        int trackH = client.Height - 2;
        int thumbH = Math.Max(_theme.Scale(20), trackH * visible / _items.Count);
        int maxScroll = _items.Count - visible;
        int thumbY = 1 + (trackH - thumbH) * _scroll / maxScroll;

        var thumb = Native.RECT.Xywh(client.Right - barW - _theme.Scale(3), thumbY, barW, thumbH);
        Gdi.RoundRect(dc, thumb, barW, Theme.SecondaryHot);
    }
}
