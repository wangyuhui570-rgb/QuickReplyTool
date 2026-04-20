using System.Diagnostics;

namespace QuickReply.Desktop.Services;

internal sealed class OllamaModelCatalogService
{
    public async Task<(bool Success, IReadOnlyList<string> Models, string Message)> ListModelsAsync(
        string ollamaBaseUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ollama",
                    Arguments = "list",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!string.IsNullOrWhiteSpace(ollamaBaseUrl))
            {
                process.StartInfo.Environment["OLLAMA_HOST"] = ollamaBaseUrl.Trim();
            }

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return (false, [], string.IsNullOrWhiteSpace(error) ? "执行 ollama list 失败。" : error.Trim());
            }

            var models = ParseModelList(output);
            return models.Count == 0
                ? (true, models, "当前 Ollama 还没有可用模型，请先执行 ollama pull 拉取模型。")
                : (true, models, $"已读取到 {models.Count} 个 Ollama 模型。");
        }
        catch (Exception ex)
        {
            return (false, [], $"读取 Ollama 模型列表失败：{ex.Message}");
        }
    }

    internal static IReadOnlyList<string> ParseModelList(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var models = new List<string>();
        foreach (var line in output
                     .Replace("\r", string.Empty, StringComparison.Ordinal)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var firstToken = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstToken))
            {
                models.Add(firstToken.Trim());
            }
        }

        return models
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
