using System.Windows.Input;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class SearchOverlayShortcutPolicyTests
{
    [Fact]
    public void IsSearchTextSendShortcut_RespectsConfiguredMode()
    {
        Assert.True(SearchOverlayShortcutPolicy.IsSearchTextSendShortcut(
            Key.Enter,
            ModifierKeys.Control,
            searchTextHasFocus: true,
            SearchTextSendShortcutMode.CtrlEnter));
        Assert.True(SearchOverlayShortcutPolicy.IsSearchTextSendShortcut(
            Key.Enter,
            ModifierKeys.Alt,
            searchTextHasFocus: true,
            SearchTextSendShortcutMode.AltEnter));
        Assert.True(SearchOverlayShortcutPolicy.IsSearchTextSendShortcut(
            Key.Enter,
            ModifierKeys.Control | ModifierKeys.Shift,
            searchTextHasFocus: true,
            SearchTextSendShortcutMode.CtrlShiftEnter));
        Assert.False(SearchOverlayShortcutPolicy.IsSearchTextSendShortcut(
            Key.Enter,
            ModifierKeys.Shift,
            searchTextHasFocus: true,
            SearchTextSendShortcutMode.CtrlEnter));
    }

    [Fact]
    public void IsSearchTextSendAndSaveShortcut_RequiresCtrlShiftS()
    {
        Assert.True(SearchOverlayShortcutPolicy.IsSearchTextSendAndSaveShortcut(
            Key.S,
            ModifierKeys.Control | ModifierKeys.Shift,
            searchTextHasFocus: true,
            isRepeat: false));
        Assert.False(SearchOverlayShortcutPolicy.IsSearchTextSendAndSaveShortcut(
            Key.LeftShift,
            ModifierKeys.Shift,
            searchTextHasFocus: true,
            isRepeat: false));
        Assert.False(SearchOverlayShortcutPolicy.IsSearchTextSendAndSaveShortcut(
            Key.S,
            ModifierKeys.Control | ModifierKeys.Shift,
            searchTextHasFocus: true,
            isRepeat: true));
    }
}
