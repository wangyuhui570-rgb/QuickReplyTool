using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal sealed class OllamaAiDraftProvider : IAiDraftProvider
{
    internal const string DefaultBaseUrl = "http://127.0.0.1:11434";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string ProviderType => "ollama";

    public async Task<AiDraftProviderResponse> GenerateAsync(
        AppSettings settings,
        AiDraftProviderRequest request,
        CancellationToken cancellationToken)
    {
        if (!SettingsInputValidator.TryBuildHttpBaseUri(settings.OllamaBaseUrl, out var baseUri))
        {
            return AiDraftProviderResponse.Failed(
                ProviderType,
                settings.OllamaModelName,
                "Ollama 地址无效，已跳过 AI 草稿。",
                "ollama_base_url_invalid");
        }

        if (string.IsNullOrWhiteSpace(settings.OllamaModelName))
        {
            return AiDraftProviderResponse.Failed(
                ProviderType,
                string.Empty,
                "还没有选择本地 Ollama 模型，已跳过 AI 草稿。",
                "ollama_model_missing");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Math.Clamp(settings.AiRequestTimeoutMs, 1000, 60000));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = baseUri
            };
            using var response = await httpClient.PostAsync(
                "api/generate",
                BuildRequestContent(settings, request),
                timeoutCts.Token).ConfigureAwait(false);

            var responseContent = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            if (!response.IsSuccessStatusCode)
            {
                return AiDraftProviderResponse.Failed(
                    ProviderType,
                    settings.OllamaModelName,
                    $"本地 Ollama 请求失败：{response.StatusCode}",
                    "ollama_http_failed",
                    stopwatch.ElapsedMilliseconds,
                    responseContent.Length);
            }

            var modelResponse = ExtractOllamaText(responseContent);
            var drafts = AiDraftPromptComposer.ParseDrafts(
                modelResponse,
                ProviderType,
                settings.OllamaModelName,
                request.DraftCount,
                request.GroundingSuggestions,
                request.GroundingKnowledgeCards,
                stopwatch.ElapsedMilliseconds,
                string.Empty);
            if (drafts.Count == 0)
            {
                return AiDraftProviderResponse.Failed(
                    ProviderType,
                    settings.OllamaModelName,
                    "本地 Ollama 没有返回可用草稿。",
                    "ollama_empty_response",
                    stopwatch.ElapsedMilliseconds,
                    modelResponse.Length);
            }

            return AiDraftProviderResponse.Succeeded(
                ProviderType,
                settings.OllamaModelName,
                $"本地 Ollama 已生成 {drafts.Count} 条草稿。",
                drafts,
                stopwatch.ElapsedMilliseconds,
                modelResponse.Length);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return AiDraftProviderResponse.Failed(
                ProviderType,
                settings.OllamaModelName,
                "本地 Ollama 响应超时，已跳过 AI 草稿。",
                "ollama_timeout",
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return AiDraftProviderResponse.Failed(
                ProviderType,
                settings.OllamaModelName,
                $"本地 Ollama 不可用：{ex.Message}",
                "ollama_unavailable",
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static StringContent BuildRequestContent(AppSettings settings, AiDraftProviderRequest request)
    {
        var payload = new
        {
            model = settings.OllamaModelName.Trim(),
            system = AiDraftPromptComposer.BuildSystemPrompt(),
            prompt = AiDraftPromptComposer.BuildUserPrompt(request),
            stream = false,
            format = "json",
            options = new
            {
                temperature = 0.35,
                top_p = 0.9
            }
        };

        return new StringContent(
            JsonSerializer.Serialize(payload, SerializerOptions),
            Encoding.UTF8,
            "application/json");
    }

    private static string ExtractOllamaText(string responseContent)
    {
        try
        {
            using var document = JsonDocument.Parse(responseContent);
            if (document.RootElement.TryGetProperty("response", out var responseElement) &&
                responseElement.ValueKind == JsonValueKind.String)
            {
                return responseElement.GetString() ?? string.Empty;
            }
        }
        catch
        {
        }

        return responseContent;
    }
}
