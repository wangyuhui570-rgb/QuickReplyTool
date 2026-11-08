using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class ClipboardPasteTrackerTests
{
    [Fact]
    public void ReadAfterPaste_MarksThePayloadConsumed()
    {
        var tracker = new ClipboardPasteTracker();
        var data = new TrackingDataObject(CreateText("文字"), tracker);

        tracker.NotePasteIssued();
        Assert.Equal("文字", data.GetData(System.Windows.DataFormats.UnicodeText));

        Assert.True(tracker.Consumed.IsCompleted);
        Assert.False(tracker.ReadBeforePaste);
    }

    [Fact]
    public void ReadBeforePaste_IsNotTakenForThePaste()
    {
        // A clipboard manager reading right after the write must not count as the chat app's paste.
        var tracker = new ClipboardPasteTracker();
        var data = new TrackingDataObject(CreateText("文字"), tracker);

        data.GetData(System.Windows.DataFormats.UnicodeText);

        Assert.False(tracker.Consumed.IsCompleted);
        Assert.True(tracker.ReadBeforePaste);
    }

    [Fact]
    public void FormatQueries_DoNotCountAsARead()
    {
        var tracker = new ClipboardPasteTracker();
        var data = new TrackingDataObject(CreateText("文字"), tracker);
        tracker.NotePasteIssued();

        Assert.True(data.GetDataPresent(System.Windows.DataFormats.UnicodeText));
        Assert.NotEmpty(data.GetFormats());

        Assert.False(tracker.Consumed.IsCompleted);
    }

    [Fact]
    public void Tracker_TimesTheLastReadAfterThePaste()
    {
        long now = 1000;
        var tracker = new ClipboardPasteTracker(() => now);
        var data = new TrackingDataObject(CreateText("文字"), tracker);

        tracker.NotePasteIssued();
        Assert.Null(tracker.MillisecondsSinceLastRead);
        now = 1030;
        data.GetData(System.Windows.DataFormats.UnicodeText);
        now = 1100;

        Assert.Equal(100, tracker.MillisecondsSincePaste);
        Assert.Equal(70, tracker.MillisecondsSinceLastRead);
        Assert.Equal(1, tracker.ReadsAfterPaste);
    }

    [Fact]
    public void Policy_AnEarlyReadIsNotEnough()
    {
        // 千牛 / 咚咚: a read 30 ms after Ctrl+V is the clipboard-change handler, not the paste.
        // 2.1.22 replaced the clipboard right then and the text paste picked up the image.
        Assert.False(PasteGapPolicy.MayReplace(sincePasteMs: 40, sinceLastReadMs: 10, readBeforePaste: false, timeoutMs: 900));
        Assert.False(PasteGapPolicy.MayReplace(sincePasteMs: 300, sinceLastReadMs: 270, readBeforePaste: false, timeoutMs: 900));
    }

    [Fact]
    public void Policy_WaitsForQuietAfterTheLastRead()
    {
        Assert.False(PasteGapPolicy.MayReplace(sincePasteMs: 500, sinceLastReadMs: 40, readBeforePaste: false, timeoutMs: 900));
        Assert.True(PasteGapPolicy.MayReplace(sincePasteMs: 500, sinceLastReadMs: PasteGapPolicy.QuietAfterReadMs, readBeforePaste: false, timeoutMs: 900));
    }

    [Fact]
    public void Policy_UnobservedPasteWaitsLongerAndTimeoutWins()
    {
        Assert.False(PasteGapPolicy.MayReplace(sincePasteMs: 600, sinceLastReadMs: null, readBeforePaste: false, timeoutMs: 900));
        Assert.False(PasteGapPolicy.MayReplace(sincePasteMs: 600, sinceLastReadMs: null, readBeforePaste: true, timeoutMs: 900));
        Assert.True(PasteGapPolicy.MayReplace(sincePasteMs: PasteGapPolicy.UnobservedFallbackMs, sinceLastReadMs: null, readBeforePaste: true, timeoutMs: 900));
        Assert.True(PasteGapPolicy.MayReplace(sincePasteMs: 900, sinceLastReadMs: 5, readBeforePaste: false, timeoutMs: 900));
    }

    private static System.Windows.DataObject CreateText(string text)
    {
        var data = new System.Windows.DataObject();
        data.SetData(System.Windows.DataFormats.UnicodeText, text);
        return data;
    }
}
