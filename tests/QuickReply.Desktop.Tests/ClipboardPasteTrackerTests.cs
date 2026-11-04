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

    private static System.Windows.DataObject CreateText(string text)
    {
        var data = new System.Windows.DataObject();
        data.SetData(System.Windows.DataFormats.UnicodeText, text);
        return data;
    }
}
