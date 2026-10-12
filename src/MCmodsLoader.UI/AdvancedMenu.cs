using System.Runtime.InteropServices;
using MCmodsLoader.UI.Interop;

namespace MCmodsLoader.UI;

internal sealed class AdvancedMenu
{
    private const string PopupClassName = "MCmodsLoaderAdvancedMenu";
    private static readonly WndProcDelegate PopupProcedure = StaticPopupProc;
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);
    private static AdvancedMenu? _openMenu;
    private static bool _classRegistered;

    private Theme _theme;
    private nint _popup;
    private nint _ownerWindow;
    private int _hoverRow = -1;
    private IReadOnlyList<Option> _options = Array.Empty<Option>();

    /// <summary>One row: a mod that can be switched on or off. <see cref="Key"/> is what <see cref="ModToggled"/> reports.</summary>
    public sealed class Option
    {
        public Option(string key, string label, bool isChecked)
        {
            Key = key;
            Label = label;
            IsChecked = isChecked;
        }

        public string Key { get; }
        public string Label { get; }
        public bool IsChecked { get; set; }
    }

    public Action<string, bool>? ModToggled { get; set; }

    /// <summary>Replaces the rows; call it before <see cref="Open"/>, whose height follows the row count.</summary>
    public void SetOptions(IReadOnlyList<Option> options)
    {
        _options = options;
        _hoverRow = -1;
    }

    public bool IsOpen => _popup != 0 && _openMenu == this;

    public AdvancedMenu(Theme theme)
    {
        _theme = theme;
    }

    public void UpdateTheme(Theme theme) => _theme = theme;

    public void Toggle(nint ownerWindow, Native.RECT anchorRect)
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open(ownerWindow, anchorRect);
        }
    }

    public void Open(nint ownerWindow, Native.RECT anchorRect)
    {
        _ownerWindow = ownerWindow;
        EnsurePopupWindow();

        int width = _theme.Scale(185);
        int headerH = _theme.Scale(24);
        int rowH = _theme.Scale(28);
        int height = headerH + _options.Count * rowH + _theme.Scale(6);

        nint monitor = Native.MonitorFromWindow(ownerWindow, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { CbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfoW(monitor, ref info);

        int left = anchorRect.Left;
        if (info.RcWork.Right > 0 && left + width > info.RcWork.Right)
        {
            left = info.RcWork.Right - width;
        }
        if (info.RcWork.Left > 0 && left < info.RcWork.Left)
        {
            left = info.RcWork.Left;
        }

        int spacing = _theme.Scale(4);
        int top = anchorRect.Top - height - spacing >= info.RcWork.Top
            ? anchorRect.Top - height - spacing
            : anchorRect.Bottom + spacing;

        Native.SetWindowPos(_popup, unchecked((nint)(-1)), left, top, width, height, Native.SWP_NOACTIVATE);
        Native.ShowWindow(_popup, Native.SW_SHOWNA);

        _openMenu = this;
        Native.SetCapture(_popup);
        Native.InvalidateRect(_popup, 0, false);
    }

    public void Close()
    {
        if (_popup == 0) return;

        if (_openMenu == this)
        {
            _openMenu = null;
            Native.ReleaseCapture();
        }

        _hoverRow = -1;
        Native.ShowWindow(_popup, Native.SW_HIDE);
    }

    /// <summary>
    /// Keyboard input while the menu is open. The popup is shown without activation and
    /// never gets the focus, so the message loop passes key presses in from outside.
    /// Returns true when the key was consumed.
    /// </summary>
    public bool HandleKey(int virtualKey, bool isRepeat)
    {
        if (!IsOpen) return false;

        switch (virtualKey)
        {
            case Native.VK_ESCAPE:
                Close();
                return true;

            case Native.VK_DOWN when _options.Count > 0:
                SetHoverRow(_hoverRow < 0 ? 0 : Math.Min(_hoverRow + 1, _options.Count - 1));
                return true;

            case Native.VK_UP when _options.Count > 0:
                SetHoverRow(_hoverRow < 0 ? _options.Count - 1 : Math.Max(_hoverRow - 1, 0));
                return true;

            case Native.VK_DOWN or Native.VK_UP:
                return true;

            case Native.VK_SPACE or Native.VK_RETURN:
                // A held key would otherwise flip the row (a settings save and a full
                // mods-folder scan each) many times a second.
                if (_hoverRow >= 0 && !isRepeat) ToggleRow(_hoverRow);
                return true;
        }

        return false;
    }

    private void SetHoverRow(int row)
    {
        if (row == _hoverRow) return;
        _hoverRow = row;
        Native.InvalidateRect(_popup, 0, false);
    }

    private void ToggleRow(int row)
    {
        if (row < 0 || row >= _options.Count) return;

        var option = _options[row];
        option.IsChecked = !option.IsChecked;
        ModToggled?.Invoke(option.Key, option.IsChecked);

        Native.InvalidateRect(_popup, 0, false);
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
    {
        if (_openMenu is not { } menu) return Native.DefWindowProcW(hwnd, msg, wParam, lParam);

        try
        {
            return menu.PopupProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // An exception escaping into user32 ends a NativeAOT process on the spot.
            MainWindow.ReportCallbackError(msg, ex);
            return 0;
        }
    }

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
                int row = RowFromPoint(hwnd, Native.LoWord(lParam), Native.HiWord(lParam));
                if (row != _hoverRow)
                {
                    _hoverRow = row;
                    Native.InvalidateRect(hwnd, 0, false);
                }
                return 0;
            }

            case Native.WM_LBUTTONDOWN:
            {
                int x = Native.LoWord(lParam);
                int y = Native.HiWord(lParam);
                int row = RowFromPoint(hwnd, x, y);

                if (row >= 0)
                {
                    ToggleRow(row);
                }
                else
                {
                    Native.GetClientRect(hwnd, out var client);
                    if (x < 0 || y < 0 || x >= client.Width || y >= client.Height)
                    {
                        Close();
                    }
                }
                return 0;
            }

            case Native.WM_KEYDOWN when HandleKey((int)wParam, Native.IsKeyRepeat(lParam)):
                return 0;

            case Native.WM_CAPTURECHANGED:
                if (_openMenu == this)
                {
                    _openMenu = null;
                    _hoverRow = -1;
                    Native.ShowWindow(_popup, Native.SW_HIDE);
                }
                return 0;
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private int RowFromPoint(nint hwnd, int x, int y)
    {
        Native.GetClientRect(hwnd, out var client);
        if (x < 0 || y < 0 || x >= client.Width || y >= client.Height) return -1;

        int headerH = _theme.Scale(24);
        int rowH = _theme.Scale(28);

        if (y < headerH) return -1;
        int row = (y - headerH) / rowH;
        return row < _options.Count ? row : -1;
    }

    private void PaintPopup(nint hwnd) => Gdi.PaintBuffered(hwnd, (memDc, client) =>
    {
        // A new bitmap starts out black, and the rounded rect leaves its corners
        // untouched; give them the window background instead.
        Gdi.Fill(memDc, client, _theme.BackgroundBrush);
        Gdi.RoundRect(memDc, client, _theme.Scale(6), Theme.Card, Theme.CardBorder);

        int pad = _theme.Scale(12);
        int headerH = _theme.Scale(24);
        int rowH = _theme.Scale(28);

        var headerRect = Native.RECT.Xywh(pad, _theme.Scale(4), client.Width - 2 * pad, headerH);
        Gdi.Text(memDc, "OPTIONAL MODS", headerRect, _theme.FontLabel, Theme.TextMuted,
            Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE);

        for (int row = 0; row < _options.Count; row++)
        {
            DrawRow(memDc, client, row, headerH + row * rowH, rowH, pad, _options[row].Label, _options[row].IsChecked);
        }
    });

    private void DrawRow(nint dc, Native.RECT client, int rowIndex, int top, int rowH, int pad, string label, bool isChecked)
    {
        var rowRect = Native.RECT.Xywh(1, top, client.Width - 2, rowH);

        if (_hoverRow == rowIndex)
        {
            nint brush = Native.CreateSolidBrush(Theme.SecondaryHot);
            Gdi.Fill(dc, rowRect, brush);
            Native.DeleteObject(brush);
        }

        int boxSize = _theme.Scale(16);
        var boxRect = Native.RECT.Xywh(rowRect.Left + pad, rowRect.Top + (rowH - boxSize) / 2, boxSize, boxSize);

        if (isChecked)
        {
            Gdi.RoundRect(dc, boxRect, _theme.Scale(4), Theme.Accent, Theme.Accent);

            int cx = (boxRect.Left + boxRect.Right) / 2;
            int cy = (boxRect.Top + boxRect.Bottom) / 2;
            int s = _theme.Scale(3);

            var pts = new[]
            {
                new Native.POINT { X = cx - s - _theme.Scale(1), Y = cy },
                new Native.POINT { X = cx - _theme.Scale(1), Y = cy + s },
                new Native.POINT { X = cx + s + _theme.Scale(1), Y = cy - s }
            };

            nint pen = Native.CreatePen(0, _theme.Scale(2), Theme.White);
            nint oldPen = Native.SelectObject(dc, pen);
            Native.Polyline(dc, pts, pts.Length);
            Native.SelectObject(dc, oldPen);
            Native.DeleteObject(pen);
        }
        else
        {
            Gdi.RoundRect(dc, boxRect, _theme.Scale(4), Theme.Background, Theme.CardBorder);
        }

        var textRect = Native.RECT.Xywh(boxRect.Right + _theme.Scale(10), rowRect.Top,
            rowRect.Width - boxRect.Width - pad - _theme.Scale(10), rowH);

        Gdi.Text(dc, label, textRect, _theme.FontBody, Theme.TextPrimary,
            Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_END_ELLIPSIS);
    }
}
