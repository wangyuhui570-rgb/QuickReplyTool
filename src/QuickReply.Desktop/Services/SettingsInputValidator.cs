using System.IO;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public static class SettingsInputValidator
{
    public static IReadOnlyList<string> ValidateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();

        AddIfNotNull(errors, ValidateOptionalDirectoryPath(settings.FileLibraryFolderPath, "资料文件夹", requireExistingDirectory: true));
        AddIfNotNull(errors, ValidateOptionalDirectoryPath(settings.PhraseImageFolderPath, "短语图片目录"));
        AddIfNotNull(errors, ValidateOptionalDirectoryPath(settings.ScreenshotToolSaveDirectory, "截图默认保存目录"));
        AddIfNotNull(errors, ValidateOptionalDirectoryPath(settings.IssueReportDirectory, "问题反馈目录"));


        return errors;
    }

    public static string? ValidateFirstRunFileLibraryFolder(string? folderPath)
    {
        return ValidateOptionalDirectoryPath(folderPath, "资料文件夹", requireExistingDirectory: true);
    }

    public static bool CanRunCloudSyncActions(string? serverBaseUrl) => false;


    public static bool CanRunUpdateActions(string? serverBaseUrl) => false;


    public static string GetCloudActionAvailabilitySummary(string? serverBaseUrl)
    {
        return "当前版本为本地工具：云同步、在线更新和知识审核已移除。";
    }


    public static string ResolveConfiguredServerBaseUrl(string? serverBaseUrl)
    {
        return QuickReplyServiceEndpointDefaults.NormalizeConfiguredServiceBaseUrl(serverBaseUrl);
    }

    public static string ResolveOperationalServerBaseUrl(string? serverBaseUrl)
    {
        var configuredServerBaseUrl = ResolveConfiguredServerBaseUrl(serverBaseUrl);
        return TryBuildRemoteServiceBaseUri(configuredServerBaseUrl, out _)
            ? configuredServerBaseUrl
            : QuickReplyServiceEndpointDefaults.DefaultRemoteServiceBaseUrl;
    }

    public static bool TryBuildConfiguredServerBaseUri(string? serverBaseUrl, out Uri baseUri)
    {
        return TryBuildRemoteServiceBaseUri(ResolveOperationalServerBaseUrl(serverBaseUrl), out baseUri);
    }

    public static bool TryBuildRemoteServiceBaseUri(string? serverBaseUrl, out Uri baseUri)
    {
        if (!TryBuildHttpBaseUri(serverBaseUrl, out baseUri))
        {
            return false;
        }

        if (string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsLoopbackHost(baseUri.Host);
    }

    public static bool TryBuildHttpBaseUri(string? serverBaseUrl, out Uri baseUri)
    {
        var normalized = (serverBaseUrl ?? string.Empty).Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var parsed) &&
            (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            baseUri = parsed;
            return true;
        }

        baseUri = null!;
        return false;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
    }

    private static string? ValidateOptionalDirectoryPath(string? path, string displayName, bool requireExistingDirectory = false)
    {
        var normalized = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (!Path.IsPathRooted(normalized))
        {
            return $"{displayName}需要填写完整的本地文件夹路径。";
        }

        if (normalized.IndexOfAny(['*', '?']) >= 0)
        {
            return $"{displayName}不是有效的 Windows 文件夹路径。";
        }

        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
            return $"{displayName}不是有效的 Windows 文件夹路径。";
        }

        if (requireExistingDirectory && !Directory.Exists(normalized))
        {
            return $"{displayName}当前不存在，请先选择一个已经存在的文件夹。";
        }

        return null;
    }

    private static void AddIfNotNull(ICollection<string> target, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            target.Add(error);
        }
    }
}
