using Microsoft.Extensions.Options;

namespace QuickReply.CloudService;

internal static class KnowledgeCloudServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledgeCloudServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<KnowledgeCloudOptions>(configuration.GetSection(KnowledgeCloudOptions.SectionName));
        services.AddSingleton<KnowledgeCloudStore>();
        services.AddSingleton<KnowledgeCandidateImportService>();
        services.AddSingleton<ICodexCliCommandRunner, CodexCliCommandRunner>();
        services.AddSingleton<CodexCliAuthService>();
        services.AddKnowledgeSynthesisServices();
        services.AddSingleton<CloudServiceRuntimeInfoService>();
        return services;
    }

    private static IServiceCollection AddKnowledgeSynthesisServices(this IServiceCollection services)
    {
        services.AddConfiguredHttpClient(
            nameof(OllamaKnowledgeCandidateSynthesizer),
            options => options.OllamaBaseUrl);
        services.AddConfiguredHttpClient(
            nameof(OpenAiKnowledgeCandidateSynthesizer),
            options => options.OpenAiBaseUrl);

        services.AddSingleton<HeuristicKnowledgeCandidateSynthesizer>();
        services.AddSingleton<CodexCliKnowledgeCandidateSynthesizer>();
        services.AddSingleton<OllamaKnowledgeCandidateSynthesizer>();
        services.AddSingleton<OpenAiKnowledgeCandidateSynthesizer>();
        services.AddSingleton<IKnowledgeCandidateSynthesizer, HybridKnowledgeCandidateSynthesizer>();
        services.AddSingleton<KnowledgeCandidateSynthesisService>();
        return services;
    }

    private static IServiceCollection AddConfiguredHttpClient(
        this IServiceCollection services,
        string clientName,
        Func<KnowledgeCloudOptions, string> baseAddressSelector)
    {
        services.AddHttpClient(clientName, (serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<KnowledgeCloudOptions>>().Value;
            client.BaseAddress = new Uri(baseAddressSelector(options));
        });

        return services;
    }
}
