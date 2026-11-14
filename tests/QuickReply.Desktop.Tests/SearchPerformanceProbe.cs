using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;
using QuickReply.Desktop.ViewModels;

namespace QuickReply.Desktop.Tests;

internal static class SearchPerformanceProbe
{
    // Called on the existing layout test's STA thread while App resources are available.
    public static void Measure(Action<string> write)
    {
        foreach (var count in new[] { 100, 5000 })
        {
            var scope = new MainViewModelTestScope();
            try
            {
                var category = new ReplyCategory { Name = "测试" };
                for (var index = 0; index < count; index++)
                {
                    category.Phrases.Add(new ReplyPhrase
                    {
                        CategoryId = category.Id,
                        Title = $"测试 {index:D5}",
                        Content = $"正文 {index}",
                        SortOrder = index
                    });
                }
                scope.Main.Categories.Add(category);
                scope.Main.SelectedCategory = category;
                var overlayModel = new SearchOverlayViewModel(scope.Main);
                var overlay = new SearchOverlayWindow(new FakeExternalWorkbenchService(), overlayModel);
                var window = new MainWindow(scope.Main, new GlobalSearchFocusService(), overlay);
                var list = (ListBox)window.FindName("PhraseList");
                void Layout()
                {
                    list.Measure(new Size(420, 600));
                    list.Arrange(new Rect(0, 0, 420, 600));
                    list.UpdateLayout();
                }
                Layout();
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
                var notifications = 0;
                scope.Main.FilteredPhrases.CollectionChanged += (_, _) => notifications++;
                foreach (var query in new[] { "测", "测试", "测试 1", "测试" })
                {
                    notifications = 0;
                    var timer = Stopwatch.StartNew();
                    scope.Main.SearchText = query;
                    Layout();
                    timer.Stop();
                    Assert.Equal(scope.Main.SearchPhrases(query, count), scope.Main.FilteredPhrases);
                    Assert.Contains(scope.Main.SelectedPhrase, scope.Main.FilteredPhrases);
                    write($"SEARCH count={count} query={query} results={list.Items.Count} notifications={notifications} elapsedMs={timer.Elapsed.TotalMilliseconds:F3}");
                }

                var scrollTimer = Stopwatch.StartNew();
                list.ScrollIntoView(list.Items[list.Items.Count - 1]);
                Layout();
                scrollTimer.Stop();
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1));
                write($"SCROLL count={count} elapsedMs={scrollTimer.Elapsed.TotalMilliseconds:F3}");
                window.Close();
            }
            finally
            {
                scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }
}
