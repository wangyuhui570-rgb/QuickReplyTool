using System.Text.Json.Serialization;

namespace QuickReply.Desktop.Models;

public sealed class PublishedKnowledgeCard
{
    public Guid Id { get; init; }

    public string CandidateKind { get; init; } = "qa";

    public string Platform { get; init; } = string.Empty;

    public string ProductKey { get; init; } = string.Empty;

    public string Question { get; init; } = string.Empty;

    public string Answer { get; init; } = string.Empty;

    public IReadOnlyList<string> Synonyms { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string UsageNotes { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }

    [JsonIgnore]
    public bool HasProductKey => !string.IsNullOrWhiteSpace(ProductKey);

    [JsonIgnore]
    public string SummaryText => string.IsNullOrWhiteSpace(ProductKey)
        ? Question
        : $"{Question} | {ProductKey}";
}
