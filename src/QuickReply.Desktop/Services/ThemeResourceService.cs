using System.Windows;
using System.Windows.Media;
using QuickReply.Desktop.Models;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace QuickReply.Desktop.Services;

public static class ThemeResourceService
{
    public static void ApplyApplicationTheme(ResourceDictionary resources, AppSettings settings)
    {
        var chrome = ParseColor(settings.SidebarColorHex, (MediaColor)MediaColorConverter.ConvertFromString("#F5F5F7"));
        var accent = ParseColor(settings.CategoryColorHex, (MediaColor)MediaColorConverter.ConvertFromString("#0071E3"));
        var white = Colors.White;
        var baseWindow = (MediaColor)MediaColorConverter.ConvertFromString("#F5F5F7");
        var baseSurface = (MediaColor)MediaColorConverter.ConvertFromString("#FFFFFF");
        var baseSurfaceAlt = (MediaColor)MediaColorConverter.ConvertFromString("#FAFAFC");
        var hairline = (MediaColor)MediaColorConverter.ConvertFromString("#D2D2D7");
        var strongHairline = (MediaColor)MediaColorConverter.ConvertFromString("#B8B8BE");
        var title = (MediaColor)MediaColorConverter.ConvertFromString("#1D1D1F");
        var text = (MediaColor)MediaColorConverter.ConvertFromString("#3A3A3C");
        var muted = (MediaColor)MediaColorConverter.ConvertFromString("#6E6E73");
        var subtleMuted = (MediaColor)MediaColorConverter.ConvertFromString("#8E8E93");
        var success = (MediaColor)MediaColorConverter.ConvertFromString("#148564");
        var danger = (MediaColor)MediaColorConverter.ConvertFromString("#C9342C");

        var windowBackground = Blend(baseWindow, chrome, 0.18);
        var surface = Blend(baseSurface, chrome, 0.08);
        var surfaceAlt = Blend(baseSurfaceAlt, chrome, 0.10);
        var line = Blend(hairline, chrome, 0.12);
        var strongLine = Blend(strongHairline, accent, 0.12);
        var accentHover = Lighten(accent, 0.05);
        var accentPressed = Darken(accent, 0.08);
        var accentSoft = Lighten(accent, 0.88);
        var accentSoftBorder = Lighten(accent, 0.62);
        var selectionHover = Blend(surfaceAlt, accent, 0.06);
        var selectionActive = Blend(surfaceAlt, accent, 0.14);
        var secondaryBackground = Blend(white, surface, 0.42);
        var secondaryHoverBackground = Blend(surfaceAlt, accent, 0.04);
        var secondaryHoverBorder = Blend(strongLine, accent, 0.20);
        var infoCardBackground = Blend(surfaceAlt, accent, 0.09);
        var infoCardBorder = Blend(line, accent, 0.18);
        var insetCardBorder = Blend(line, accent, 0.08);
        var menuBackground = Blend(surface, white, 0.30);
        var menuBorder = Blend(line, accent, 0.08);
        var menuHoverBackground = Blend(surfaceAlt, accent, 0.12);
        var menuHoverBorder = Blend(line, accent, 0.28);
        var menuSeparator = Blend(line, accent, 0.10);
        var tooltipBackground = Blend(white, surface, 0.35);
        var tooltipBorder = Blend(line, accent, 0.08);
        var dangerBackground = Lighten(danger, 0.88);
        var dangerBorder = Lighten(danger, 0.58);

        SetBrush(resources, "AppleWindowBackgroundBrush", windowBackground);
        SetBrush(resources, "AppleSurfaceBrush", surface);
        SetBrush(resources, "AppleSurfaceAltBrush", surfaceAlt);
        SetBrush(resources, "AppleLineBrush", line);
        SetBrush(resources, "AppleStrongLineBrush", strongLine);
        SetBrush(resources, "AppleAccentBrush", accent);
        SetBrush(resources, "AppleAccentHoverBrush", accentHover);
        SetBrush(resources, "AppleAccentPressedBrush", accentPressed);
        SetBrush(resources, "AppleAccentSoftBrush", accentSoft);
        SetBrush(resources, "AppleAccentSoftBorderBrush", accentSoftBorder);
        SetBrush(resources, "AppleTitleBrush", title);
        SetBrush(resources, "AppleTextBrush", text);
        SetBrush(resources, "AppleMutedBrush", muted);
        SetBrush(resources, "AppleSubtleMutedBrush", subtleMuted);
        SetBrush(resources, "AppleSuccessBrush", success);
        SetBrush(resources, "AppleSelectionHoverBrush", selectionHover);
        SetBrush(resources, "AppleSelectionActiveBrush", selectionActive);
        SetBrush(resources, "AppleProgressTrackBrush", Blend(line, surfaceAlt, 0.52));

        SetBrush(resources, "AppleTooltipBackgroundBrush", tooltipBackground);
        SetBrush(resources, "AppleTooltipBorderBrush", tooltipBorder);
        SetBrush(resources, "AppleSecondaryButtonBackgroundBrush", secondaryBackground);
        SetBrush(resources, "AppleSecondaryButtonBorderBrush", line);
        SetBrush(resources, "AppleSecondaryButtonForegroundBrush", title);
        SetBrush(resources, "AppleSecondaryButtonHoverBackgroundBrush", secondaryHoverBackground);
        SetBrush(resources, "AppleSecondaryButtonHoverBorderBrush", secondaryHoverBorder);
        SetBrush(resources, "ApplePrimaryButtonForegroundBrush", GetReadableForeground(accent));
        SetBrush(resources, "AppleTabSelectedBackgroundBrush", accentSoft);
        SetBrush(resources, "AppleTabSelectedBorderBrush", accentSoftBorder);
        SetBrush(resources, "AppleTabHoverBackgroundBrush", selectionHover);
        SetBrush(resources, "AppleBadgeBackgroundBrush", accentSoft);
        SetBrush(resources, "AppleBadgeBorderBrush", accentSoftBorder);
        SetBrush(resources, "AppleBadgeForegroundBrush", Darken(accent, 0.08));
        SetBrush(resources, "AppleInfoCardBackgroundBrush", infoCardBackground);
        SetBrush(resources, "AppleInfoCardBorderBrush", infoCardBorder);
        SetBrush(resources, "AppleInsetCardBorderBrush", insetCardBorder);
        SetBrush(resources, "AppleDangerBadgeBackgroundBrush", dangerBackground);
        SetBrush(resources, "AppleDangerBadgeBorderBrush", dangerBorder);
        SetBrush(resources, "AppleDangerBadgeForegroundBrush", Darken(danger, 0.04));
        SetBrush(resources, "AppleMenuBackgroundBrush", menuBackground, 98);
        SetBrush(resources, "AppleMenuBorderBrush", menuBorder);
        SetBrush(resources, "AppleMenuForegroundBrush", title);
        SetBrush(resources, "AppleMenuMutedBrush", muted);
        SetBrush(resources, "AppleMenuHoverBackgroundBrush", menuHoverBackground);
        SetBrush(resources, "AppleMenuHoverBorderBrush", menuHoverBorder);
        SetBrush(resources, "AppleMenuSeparatorBrush", menuSeparator);

        SetBrush(resources, "Menu.Static.Background", menuBackground, 98);
        SetBrush(resources, "Menu.Static.Border", menuBorder);
        SetBrush(resources, "Menu.Static.Foreground", title);
        SetBrush(resources, "MenuItem.Selected.Background", menuHoverBackground);
        SetBrush(resources, "MenuItem.Selected.Border", menuHoverBorder);
        SetBrush(resources, "MenuItem.Highlight.Background", menuHoverBackground);
        SetBrush(resources, "MenuItem.Highlight.Border", menuHoverBorder);
        SetBrush(resources, "MenuItem.Highlight.Disabled.Background", Blend(surfaceAlt, chrome, 0.10));
        SetBrush(resources, "MenuItem.Highlight.Disabled.Border", line);
        SetBrush(resources, "MenuItem.Disabled.Foreground", subtleMuted);
        SetBrush(resources, "MenuItem.Separator.Fill", menuSeparator);
        SetBrush(resources, "MenuItem.Separator.Stroke", menuSeparator);
        SetBrush(resources, "Menu.Popup.Background", menuBackground, 98);
        SetBrush(resources, "Menu.Popup.Border", menuBorder);
    }

    public static void ApplyMainWindowTheme(ResourceDictionary resources, AppSettings settings)
    {
        var sidebar = ParseColor(settings.SidebarColorHex, (MediaColor)MediaColorConverter.ConvertFromString("#F5F5F7"));
        var phrase = ParseColor(settings.PhraseColorHex, Colors.White);
        var accent = ParseColor(settings.CategoryColorHex, (MediaColor)MediaColorConverter.ConvertFromString("#0071E3"));
        var tag = ParseColor(settings.TagColorHex, (MediaColor)MediaColorConverter.ConvertFromString("#E8EEF9"));
        var white = Colors.White;
        var hairline = (MediaColor)MediaColorConverter.ConvertFromString("#D2D2D7");
        var textPrimary = (MediaColor)MediaColorConverter.ConvertFromString("#1D1D1F");
        var textSecondary = (MediaColor)MediaColorConverter.ConvertFromString("#6E6E73");

        SetBrush(resources, "SidebarChromeBrush", Blend(sidebar, white, 0.72), settings.SidebarOpacityPercent);
        SetBrush(resources, "SidebarOuterBrush", Lighten(sidebar, 0.03));
        SetBrush(resources, "SidebarBorderBrush", hairline);
        SetBrush(resources, "SidebarPanelBackgroundBrush", Blend(phrase, white, 0.96), Math.Max(88, Math.Min(100, settings.SidebarOpacityPercent)));
        SetBrush(resources, "SidebarPanelBorderBrush", hairline);

        SetBrush(resources, "CategoryChipBackgroundBrush", Lighten(accent, 0.86), Math.Max(82, Math.Min(100, settings.CategoryOpacityPercent)));
        SetBrush(resources, "CategoryChipBorderBrush", Lighten(accent, 0.64));
        SetBrush(resources, "CategoryChipEditingBackgroundBrush", Blend(white, accent, 0.08));
        SetBrush(resources, "CategoryChipHoverBackgroundBrush", Lighten(accent, 0.80), Math.Max(86, Math.Min(100, settings.CategoryOpacityPercent)));
        SetBrush(resources, "CategoryChipHoverBorderBrush", Lighten(accent, 0.48));
        SetBrush(resources, "CategoryChipSelectedBackgroundBrush", Lighten(accent, 0.74), Math.Max(90, Math.Min(100, settings.CategoryOpacityPercent)));
        SetBrush(resources, "CategoryChipSelectedBorderBrush", accent);
        SetBrush(resources, "CategoryChipForegroundBrush", Darken(accent, 0.08));

        SetBrush(resources, "PhraseCardBackgroundBrush", Blend(phrase, white, 0.98), settings.PhraseOpacityPercent);
        SetBrush(resources, "PhraseCardBorderBrush", hairline);
        SetBrush(resources, "PhraseCardForegroundBrush", textPrimary);
        SetBrush(resources, "PhraseCardSelectedBackgroundBrush", Lighten(accent, 0.88), Math.Max(92, Math.Min(100, settings.TagOpacityPercent)));
        SetBrush(resources, "PhraseCardSelectedBorderBrush", Lighten(accent, 0.18));
        SetBrush(resources, "PhraseInlineEditorBackgroundBrush", Blend(phrase, white, 0.98), settings.PhraseOpacityPercent);
        SetBrush(resources, "PhraseInlineEditorBorderBrush", Lighten(accent, 0.56));
        SetBrush(resources, "PhraseTooltipBackgroundBrush", white, 96);
        SetBrush(resources, "PhraseTooltipBorderBrush", hairline);
        SetBrush(resources, "PhraseArrowBackgroundBrush", accent);
        SetBrush(resources, "PhraseArrowBorderBrush", accent);
        SetBrush(resources, "PhraseArrowForegroundBrush", white);
        SetBrush(resources, "PhraseArrowHoverBackgroundBrush", Lighten(accent, 0.06));
        SetBrush(resources, "PhraseArrowHoverBorderBrush", Lighten(accent, 0.06));
        SetBrush(resources, "PhraseImageBadgeBackgroundBrush", Lighten(accent, 0.84));
        SetBrush(resources, "PhraseImageBadgeBorderBrush", Lighten(accent, 0.60));
        SetBrush(resources, "PhraseImageBadgeForegroundBrush", Darken(accent, 0.10));
        SetBrush(resources, "FooterSecondaryBackgroundBrush", white, 88);
        SetBrush(resources, "FooterSecondaryBorderBrush", hairline);
        SetBrush(resources, "FooterSecondaryForegroundBrush", textPrimary);
        SetBrush(resources, "FooterPrimaryBackgroundBrush", accent);
        SetBrush(resources, "FooterPrimaryBorderBrush", accent);
        SetBrush(resources, "FooterPrimaryForegroundBrush", white);

        SetBrush(resources, "TagBadgeBackgroundBrush", tag, settings.TagOpacityPercent);
        SetBrush(resources, "TagBadgeBorderBrush", Blend(tag, hairline, 0.45));
        SetBrush(resources, "TagBadgeForegroundBrush", textSecondary);
    }

    public static void ApplySearchOverlayTheme(ResourceDictionary resources, AppSettings settings)
    {
        var overlay = ParseColor(settings.SearchOverlayColorHex, Colors.White);
        var input = ParseColor(settings.SearchInputColorHex, Colors.White);
        var accent = ParseColor(settings.CategoryColorHex, (MediaColor)MediaColorConverter.ConvertFromString("#0071E3"));
        var tag = ParseColor(settings.TagColorHex, (MediaColor)MediaColorConverter.ConvertFromString("#E8EEF9"));
        var white = Colors.White;
        var hairline = (MediaColor)MediaColorConverter.ConvertFromString("#D2D2D7");
        var textPrimary = (MediaColor)MediaColorConverter.ConvertFromString("#1D1D1F");
        var textSecondary = (MediaColor)MediaColorConverter.ConvertFromString("#6E6E73");

        SetBrush(resources, "SearchOverlayBackgroundBrush", Blend(overlay, white, 0.98), settings.SearchOverlayOpacityPercent);
        SetBrush(resources, "SearchOverlayBorderBrush", hairline);
        SetBrush(resources, "SearchOverlayForegroundBrush", textPrimary);
        SetBrush(resources, "SearchOverlayMutedBrush", textSecondary);
        SetBrush(resources, "SearchInputBackgroundBrush", Blend(input, white, 0.98), settings.SearchInputOpacityPercent);
        SetBrush(resources, "SearchInputBorderBrush", hairline);
        SetBrush(resources, "SearchInputForegroundBrush", textPrimary);
        SetBrush(resources, "SearchInputMutedBrush", (MediaColor)MediaColorConverter.ConvertFromString("#8E8E93"));
        SetBrush(resources, "SearchResultBackgroundBrush", white, Math.Min(100, settings.SearchOverlayOpacityPercent));
        SetBrush(resources, "SearchResultBorderBrush", hairline);
        SetBrush(resources, "SearchResultHoverBackgroundBrush", Blend(white, accent, 0.05));
        SetBrush(resources, "SearchResultHoverBorderBrush", Blend(hairline, accent, 0.20));
        SetBrush(resources, "SearchResultSelectedBackgroundBrush", Blend(white, accent, 0.30));
        SetBrush(resources, "SearchResultSelectedBorderBrush", accent);
        SetBrush(resources, "SearchResultSelectedAccentBrush", accent);
        SetBrush(resources, "SearchPillBackgroundBrush", tag, settings.TagOpacityPercent);
        SetBrush(resources, "SearchPillBorderBrush", Blend(tag, hairline, 0.42));
        SetBrush(resources, "SearchPillForegroundBrush", textSecondary);
        SetBrush(resources, "FilePillBackgroundBrush", (MediaColor)MediaColorConverter.ConvertFromString("#E8F5EC"));
        SetBrush(resources, "FilePillBorderBrush", (MediaColor)MediaColorConverter.ConvertFromString("#B6DCC0"));
        SetBrush(resources, "FilePillForegroundBrush", (MediaColor)MediaColorConverter.ConvertFromString("#1F6F43"));
        SetBrush(resources, "RecommendationPanelBackgroundBrush", Lighten(accent, 0.92));
        SetBrush(resources, "RecommendationPanelBorderBrush", Lighten(accent, 0.70));
        SetBrush(resources, "RecommendationPanelForegroundBrush", Darken(accent, 0.12));
        SetBrush(resources, "RecommendationPanelMutedBrush", textSecondary);
        SetBrush(resources, "RecommendationCardBackgroundBrush", white, 86);
        SetBrush(resources, "RecommendationCardBorderBrush", hairline);
        SetBrush(resources, "RecommendationBadgeBackgroundBrush", Lighten(accent, 0.84));
        SetBrush(resources, "RecommendationBadgeBorderBrush", Lighten(accent, 0.58));
        SetBrush(resources, "RecommendationBadgeForegroundBrush", Darken(accent, 0.10));
    }

    private static MediaColor ParseColor(string value, MediaColor fallback)
    {
        try
        {
            var parsed = MediaColorConverter.ConvertFromString(value);
            if (parsed is MediaColor color)
            {
                return color;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static void SetBrush(ResourceDictionary resources, object key, MediaColor color, int opacityPercent = 100)
    {
        var brushColor = ApplyOpacity(color, opacityPercent);

        if (TryGetMutableBrush(resources, key, out var existing))
        {
            existing.Color = brushColor;
            existing.Opacity = 1d;
            return;
        }

        resources[key] = CreateBrush(brushColor);
    }

    private static bool TryGetMutableBrush(ResourceDictionary resources, object key, out SolidColorBrush brush)
    {
        brush = null!;

        try
        {
            if (resources[key] is SolidColorBrush existing && !existing.IsFrozen)
            {
                brush = existing;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static SolidColorBrush CreateBrush(MediaColor color)
    {
        return new SolidColorBrush(color);
    }

    private static MediaColor ApplyOpacity(MediaColor color, int opacityPercent)
    {
        var clampedOpacity = Math.Clamp(opacityPercent, 0, 100);
        color.A = (byte)Math.Round(255d * clampedOpacity / 100d, MidpointRounding.AwayFromZero);
        return color;
    }

    private static MediaColor Lighten(MediaColor color, double factor)
    {
        factor = Math.Clamp(factor, 0d, 1d);
        return MediaColor.FromRgb(
            (byte)Math.Round(color.R + (255 - color.R) * factor, MidpointRounding.AwayFromZero),
            (byte)Math.Round(color.G + (255 - color.G) * factor, MidpointRounding.AwayFromZero),
            (byte)Math.Round(color.B + (255 - color.B) * factor, MidpointRounding.AwayFromZero));
    }

    private static MediaColor Darken(MediaColor color, double factor)
    {
        factor = Math.Clamp(factor, 0d, 1d);
        return MediaColor.FromRgb(
            (byte)Math.Round(color.R * (1d - factor), MidpointRounding.AwayFromZero),
            (byte)Math.Round(color.G * (1d - factor), MidpointRounding.AwayFromZero),
            (byte)Math.Round(color.B * (1d - factor), MidpointRounding.AwayFromZero));
    }

    private static MediaColor Blend(MediaColor baseColor, MediaColor overlayColor, double overlayWeight)
    {
        overlayWeight = Math.Clamp(overlayWeight, 0d, 1d);
        var baseWeight = 1d - overlayWeight;
        return MediaColor.FromRgb(
            (byte)Math.Round((baseColor.R * baseWeight) + (overlayColor.R * overlayWeight), MidpointRounding.AwayFromZero),
            (byte)Math.Round((baseColor.G * baseWeight) + (overlayColor.G * overlayWeight), MidpointRounding.AwayFromZero),
            (byte)Math.Round((baseColor.B * baseWeight) + (overlayColor.B * overlayWeight), MidpointRounding.AwayFromZero));
    }

    private static MediaColor GetReadableForeground(MediaColor background)
    {
        var luminance = (0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B);
        return luminance >= 168
            ? (MediaColor)MediaColorConverter.ConvertFromString("#0F172A")
            : Colors.White;
    }
}
