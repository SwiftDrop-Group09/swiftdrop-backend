using System.ComponentModel.DataAnnotations;

namespace SwiftDrop.Delivery.Api.Application.Assistant;

public sealed class AssistantLimits
{
    [Range(1024, 65536)] public int RequestBytes { get; set; } = 32768;
    [Range(1, 4096)] public int MessageCharacters { get; set; } = 1500;
    [Range(0, 30)] public int HistoryEntries { get; set; } = 10;
    [Range(128, 4096)] public int ToolArgumentCharacters { get; set; } = 2048;
    [Range(1, 30)] public int ProviderTimeoutSeconds { get; set; } = 8;
    [Range(1, 4)] public int ProviderToolCalls { get; set; } = 1;
    [Range(1, 10000)] public int AnonymousRequestsPerMinute { get; set; } = 120;
}
