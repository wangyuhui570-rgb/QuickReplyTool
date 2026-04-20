using System.IO;
using Forms = System.Windows.Forms;

namespace QuickReply.Desktop.Services;

internal interface IFolderBrowserDialogAdapter : IDisposable
{
    string Description { get; set; }

    bool UseDescriptionForTitle { get; set; }

    bool ShowNewFolderButton { get; set; }

    string SelectedPath { get; set; }

    Forms.DialogResult ShowDialog();
}

internal sealed class FolderBrowserDialogAdapter : IFolderBrowserDialogAdapter
{
    private readonly Forms.FolderBrowserDialog _dialog = new();

    public string Description
    {
        get => _dialog.Description;
        set => _dialog.Description = value;
    }

    public bool UseDescriptionForTitle
    {
        get => _dialog.UseDescriptionForTitle;
        set => _dialog.UseDescriptionForTitle = value;
    }

    public bool ShowNewFolderButton
    {
        get => _dialog.ShowNewFolderButton;
        set => _dialog.ShowNewFolderButton = value;
    }

    public string SelectedPath
    {
        get => _dialog.SelectedPath;
        set => _dialog.SelectedPath = value;
    }

    public Forms.DialogResult ShowDialog()
    {
        return _dialog.ShowDialog();
    }

    public void Dispose()
    {
        _dialog.Dispose();
    }
}

internal sealed record FolderSelectionResult(bool Confirmed, string SelectedPath, string ErrorMessage)
{
    public static FolderSelectionResult Cancelled { get; } = new(false, string.Empty, string.Empty);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
}

internal sealed class FolderSelectionDialogService
{
    private readonly Func<IFolderBrowserDialogAdapter> _dialogFactory;

    public FolderSelectionDialogService(Func<IFolderBrowserDialogAdapter>? dialogFactory = null)
    {
        _dialogFactory = dialogFactory ?? (() => new FolderBrowserDialogAdapter());
    }

    public FolderSelectionResult SelectFolder(string description, string initialPath)
    {
        try
        {
            using var dialog = _dialogFactory();
            dialog.Description = description;
            dialog.UseDescriptionForTitle = true;
            dialog.ShowNewFolderButton = true;
            dialog.SelectedPath = NormalizeInitialPath(initialPath);

            if (dialog.ShowDialog() != Forms.DialogResult.OK)
            {
                return FolderSelectionResult.Cancelled;
            }

            return new FolderSelectionResult(true, dialog.SelectedPath?.Trim() ?? string.Empty, string.Empty);
        }
        catch (Exception ex)
        {
            return new FolderSelectionResult(
                false,
                string.Empty,
                $"打开文件夹选择器失败：{ex.Message}");
        }
    }

    private static string NormalizeInitialPath(string initialPath)
    {
        if (string.IsNullOrWhiteSpace(initialPath))
        {
            return string.Empty;
        }

        var trimmedPath = initialPath.Trim();
        if (!Path.IsPathRooted(trimmedPath))
        {
            return string.Empty;
        }

        return Directory.Exists(trimmedPath)
            ? trimmedPath
            : string.Empty;
    }
}
