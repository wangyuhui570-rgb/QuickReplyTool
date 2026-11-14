using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IJdWorkbenchChatClient : IDisposable
{
    Task<IReadOnlyList<JdChatSession>> QuerySessionsAsync(DateTime startTime, DateTime endTime, CancellationToken cancellationToken);

    Task<IReadOnlyList<JdChatMessage>> QueryChatMessagesAsync(string cid, CancellationToken cancellationToken);
}
