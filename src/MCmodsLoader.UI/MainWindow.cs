using System.Runtime.InteropServices;
using MCmodsLoader.UI.Interop;

namespace MCmodsLoader.UI;

/// <summary>
/// The application window. Layout, painting and message handling live here; the
/// behaviour ported from the old WPF code-behind lives in MainWindow.Logic.cs.
///
/// The window is fixed-size and centred (the WPF version used SizeToContent plus
/// ResizeMode=CanMinimize), so the layout is recomputed only when the DPI changes
/// or the update banner appears.
/// </summary>
internal sealed partial class MainWindow
{
    private const string ClassName = "MCmodsLoaderWindow";
    private const string WindowTitle = "MCmodsLoader";

    // Design metrics, in 96-DPI units.
    private const int DesignWidth = 840;
    private const int Margin = 20, Gap = 16, HeaderH = 34, BannerH = 54, CardsH = 74, ActionH = 104;
    private const int CardPad = 12, BrowseButtonW = 70, FabricButtonW = 100, InjectW = 240, ActionPad = 16;

    private const int IdOpenMods = 1001, IdRefresh = 1002, IdCheckUpdates = 1003, IdUpdateNow = 1004,
                      IdBrowse = 1005, IdVersions = 1006, IdFabric = 1007, IdInject = 1008;

    // Rooted for the lifetime of the process: Windows keeps the raw pointer.
    private static readonly WndProcDelegate WindowProcedure = StaticWndProc;
    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);
    private static MainWindow? _instance;

    private readonly UiSyncContext _sync = new();
    private nint _hwnd, _hInstance, _prgProgress;
    private Theme _theme = null!;
    private VersionPicker _picker = null!;

    private readonly UiButton _btnOpenMods = new(IdOpenMods, "Open Mods Folder", ButtonKind.Secondary);
    private readonly UiButton _btnRefresh = new(IdRefresh, "Refresh", ButtonKind.Secondary);
    private readonly UiButton _btnCheckUpdates = new(IdCheckUpdates, "Check Updates", ButtonKind.Secondary);
    private readonly UiButton _btnUpdateNow = new(IdUpdateNow, "Update Now", ButtonKind.Primary);
    private readonly UiButton _btnBrowse = new(IdBrowse, "Browse", ButtonKind.Secondary);
    private readonly UiButton _btnFabric = new(IdFabric, "Install Fabric", ButtonKind.Secondary);
    private readonly UiButton _btnInject = new(IdInject, "Inject Performance Mods", ButtonKind.Primary);

    private UiButton[] AllButtons => new[]
    {
        _btnOpenMods, _btnRefresh, _btnCheckUpdates, _btnUpdateNow, _btnBrowse, _btnFabric, _btnInject,
        _picker.Field,
    };

    // Layout, recomputed by Relayout().
    private Native.RECT _rcHeader, _rcBanner, _rcCardPath, _rcCardVersion, _rcCardFabric, _rcAction, _rcProgress;

    public static int Run()
    {
        _instance = new MainWindow();
        return _instance.Start();
    }

    private int Start()
    {
        SynchronizationContext.SetSynchronizationContext(_sync);

        var icc = new Native.INITCOMMONCONTROLSEX
        {
            DwSize = (uint)Marshal.SizeOf<Native.INITCOMMONCONTROLSEX>(),
            DwICC = 0x00004000 | 0x00000020, // ICC_STANDARD_CLASSES | ICC_PROGRESS_CLASS
        };
        Native.InitCommonControlsEx(ref icc);

        _hInstance = Native.GetModuleHandleW(null);
        RegisterWindowClass();

        const uint style = Native.WS_OVERLAPPEDWINDOW_FIXED;
        _hwnd = Native.CreateWindowExW(0, ClassName, WindowTitle, style,
            0, 0, 100, 100, 0, 0, _hInstance, 0);

        if (_hwnd == 0)
        {
            Native.MessageBoxW(0, "Could not create the application window.", "MCmodsLoader", Native.MB_ICONERROR);
            return 1;
        }

        UseDarkTitleBar();
        _theme = new Theme(Native.GetDpiForWindow(_hwnd));
        CreateControls();
        Relayout(center: true);

        Native.ShowWindow(_hwnd, Native.SW_SHOW);
        Native.UpdateWindow(_hwnd);

        _sync.Attach(_hwnd);
        _sync.Post(_ => _ = OnLoadedAsync(), null);

        int exitCode = RunMessageLoop();
        _theme.Dispose();
        return exitCode;
    }

    private void RegisterWindowClass()
    {
        nint classNamePtr = Marshal.StringToHGlobalUni(ClassName);
        var wc = new Native.WNDCLASSEX
        {
            CbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            Style = 0x0002 | 0x0001, // CS_HREDRAW | CS_VREDRAW
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            HInstance = _hInstance,
            HCursor = Native.LoadCursorW(0, Native.IDC_ARROW),
            HbrBackground = 0, // painted entirely in WM_PAINT
            LpszClassName = classNamePtr,
        };

        if (Native.RegisterClassExW(ref wc) == 0)
        {
            throw new InvalidOperationException(
                $"RegisterClassEx failed (0x{Marshal.GetLastWin32Error():X8}).");
        }
    }

    private void UseDarkTitleBar()
    {
        int enabled = 1;
        // Fails harmlessly on builds older than Windows 10 20H1.
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }

    private void CreateControls()
    {
        _picker = new VersionPicker(IdVersions, _theme) { SelectionChanged = OnVersionSelected };
        _picker.Field.Surface = Theme.Card;

        foreach (var button in AllButtons) button.Create(_hwnd, _hInstance);

        _btnOpenMods.Surface = Theme.Background;
        _btnRefresh.Surface = Theme.Background;
        _btnCheckUpdates.Surface = Theme.Background;
        _btnUpdateNow.Surface = Theme.BannerBack;

        _prgProgress = Native.CreateWindowExW(0, "msctls_progress32", null,
            Native.WS_CHILD, 0, 0, 10, 10, _hwnd, 0, _hInstance, 0);
        // Custom bar colours only apply when the control is not themed.
        Native.SetWindowTheme(_prgProgress, " ", " ");
        Native.SendMessageW(_prgProgress, Native.PBM_SETRANGE32, 0, 100);
        Native.SendMessageW(_prgProgress, Native.PBM_SETBKCOLOR, 0, (nint)Theme.Background);
        Native.SendMessageW(_prgProgress, Native.PBM_SETBARCOLOR, 0, (nint)Theme.Accent);
    }

    private int RunMessageLoop()
    {
        while (true)
        {
            int result = Native.GetMessageW(out var msg, 0, 0, 0);
            if (result == 0) return (int)msg.WParam;
            if (result == -1) return 1;

            // While the version list is open it owns the keyboard: IsDialogMessage would
            // otherwise swallow Enter as "press the default button" and Escape as cancel,
            // so neither would ever reach the picker.
            bool pickerWantsKeys = _picker.IsOpen && msg.Message == Native.WM_KEYDOWN;

            // Otherwise IsDialogMessage gives Tab/Shift-Tab navigation and
            // Enter-on-focused-button for free.
            if (pickerWantsKeys || !Native.IsDialogMessageW(_hwnd, ref msg))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
        }
    }

    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
        => _instance is { } window
            ? window.WndProc(hwnd, msg, wParam, lParam)
            : Native.DefWindowProcW(hwnd, msg, wParam, lParam);

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case UiSyncContext.WM_RUN_CALLBACK:
                _sync.Drain();
                return 0;

            case Native.WM_ERASEBKGND:
                return 1; // WM_PAINT covers every pixel

            case Native.WM_PAINT:
                OnPaint();
                return 0;

            case Native.WM_DRAWITEM:
                return OnDrawItem(lParam);

            case Native.WM_COMMAND:
                OnCommand(Native.LoWord(wParam), Native.HiWord(wParam));
                return 0;

            case Native.WM_GETMINMAXINFO when _theme != null:
                OnGetMinMaxInfo(lParam);
                return 0;

            case Native.WM_DPICHANGED:
                OnDpiChanged(Native.LoWord(wParam), lParam);
                return 0;

            case Native.WM_CLOSE:
                Native.PostQuitMessage(0);
                return 0;

            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return 0;
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Windows passes the rectangle it wants the window to occupy on the new monitor.
    /// Its top-left is honoured; the size comes from the design metrics at the new DPI.
    /// </summary>
    private void OnDpiChanged(int newDpi, nint suggestedRect)
    {
        _theme.Dispose();
        _theme = new Theme((uint)newDpi);
        _picker.UpdateTheme(_theme);

        var suggested = Marshal.PtrToStructure<Native.RECT>(suggestedRect);
        var (winW, winH) = WindowSize();
        Native.SetWindowPos(_hwnd, 0, suggested.Left, suggested.Top, winW, winH,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        LayoutChildren();
        Native.InvalidateRect(_hwnd, 0, true);
    }

    /// <summary>Keeps the window from being resized below or above its fixed layout.</summary>
    private void OnGetMinMaxInfo(nint lParam)
    {
        var info = Marshal.PtrToStructure<Native.MINMAXINFO>(lParam);
        var (w, h) = WindowSize();
        info.MinTrackSize = new Native.POINT { X = w, Y = h };
        info.MaxTrackSize = new Native.POINT { X = w, Y = h };
        Marshal.StructureToPtr(info, lParam, false);
    }

    private int ClientHeight96 => Margin + HeaderH + Gap
                                + (_bannerVisible ? BannerH + Gap : 0)
                                + CardsH + Gap + ActionH + Margin;

    private (int Width, int Height) WindowSize()
    {
        var rc = Native.RECT.Xywh(0, 0, _theme.Scale(DesignWidth), _theme.Scale(ClientHeight96));
        Native.AdjustWindowRectExForDpi(ref rc, Native.WS_OVERLAPPEDWINDOW_FIXED, false, 0, _theme.Dpi);
        return (rc.Width, rc.Height);
    }

    /// <summary>
    /// Work area of the monitor the window is on, so a multi-monitor setup centres the
    /// window where the user actually is and clears the taskbar.
    /// </summary>
    private Native.RECT WorkAreaOfCurrentMonitor()
    {
        nint monitor = Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { CbSize = (uint)Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor != 0 && Native.GetMonitorInfoW(monitor, ref info)) return info.RcWork;

        return Native.RECT.Xywh(0, 0, Native.GetSystemMetrics(0), Native.GetSystemMetrics(1));
    }

    /// <summary>
    /// Resizes the frame to the current design size, and recomputes the layout inside it.
    /// The window is only ever centred at startup: re-centring later would fight the
    /// user, or any window manager that has since placed the window somewhere.
    /// </summary>
    private void Relayout(bool center = false)
    {
        var (winW, winH) = WindowSize();

        if (center)
        {
            var work = WorkAreaOfCurrentMonitor();
            Native.SetWindowPos(_hwnd, 0,
                work.Left + (work.Width - winW) / 2,
                work.Top + (work.Height - winH) / 2,
                winW, winH, Native.SWP_NOZORDER);
        }
        else
        {
            Native.SetWindowPos(_hwnd, 0, 0, 0, winW, winH, Native.SWP_NOMOVE | Native.SWP_NOZORDER);
        }

        LayoutChildren();
    }

    private void LayoutChildren()
    {
        int S(int v) => _theme.Scale(v);

        int width = S(DesignWidth);
        int left = S(Margin), right = width - S(Margin), contentW = right - left;
        int y = S(Margin);

        _rcHeader = Native.RECT.Xywh(left, y, contentW, S(HeaderH));
        y += S(HeaderH) + S(Gap);

        if (_bannerVisible)
        {
            _rcBanner = Native.RECT.Xywh(left, y, contentW, S(BannerH));
            y += S(BannerH) + S(Gap);
        }
        else
        {
            _rcBanner = default;
        }

        int cardVersionW = S(200), cardFabricW = S(285), cardGap = S(12);
        int cardPathW = contentW - cardVersionW - cardFabricW - 2 * cardGap;
        _rcCardPath = Native.RECT.Xywh(left, y, cardPathW, S(CardsH));
        _rcCardVersion = Native.RECT.Xywh(_rcCardPath.Right + cardGap, y, cardVersionW, S(CardsH));
        _rcCardFabric = Native.RECT.Xywh(_rcCardVersion.Right + cardGap, y, cardFabricW, S(CardsH));
        y += S(CardsH) + S(Gap);

        _rcAction = Native.RECT.Xywh(left, y, contentW, S(ActionH));

        // --- child controls ---
        int btnH = S(28), btnGap = S(8);
        int x = right;
        foreach (var (button, w) in new[]
                 {
                     (_btnCheckUpdates, S(126)), (_btnRefresh, S(92)), (_btnOpenMods, S(150)),
                 })
        {
            x -= w;
            button.Move(Native.RECT.Xywh(x, _rcHeader.Top + (S(HeaderH) - btnH) / 2, w, btnH));
            x -= btnGap;
        }

        // A child window is visible from the moment it is created, so the update
        // button has to be hidden while the banner is not there: it would otherwise
        // sit unmoved at the top-left of the client area as a stray green dot.
        Native.ShowWindow(_btnUpdateNow.Hwnd, _bannerVisible ? Native.SW_SHOW : Native.SW_HIDE);
        if (_bannerVisible)
        {
            int w = S(120), h = S(32);
            _btnUpdateNow.Move(Native.RECT.Xywh(_rcBanner.Right - S(14) - w,
                _rcBanner.Top + (_rcBanner.Height - h) / 2, w, h));
        }

        int pad = S(CardPad), rowH = S(28);
        int browseW = S(BrowseButtonW);
        int rowTop = _rcCardPath.Top + S(38);
        _btnBrowse.Move(Native.RECT.Xywh(_rcCardPath.Right - pad - browseW, rowTop, browseW, S(26)));

        _picker.Field.Move(Native.RECT.Xywh(_rcCardVersion.Left + pad, rowTop,
            _rcCardVersion.Width - 2 * pad, rowH));

        int fabricBtnW = S(FabricButtonW);
        _btnFabric.Move(Native.RECT.Xywh(_rcCardFabric.Right - pad - fabricBtnW, rowTop, fabricBtnW, S(26)));

        int injectW = S(InjectW), injectH = S(46), actionPad = S(ActionPad);
        _btnInject.Move(Native.RECT.Xywh(_rcAction.Right - actionPad - injectW,
            _rcAction.Top + actionPad, injectW, injectH));

        _rcProgress = Native.RECT.Xywh(_rcAction.Left + actionPad, _rcAction.Bottom - actionPad - S(10),
            _rcAction.Width - 2 * actionPad, S(10));
        Native.MoveWindow(_prgProgress, _rcProgress.Left, _rcProgress.Top, _rcProgress.Width, _rcProgress.Height, true);
        Native.ShowWindow(_prgProgress, _progressVisible ? Native.SW_SHOW : Native.SW_HIDE);
    }
}
