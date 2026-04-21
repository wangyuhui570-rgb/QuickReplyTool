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
        services.AddSingleton<CloudServiceRuntimeInfoService>();
        return services;
    }
}
