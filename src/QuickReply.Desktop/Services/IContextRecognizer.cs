using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IContextRecognizer
{
    ConversationRecommendationSnapshot RecognizeContext(CapturedConversationContext capturedContext);
}
