using System.IO;
using System.Text;
using System.Text.Json;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class ExceptionLogService
{
    private readonly object _syncRoot = new();
    private readonly string _rootPath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = false
    };

    public ExceptionLogService()
    {
        _rootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QuickReplyTool",
            "logs",
            "exceptions");

        Directory.CreateDirectory(_rootPath);
    }

    public string RootPath => _rootPath;

    public void RecordException(Exception exception, string source, string? context = null, bool isTerminating = false)
    {
        try
        {
            var entry = new ExceptionLogEntry
            {
                OccurredAt = DateTimeOffset.Now,
                Source = source?.Trim() ?? string.Empty,
                ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                Message = exception.Message ?? string.Empty,
                StackTrace = exception.ToString(),
                Context = context?.Trim() ?? string.Empty,
                IsTerminating = isTerminating
            };

            var filePath = Path.Combine(_rootPath, $"{DateTime.Now:yyyyMMdd}.jsonl");
            var line = JsonSerializer.Serialize(entry, _serializerOptions);

            lock (_syncRoot)
            {
                Directory.CreateDirectory(_rootPath);
                File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    public async Task<IReadOnlyList<ExceptionLogEntry>> GetRecentEntriesAsync(int maxCount = 20)
    {
        if (!Directory.Exists(_rootPath))
        {
            return [];
        }

        var files = new DirectoryInfo(_rootPath)
            .EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.Name)
            .ToList();

        var entries = new List<ExceptionLogEntry>(maxCount);
        foreach (var file in files)
        {
            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file.FullName).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var line = lines[index]?.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<ExceptionLogEntry>(line);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                catch
                {
                }

                if (entries.Count >= maxCount)
                {
                    return entries
                        .OrderByDescending(item => item.OccurredAt)
                        .ToArray();
                }
            }
        }

        return entries
            .OrderByDescending(item => item.OccurredAt)
            .ToArray();
    }

    public Task<int> ClearAsync()
    {
        if (!Directory.Exists(_rootPath))
        {
            return Task.FromResult(0);
        }

        var files = Directory.GetFiles(_rootPath, "*.jsonl", SearchOption.TopDirectoryOnly);
        var removedCount = 0;
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
                removedCount++;
            }
            catch
            {
            }
        }

        return Task.FromResult(removedCount);
    }
}
