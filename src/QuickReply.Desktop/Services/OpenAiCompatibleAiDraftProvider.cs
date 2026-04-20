using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

internal sealed class OpenAiCompatibleAiDraftProvider : IAiDraftProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string ProviderType => "openai_compatible";

    public async Task<AiDraftProviderResponse> GenerateAsync(
        AppSettings settings,
        AiDraftProviderRequest request,
        CancellationToken cancellationToken)
    {
        if (!SettingsInputValidator.TryBuildRemoteServiceBaseUri(settings.AiApiBaseUrl, out var baseUri))
        {
            return AiDraftProviderResponse.Failed(
                ProviderType,
                settings.AiApiModelName,
                "AI API 地址无效，已跳过远端草稿生成。",
                "api_base_url_invalid");
        }

        if (string.IsNullOrWhiteSpace(settings.AiApiModelName))
        {
            return AiDraftProviderResponse.Failed(
                ProviderType,
                string.Empty,
                "还没有填写远端 AI 模型名，已跳过远端草稿生成。",
                "api_model_missing");
        }

        if (string.IsNullOrWhiteSpace(settings.AiApiKey))
        {
            return AiDraftProviderResponse.Failed(
                ProviderType,
                settings.AiApiModelName,
                "还没有填写远端 AI 密钥，已跳过远端草稿生成。",
                "api_key_missing");
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
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiApiKey.Trim());

            using var response = await httpClient.PostAsync(
                "chat/completions",
                BuildRequestContent(settings, request),
                timeoutCts.Token).ConfigureAwait(false);

            var responseContent = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            if (!response.IsSuccessStatusCode)
            {
                return AiDraftProviderResponse.Failed(
                    ProviderType,
                    settings.AiApiModelName,
                    $"远端 AI 请求失败：{response.StatusCode}",
                    "api_http_failed",
                    stopwatch.ElapsedMilliseconds,
                    responseContent.Length);
            }

            var modelResponse = ExtractMessageContent(responseContent);
            var drafts = AiDraftPromptComposer.ParseDrafts(
                modelResponse,
                ProviderType,
                settings.AiApiModelName,
                request.DraftCount,
                request.GroundingSuggestions,
                request.GroundingKnowledgeCards,
                stopwatch.ElapsedMilliseconds,
                "ollama_failed");
            if (drafts.Count == 0)
            {
                return AiDraftProviderResponse.Failed(
                    ProviderType,
                    settings.AiApiModelName,
                    "远端 AI 没有返回可用草稿。",
                    "api_empty_response",
                    stopwatch.ElapsedMilliseconds,
                    modelResponse.Length);
            }

            return AiDraftProviderResponse.Succeeded(
                ProviderType,
                settings.AiApiModelName,
                $"远端 AI 已生成 {drafts.Count} 条草稿。",
                drafts,
                stopwatch.ElapsedMilliseconds,
                modelResponse.Length);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return AiDraftProviderResponse.Failed(
                ProviderType,
                settings.AiApiModelName,
                "远端 AI 响应超时，已跳过草稿生成。",
                "api_timeout",
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return AiDraftProviderResponse.Failed(
                ProviderType,
                settings.AiApiModelName,
                $"远端 AI 不可用：{ex.Message}",
                "api_unavailable",
                stopwatch.ElapsedMilliseconds);
        }
    }

    private static StringContent BuildRequestContent(AppSettings settings, AiDraftProviderRequest request)
    {
        var payload = new
        {
            model = settings.AiApiModelName.Trim(),
            temperature = 0.35,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = AiDraftPromptComposer.BuildSystemPrompt()
                },
                new
                {
                    role = "user",
                    content = AiDraftPromptComposer.BuildUserPrompt(request)
                }
            }
        };

        return new StringContent(
            JsonSerializer.Serialize(payload, SerializerOptions),
            Encoding.UTF8,
            "application/json");
    }

    private static string ExtractMessageContent(string responseContent)
    {
        try
        {
            using var document = JsonDocument.Parse(responseContent);
            if (document.RootElement.TryGetProperty("choices", out var choicesElement) &&
                choicesElement.ValueKind == JsonValueKind.Array &&
                choicesElement.GetArrayLength() > 0)
            {
                var firstChoice = choicesElement[0];
                if (firstChoice.TryGetProperty("message", out var messageElement) &&
                    messageElement.ValueKind == JsonValueKind.Object &&
                    messageElement.TryGetProperty("content", out var contentElement) &&
                    contentElement.ValueKind == JsonValueKind.String)
                {
                    return contentElement.GetString() ?? string.Empty;
                }
            }
        }
        catch
        {
        }

        return responseContent;
    }
}
