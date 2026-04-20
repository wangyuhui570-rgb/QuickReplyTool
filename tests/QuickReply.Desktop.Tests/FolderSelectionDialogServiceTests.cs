using System.Runtime.InteropServices;
using QuickReply.Desktop.Services;
using QuickReply.Desktop.Tests.TestSupport;
using Forms = System.Windows.Forms;

namespace QuickReply.Desktop.Tests;

public sealed class FolderSelectionDialogServiceTests
{
    [Fact]
    public void SelectFolder_ReturnsSelectedPathWhenDialogIsConfirmed()
    {
        using var scope = new TestAppDataScope();
        var existingDirectory = scope.LibraryRootPath;
        var fakeDialog = new FakeFolderBrowserDialog
        {
            ShowDialogResult = Forms.DialogResult.OK,
            ResultSelectedPath = @"C:\data\library"
        };
        var service = new FolderSelectionDialogService(() => fakeDialog);

        var result = service.SelectFolder("选择目录", existingDirectory);

        Assert.True(result.Confirmed);
        Assert.False(result.HasError);
        Assert.Equal(@"C:\data\library", result.SelectedPath);
        Assert.Equal(existingDirectory, fakeDialog.SelectedPathSeenByShowDialog);
    }

    [Fact]
    public void SelectFolder_ClearsMissingInitialPathBeforeShowingDialog()
    {
        var fakeDialog = new FakeFolderBrowserDialog
        {
            ShowDialogResult = Forms.DialogResult.Cancel
        };
        var service = new FolderSelectionDialogService(() => fakeDialog);

        _ = service.SelectFolder("选择目录", @"C:\does-not-exist\folder");

        Assert.Equal(string.Empty, fakeDialog.SelectedPathSeenByShowDialog);
    }

    [Fact]
    public void SelectFolder_ReturnsRecoverableErrorWhenDialogThrowsComException()
    {
        var fakeDialog = new FakeFolderBrowserDialog
        {
            ExceptionToThrow = new COMException("E_FAIL", unchecked((int)0x80004005))
        };
        var service = new FolderSelectionDialogService(() => fakeDialog);

        var result = service.SelectFolder("选择目录", string.Empty);

        Assert.False(result.Confirmed);
        Assert.True(result.HasError);
        Assert.Contains("打开文件夹选择器失败", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("E_FAIL", result.ErrorMessage, StringComparison.Ordinal);
    }

    private sealed class FakeFolderBrowserDialog : IFolderBrowserDialogAdapter
    {
        public string Description { get; set; } = string.Empty;

        public bool UseDescriptionForTitle { get; set; }

        public bool ShowNewFolderButton { get; set; }

        public string SelectedPath { get; set; } = string.Empty;

        public Forms.DialogResult ShowDialogResult { get; set; } = Forms.DialogResult.Cancel;

        public string ResultSelectedPath { get; set; } = string.Empty;

        public Exception? ExceptionToThrow { get; set; }

        public string SelectedPathSeenByShowDialog { get; private set; } = string.Empty;

        public Forms.DialogResult ShowDialog()
        {
            SelectedPathSeenByShowDialog = SelectedPath;
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            if (ShowDialogResult == Forms.DialogResult.OK)
            {
                SelectedPath = ResultSelectedPath;
            }

            return ShowDialogResult;
        }

        public void Dispose()
        {
        }
    }
}
