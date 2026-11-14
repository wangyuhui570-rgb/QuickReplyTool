using System.IO;
using System.Text;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class JdWorkbenchProfileService : IJdWorkbenchProfileService
{
    private readonly string _profileRootPath;

    public JdWorkbenchProfileService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JDM",
            "JMWorkStation"))
    {
    }

    internal JdWorkbenchProfileService(string profileRootPath)
    {
        _profileRootPath = profileRootPath;
    }

    public IReadOnlyList<JdWorkbenchProfile> FindProfiles()
    {
        if (string.IsNullOrWhiteSpace(_profileRootPath) || !Directory.Exists(_profileRootPath))
        {
            return [];
        }

        return Directory
            .EnumerateDirectories(_profileRootPath)
            .Select(TryBuildProfile)
            .Where(profile => profile is not null)
            .Select(profile => profile!)
            .OrderByDescending(profile => profile.LastModifiedAt)
            .ToArray();
    }

    public JdWorkbenchProfile ResolveProfile(string? configuredProfilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredProfilePath))
        {
            var profile = TryBuildProfile(configuredProfilePath.Trim());
            if (profile is null)
            {
                throw new FileNotFoundException($"京东工作台 Profile 不完整或不存在：{configuredProfilePath}");
            }

            return profile;
        }

        return FindProfiles().FirstOrDefault()
            ?? throw new FileNotFoundException($"未在 {_profileRootPath} 下找到京东工作台登录档案。");
    }

    private static JdWorkbenchProfile? TryBuildProfile(string profilePath)
    {
        var cookiesPath = Path.Combine(profilePath, ".cefcache", "Network", "Cookies");
        var prefsPath = Path.Combine(profilePath, ".cefcache", "LocalPrefs.json");
        if (!File.Exists(cookiesPath) || !File.Exists(prefsPath))
        {
            return null;
        }

        var displayName = DecodeProfileName(Path.GetFileName(profilePath));
        var lastModified = new[] { File.GetLastWriteTimeUtc(cookiesPath), File.GetLastWriteTimeUtc(prefsPath) }.Max();
        return new JdWorkbenchProfile
        {
            DisplayName = displayName,
            ProfilePath = profilePath,
            LastModifiedAt = new DateTimeOffset(lastModified, TimeSpan.Zero).ToLocalTime()
        };
    }

    private static string DecodeProfileName(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            var decoded = Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(decoded) ? value : decoded;
        }
        catch
        {
            return value;
        }
    }
}
