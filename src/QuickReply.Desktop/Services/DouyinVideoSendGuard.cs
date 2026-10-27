using System.IO;

namespace QuickReply.Desktop.Services;

/// <summary>
/// Prevents the same Douyin video from being submitted twice in a short window
/// (a duplicate video appeared once in the 2026-10-23 speed test) and serializes
/// toolbar uploads so two hotkey presses never drive the file dialog at once.
/// </summary>
internal sealed class DouyinVideoSendGuard
{
    internal static readonly TimeSpan DefaultDuplicateWindow = TimeSpan.FromSeconds(30);

    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _recentSubmissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _duplicateWindow;
    private bool _uploadInProgress;

    public DouyinVideoSendGuard(Func<DateTimeOffset>? clock = null, TimeSpan? duplicateWindow = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        _duplicateWindow = duplicateWindow ?? DefaultDuplicateWindow;
    }

    public GuardDecision TryBegin(IntPtr targetWindowHandle, string videoPath)
    {
        var key = BuildKey(targetWindowHandle, videoPath);
        lock (_sync)
        {
            if (_uploadInProgress)
            {
                return GuardDecision.Busy;
            }

            var now = _clock();
            if (_recentSubmissions.TryGetValue(key, out var submittedAt) && now - submittedAt < _duplicateWindow)
            {
                return GuardDecision.Duplicate;
            }

            _uploadInProgress = true;
            return GuardDecision.Allowed;
        }
    }

    public void Complete(IntPtr targetWindowHandle, string videoPath, bool submitted)
    {
        lock (_sync)
        {
            _uploadInProgress = false;
            if (!submitted)
            {
                return;
            }

            var now = _clock();
            _recentSubmissions[BuildKey(targetWindowHandle, videoPath)] = now;
            foreach (var stale in _recentSubmissions.Where(pair => now - pair.Value >= _duplicateWindow).Select(pair => pair.Key).ToArray())
            {
                _recentSubmissions.Remove(stale);
            }
        }
    }

    private static string BuildKey(IntPtr targetWindowHandle, string videoPath)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(videoPath);
        }
        catch
        {
            normalized = videoPath;
        }

        return $"{targetWindowHandle.ToInt64():X}|{normalized}";
    }

    internal enum GuardDecision
    {
        Allowed,
        Busy,
        Duplicate
    }
}
