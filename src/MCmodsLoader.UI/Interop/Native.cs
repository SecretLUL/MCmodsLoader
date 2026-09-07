using System.Runtime.InteropServices;

namespace MCmodsLoader.UI.Interop;

/// <summary>
/// The Win32 surface this app needs. Kept deliberately small: every entry here is used,
/// and everything is plain P/Invoke, so it survives NativeAOT compilation without
/// reflection or COM.
/// </summary>
internal static class Native
{
    // ---- Messages -------------------------------------------------------
    public const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_CLOSE = 0x0010,
                      WM_ERASEBKGND = 0x0014, WM_GETMINMAXINFO = 0x0024,
                      WM_DRAWITEM = 0x002B,
                      WM_SETFONT = 0x0030, WM_COMMAND = 0x0111,
                      WM_MOUSEMOVE = 0x0200, WM_MOUSELEAVE = 0x02A3, WM_DPICHANGED = 0x02E0,
                      WM_APP = 0x8000, WM_KILLFOCUS = 0x0008, WM_KEYDOWN = 0x0100,
                      WM_GETDLGCODE = 0x0087, WM_LBUTTONDOWN = 0x0201, WM_MOUSEWHEEL = 0x020A,
                      WM_CAPTURECHANGED = 0x0215;

    // ---- Virtual keys ----------------------------------------------------
    public const int VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_END = 0x23,
                     VK_HOME = 0x24, VK_UP = 0x26, VK_DOWN = 0x28,
                     VK_PRIOR = 0x21, VK_NEXT = 0x22;

    public const nint DLGC_WANTARROWS = 0x0001, DLGC_WANTCHARS = 0x0080;

    // ---- Window styles --------------------------------------------------
    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000,
                      WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000,
                      WS_MINIMIZEBOX = 0x00020000, WS_CLIPCHILDREN = 0x02000000;

    /// <summary>A caption bar that can be minimised and closed, but not resized or maximised.</summary>
    public const uint WS_OVERLAPPEDWINDOW_FIXED = WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN;

    public const uint BS_OWNERDRAW = 0x0000000B;

    // ---- Control notifications / messages -------------------------------
    public const int BN_CLICKED = 0;
    public const uint PBM_SETPOS = 0x0402, PBM_SETRANGE32 = 0x0406,
                      PBM_SETBARCOLOR = 0x0409, PBM_SETBKCOLOR = 0x2001;

    // ---- Owner-draw ------------------------------------------------------
    public const uint ODT_BUTTON = 4;
    public const uint ODS_SELECTED = 0x0001, ODS_DISABLED = 0x0004, ODS_FOCUS = 0x0010;

    // ---- DrawText flags --------------------------------------------------
    public const uint DT_LEFT = 0x0, DT_CENTER = 0x1, DT_RIGHT = 0x2, DT_VCENTER = 0x4,
                      DT_SINGLELINE = 0x20, DT_PATH_ELLIPSIS = 0x4000, DT_END_ELLIPSIS = 0x8000,
                      DT_NOPREFIX = 0x0800;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;

    public const int TRANSPARENT = 1;
    public const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNA = 8;
    public const uint TME_LEAVE = 0x0002;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const uint SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    public const uint MB_OK = 0x0, MB_ICONERROR = 0x10, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40;
    public const uint BIF_RETURNONLYFSDIRS = 0x0001, BIF_EDITBOX = 0x0010, BIF_NEWDIALOGSTYLE = 0x0040;
    public const uint SRCCOPY = 0x00CC0020;
    public const int IDC_ARROW = 32512;

    // ---- Structs ---------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public static RECT Xywh(int x, int y, int w, int h) => new() { Left = x, Top = y, Right = x + w, Bottom = y + h };
        public readonly RECT Inflate(int dx, int dy) => new() { Left = Left - dx, Top = Top - dy, Right = Right + dx, Bottom = Bottom + dy };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint Hwnd; public uint Message; public nint WParam, LParam; public uint Time; public POINT Pt; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint CbSize, Style;
        public nint LpfnWndProc;
        public int CbClsExtra, CbWndExtra;
        public nint HInstance, HIcon, HCursor, HbrBackground;
        public nint LpszMenuName, LpszClassName;
        public nint HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT
    {
        public nint Hdc;
        public int FErase;
        public RECT RcPaint;
        public int FRestore, FIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] RgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRAWITEMSTRUCT
    {
        public uint CtlType, CtlID, ItemID, ItemAction, ItemState;
        public nint HwndItem, Hdc;
        public RECT RcItem;
        public nuint ItemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT { public uint CbSize, DwFlags; public nint HwndTrack; public uint DwHoverTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOGFONT
    {
        public int LfHeight, LfWidth, LfEscapement, LfOrientation, LfWeight;
        public byte LfItalic, LfUnderline, LfStrikeOut, LfCharSet, LfOutPrecision,
                    LfClipPrecision, LfQuality, LfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string LfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BROWSEINFO
    {
        public nint HwndOwner, PidlRoot, PszDisplayName;
        public string LpszTitle;
        public uint UlFlags;
        public nint Lpfn, LParam;
        public int IImage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INITCOMMONCONTROLSEX { public uint DwSize, DwICC; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint CbSize;
        public RECT RcMonitor, RcWork;
        public uint DwFlags;
    }

    // ---- user32 ----------------------------------------------------------
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(uint exStyle, string className, string? windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int cmdShow);
    [DllImport("user32.dll")] public static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool IsDialogMessageW(nint hwnd, ref MSG msg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")] public static extern bool MoveWindow(nint hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] public static extern bool EnableWindow(nint hwnd, bool enable);
    [DllImport("user32.dll")] public static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool AdjustWindowRectExForDpi(ref RECT rect, uint style, bool menu, uint exStyle, uint dpi);
    [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
    [DllImport("user32.dll")] public static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hwnd, ref POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint LoadCursorW(nint instance, nint cursorName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    [DllImport("user32.dll")] public static extern nint BeginPaint(nint hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern bool EndPaint(nint hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern int FillRect(nint hdc, ref RECT rect, nint brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DrawTextW(nint hdc, string text, int count, ref RECT rect, uint format);

    // ---- gdi32 -----------------------------------------------------------
    [DllImport("gdi32.dll")] public static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] public static extern nint CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(nint hdc, uint color);
    [DllImport("gdi32.dll")] public static extern bool RoundRect(nint hdc, int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] public static extern bool Polygon(nint hdc, POINT[] points, int count);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern nint CreateFontIndirectW(ref LOGFONT lf);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint hdc);

    // ---- comctl32 / uxtheme / dwmapi / shell32 ---------------------------
    [DllImport("comctl32.dll")] public static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX icc);

    public delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData);

    [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint refData);
    [DllImport("comctl32.dll")] public static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(nint hwnd, string? appName, string? idList);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SHBrowseForFolderW(ref BROWSEINFO bi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SHGetPathFromIDListW(nint pidl, nint path);

    [DllImport("shell32.dll")] public static extern void ILFree(nint pidl);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? name);

    public const uint COINIT_APARTMENTTHREADED = 0x2;

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(nint reserved, uint flags);

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll")]
    public static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint LoadLibraryW(string name);

    /// <summary>Resolves an export by ordinal; the name pointer is the ordinal value itself.</summary>
    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true)]
    public static extern nint GetProcAddressByOrdinal(nint module, nint ordinal);

    // ---- Helpers ---------------------------------------------------------
    /// <summary>COLORREF is 0x00BBGGRR, so the byte order is reversed from HTML hex.</summary>
    public static uint FromHex(uint rrggbb) =>
        (uint)(((rrggbb >> 16) & 0xFF) | (((rrggbb >> 8) & 0xFF) << 8) | ((rrggbb & 0xFF) << 16));

    public static int LoWord(nint value) => (short)(value & 0xFFFF);
    public static int HiWord(nint value) => (short)((value >> 16) & 0xFFFF);
}
