namespace QuickReply.Desktop.Services;

/// <summary>
/// Kept for settings compatibility. Cloud endpoints are retired; default is empty (local-only).
/// </summary>
public static class QuickReplyServiceEndpointDefaults
{
    public const string DefaultRemoteServiceBaseUrl = "";

    public static string NormalizeConfiguredServiceBaseUrl(string? value)
    {
        return (value ?? string.Empty).Trim().TrimEnd('/');
    }
}
