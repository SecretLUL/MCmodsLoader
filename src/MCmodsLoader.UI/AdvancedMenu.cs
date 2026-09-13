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

    public bool IncludeXaeroMinimap { get; set; } = true;
    public bool IncludeXaeroWorldMap { get; set; } = true;
    public Action<string, bool>? ModToggled { get; set; }

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
        int height = headerH + 2 * rowH + _theme.Scale(6);

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
        => _openMenu is { } menu
            ? menu.PopupProc(hwnd, msg, wParam, lParam)
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

                if (row == 0)
                {
                    IncludeXaeroMinimap = !IncludeXaeroMinimap;
                    ModToggled?.Invoke("xaeros-minimap", IncludeXaeroMinimap);
                    Native.InvalidateRect(hwnd, 0, false);
                }
                else if (row == 1)
                {
                    IncludeXaeroWorldMap = !IncludeXaeroWorldMap;
                    ModToggled?.Invoke("xaeros-world-map", IncludeXaeroWorldMap);
                    Native.InvalidateRect(hwnd, 0, false);
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

            case Native.WM_KEYDOWN when wParam == Native.VK_ESCAPE:
                Close();
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
        return row is 0 or 1 ? row : -1;
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

            Gdi.RoundRect(memDc, client, _theme.Scale(6), Theme.Card, Theme.CardBorder);

            int pad = _theme.Scale(12);
            int headerH = _theme.Scale(24);
            int rowH = _theme.Scale(28);

            var headerRect = Native.RECT.Xywh(pad, _theme.Scale(4), client.Width - 2 * pad, headerH);
            Gdi.Text(memDc, "OPTIONAL MODS", headerRect, _theme.FontLabel, Theme.TextMuted,
                Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE);

            DrawRow(memDc, client, 0, headerH, rowH, pad, "Xaero's Minimap", IncludeXaeroMinimap);
            DrawRow(memDc, client, 1, headerH + rowH, rowH, pad, "Xaero's World Map", IncludeXaeroWorldMap);

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
