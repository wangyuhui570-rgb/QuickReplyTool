using System.Windows.Input;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal static class SearchOverlayShortcutPolicy
{
    public static bool IsSearchTextSendShortcut(
        Key key,
        ModifierKeys modifiers,
        bool searchTextHasFocus,
        SearchTextSendShortcutMode shortcutMode)
    {
        if (key != Key.Enter || !searchTextHasFocus)
        {
            return false;
        }

        return shortcutMode switch
        {
            SearchTextSendShortcutMode.AltEnter => modifiers == ModifierKeys.Alt,
            SearchTextSendShortcutMode.CtrlShiftEnter => modifiers == (ModifierKeys.Control | ModifierKeys.Shift),
            _ => modifiers == ModifierKeys.Control
        };
    }

    public static bool IsSearchTextSendAndSaveShortcut(
        Key key,
        ModifierKeys modifiers,
        bool searchTextHasFocus,
        bool isRepeat)
    {
        return !isRepeat &&
               searchTextHasFocus &&
               key == Key.S &&
               modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
    }
}
