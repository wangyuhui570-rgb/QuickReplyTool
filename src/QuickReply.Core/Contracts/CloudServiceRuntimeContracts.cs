namespace QuickReply.Core.Contracts;

public sealed class CloudServiceRuntimeInfoResponse
{
    public bool Ok { get; init; }
    public string Service { get; init; } = "QuickReply.CloudService";
    public string Version { get; init; } = string.Empty;
    public bool DatabaseReady { get; init; }
    public string DatabasePath { get; init; } = string.Empty;
    public bool ManagementApiTokenConfigured { get; init; }
    public bool LoopbackBypassEnabled { get; init; }
    public string[] Warnings { get; init; } = [];
}
