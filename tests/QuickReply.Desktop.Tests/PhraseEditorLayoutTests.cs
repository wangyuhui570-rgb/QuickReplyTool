using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using QuickReply.Desktop;
using Xunit;

namespace QuickReply.Desktop.Tests;

public sealed class PhraseEditorLayoutTests
{
    [Fact]
    public void EditorTextViewportShowsChineseTextAndMultilineContent()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var editor = new PhraseEditorWindow("新增短语", "新增", "退换货说明", "您好\n请查看图片");
                foreach (var fontSize in new[] { 14d, 18d, 24d })
                {
                    foreach (var key in new[] { "EditorTextBoxStyle", "EditorMultiLineTextBoxStyle" })
                    {
                        var box = new TextBox
                        {
                            Style = (Style)editor.Resources[key],
                            Text = key == "EditorTextBoxStyle" ? "退换货说明" : "您好\n请查看图片",
                            FontSize = fontSize,
                            Width = 600
                        };
                        box.Measure(new Size(600, double.PositiveInfinity));
                        box.Arrange(new Rect(new Point(), box.DesiredSize));
                        box.UpdateLayout();
                        var host = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
                        Assert.True(host.ViewportHeight >= fontSize, $"{key}: viewport {host.ViewportHeight}, font {fontSize}");
                        var first = box.GetRectFromCharacterIndex(0);
                        Assert.False(first.IsEmpty);
                        Assert.True(first.Height >= fontSize);
                        Assert.True(first.Top >= 0 && first.Bottom <= box.ActualHeight);
                        if (box.AcceptsReturn)
                        {
                            var second = box.GetRectFromCharacterIndex(3);
                            Assert.True(second.Top > first.Top);
                            Assert.True(second.Bottom <= box.ActualHeight);
                        }
                    }
                }
                app.Shutdown();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF layout test timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
