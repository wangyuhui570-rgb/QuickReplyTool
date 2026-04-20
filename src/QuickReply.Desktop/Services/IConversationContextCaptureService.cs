using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IConversationContextCaptureService
{
    Task<CapturedConversationContext> CaptureConversationContextAsync(
        IntPtr targetWindowHandle,
        bool includeScreenshot = false,
        bool enableOcr = false);
}
