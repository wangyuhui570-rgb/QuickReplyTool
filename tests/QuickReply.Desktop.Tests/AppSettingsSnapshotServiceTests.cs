using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class AppSettingsSnapshotServiceTests
{
    [Fact]
    public void CloneAndApply_RoundTripRepresentativeSettings()
    {
        var source = new AppSettings
        {
            SearchFocusHotkeyMode = SearchFocusHotkeyMode.DoubleCtrl,
            SearchTextSendShortcutMode = SearchTextSendShortcutMode.CtrlShiftEnter,
            ServerBaseUrl = "https://sync.example.com",
            SyncApiToken = "token",
            LastPublishedKnowledgeRevision = 42,
            PhraseImageSendMode = PhraseImageSendMode.ImageThenTextThenSend,
            FileLibraryFolderPath = @"D:\Library",
            RestoreFocusDelayMs = 180,
            SearchOverlayWidthPercent = 72,
            SidebarWidth = 360,
            PhraseFontSize = 15,
            SearchInputColorHex = "#123456"
        };

        var clone = AppSettingsSnapshotService.Clone(source);
        var applied = new AppSettings();
        AppSettingsSnapshotService.Apply(applied, source);

        Assert.Equal(source.SearchFocusHotkeyMode, clone.SearchFocusHotkeyMode);
        Assert.Equal(source.ServerBaseUrl, clone.ServerBaseUrl);
        Assert.Equal(source.LastPublishedKnowledgeRevision, clone.LastPublishedKnowledgeRevision);
        Assert.Equal(source.SearchInputColorHex, clone.SearchInputColorHex);
        Assert.Equal(source.SyncApiToken, applied.SyncApiToken);
        Assert.Equal(source.SidebarWidth, applied.SidebarWidth);

        source.ServerBaseUrl = "https://changed.example.com";
        Assert.Equal("https://sync.example.com", clone.ServerBaseUrl);
    }
}
