namespace QuickReply.Desktop.Services;

public static class QuickReplyServiceEndpointDefaults
{
    public const string DefaultRemoteServiceBaseUrl = "https://gta.wyh570.online";

    public static string NormalizeConfiguredServiceBaseUrl(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized)
            ? DefaultRemoteServiceBaseUrl
            : normalized;
    }
}
