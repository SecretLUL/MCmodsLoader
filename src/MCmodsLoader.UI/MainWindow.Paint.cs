using System.Runtime.InteropServices;
using MCmodsLoader.UI.Interop;

namespace MCmodsLoader.UI;

/// <summary>
/// Everything the window draws itself. Only the buttons, the version drop-down and
/// the progress bar are real controls; labels and cards are painted directly, which
/// avoids a dozen child windows that would each need their own colour handling.
/// </summary>
internal sealed partial class MainWindow
{
    private void OnPaint()
    {
        nint hdc = Native.BeginPaint(_hwnd, out var ps);
        try
        {
            Native.GetClientRect(_hwnd, out var client);

            // Draw into a memory bitmap and blit once, so nothing flickers.
            nint memDc = Native.CreateCompatibleDC(hdc);
            nint bitmap = Native.CreateCompatibleBitmap(hdc, client.Width, client.Height);
            nint oldBitmap = Native.SelectObject(memDc, bitmap);

            Gdi.Fill(memDc, client, _theme.BackgroundBrush);
            PaintHeader(memDc);
            if (_bannerVisible) PaintBanner(memDc);
            PaintCards(memDc);
            PaintAction(memDc);

            Native.BitBlt(hdc, 0, 0, client.Width, client.Height, memDc, 0, 0, Native.SRCCOPY);

            Native.SelectObject(memDc, oldBitmap);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memDc);
        }
        finally
        {
            Native.EndPaint(_hwnd, ref ps);
        }
    }

    private void PaintHeader(nint dc)
    {
        int S(int v) => _theme.Scale(v);

        const string title = "MCmodsLoader";
        int titleW = Gdi.MeasureWidth(dc, title, _theme.FontHeading);
        var titleRect = Native.RECT.Xywh(_rcHeader.Left, _rcHeader.Top, titleW, _rcHeader.Height);
        Gdi.Text(dc, title, titleRect, _theme.FontHeading, Theme.TextPrimary,
            Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE);

        // Version pill, sized to its text.
        string version = _appVersionText;
        int versionW = Gdi.MeasureWidth(dc, version, _theme.FontSmallBold) + S(16);
        int pillH = S(20);
        var pill = Native.RECT.Xywh(titleRect.Right + S(10),
            _rcHeader.Top + (_rcHeader.Height - pillH) / 2, versionW, pillH);
        Gdi.RoundRect(dc, pill, S(6), Theme.Card, Theme.CardBorder);
        Gdi.Text(dc, version, pill, _theme.FontSmallBold, Theme.TextMuted,
            Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE);
    }

    private void PaintBanner(nint dc)
    {
        int S(int v) => _theme.Scale(v);

        Gdi.RoundRect(dc, _rcBanner, S(8), Theme.BannerBack, Theme.BannerBorder);

        int pad = S(14);
        int textW = _rcBanner.Width - 2 * pad - S(120) - S(12);
        var titleRect = Native.RECT.Xywh(_rcBanner.Left + pad, _rcBanner.Top + S(10), textW, S(18));
        var detailRect = Native.RECT.Xywh(_rcBanner.Left + pad, _rcBanner.Top + S(29), textW, S(16));

        Gdi.Text(dc, _bannerTitle, titleRect, _theme.FontBodyBold, Theme.BannerTitle,
            Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_END_ELLIPSIS);
        Gdi.Text(dc, _bannerDetails, detailRect, _theme.FontSmall, Theme.BannerText,
            Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_END_ELLIPSIS);
    }

    private void PaintCards(nint dc)
    {
        int S(int v) => _theme.Scale(v);
        int pad = S(CardPad);

        foreach (var card in new[] { _rcCardPath, _rcCardVersion, _rcCardFabric })
        {
            Gdi.RoundRect(dc, card, S(8), Theme.Card, Theme.CardBorder);
        }

        void Label(Native.RECT card, string text) =>
            Gdi.Text(dc, text, Native.RECT.Xywh(card.Left + pad, card.Top + S(12), card.Width - 2 * pad, S(14)),
                _theme.FontLabel, Theme.TextMuted, Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE);

        Label(_rcCardPath, "MINECRAFT DIRECTORY");
        Label(_rcCardVersion, "TARGET VERSION");
        Label(_rcCardFabric, "FABRIC LOADER");

        // Path, ellipsised in the middle so the tail stays readable.
        int pathW = _rcCardPath.Width - 2 * pad - S(BrowseButtonW) - S(8);
        Gdi.Text(dc, _minecraftPath,
            Native.RECT.Xywh(_rcCardPath.Left + pad, _rcCardPath.Top + S(38), pathW, S(26)),
            _theme.FontBody, Theme.TextPrimary,
            Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_PATH_ELLIPSIS);

        int fabricW = _rcCardFabric.Width - 2 * pad - S(FabricButtonW) - S(8);
        Gdi.Text(dc, _fabricStatusText,
            Native.RECT.Xywh(_rcCardFabric.Left + pad, _rcCardFabric.Top + S(38), fabricW, S(26)),
            _theme.FontBodyBold, _fabricStatusColor,
            Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_END_ELLIPSIS);
    }

    private void PaintAction(nint dc)
    {
        int S(int v) => _theme.Scale(v);

        Gdi.RoundRect(dc, _rcAction, S(8), Theme.Card, Theme.CardBorder);

        int pad = S(ActionPad);
        int textLeft = _rcAction.Left + pad;
        int textW = _rcAction.Width - 2 * pad - S(InjectW) - S(16);

        int statusW = Gdi.MeasureWidth(dc, _statusText, _theme.FontTitle);
        statusW = Math.Min(statusW, textW);
        var statusRect = Native.RECT.Xywh(textLeft, _rcAction.Top + S(18), statusW, S(22));
        Gdi.Text(dc, _statusText, statusRect, _theme.FontTitle, Theme.TextPrimary,
            Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_END_ELLIPSIS);

        // Mod-count pill, right after the status line.
        int badgeW = Gdi.MeasureWidth(dc, _modCountText, _theme.FontSmallBold) + S(16);
        int badgeH = S(20);
        var badge = Native.RECT.Xywh(statusRect.Right + S(10),
            statusRect.Top + (statusRect.Height - badgeH) / 2, badgeW, badgeH);
        if (badge.Right <= textLeft + textW)
        {
            Gdi.RoundRect(dc, badge, S(10), Theme.Badge, Theme.CardBorder);
            Gdi.Text(dc, _modCountText, badge, _theme.FontSmallBold, _modCountColor,
                Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE);
        }

        Gdi.Text(dc, _subStatusText,
            Native.RECT.Xywh(textLeft, _rcAction.Top + S(44), textW, S(34)),
            _theme.FontSmall, Theme.TextMuted,
            Native.DT_LEFT | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_END_ELLIPSIS);
    }

    private nint OnDrawItem(nint lParam)
    {
        var item = Marshal.PtrToStructure<Native.DRAWITEMSTRUCT>(lParam);

        if (item.CtlType == Native.ODT_BUTTON)
        {
            UiButton.Find(item.HwndItem)?.Draw(_theme, item);
            return 1;
        }

        return 0;
    }
}
