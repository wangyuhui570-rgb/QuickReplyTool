using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IMessageComposeService
{
    Task<ReplyActionResult> CopyToClipboardAsync(string text, IReadOnlyCollection<string>? imagePaths = null);

    Task<ReplyActionResult> ExecuteAsync(string text, AppSettings settings, ComposeActionMode mode, IReadOnlyCollection<string>? imagePaths = null);

    Task<ReplyActionResult> CopyFilesToClipboardAsync(IReadOnlyCollection<string> filePaths);

    Task<ReplyActionResult> ExecuteFilesAsync(IReadOnlyCollection<string> filePaths, AppSettings settings, ComposeActionMode mode);
}
