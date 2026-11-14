using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public interface IJdWorkbenchProfileService
{
    IReadOnlyList<JdWorkbenchProfile> FindProfiles();

    JdWorkbenchProfile ResolveProfile(string? configuredProfilePath = null);
}
