namespace H2HClientWeb.Models;

public sealed class DashboardViewModel
{
    public required Merchant Merchant { get; init; }
    public bool IsProd { get; init; }
    public string WebhookUrl { get; init; } = "";
    public ApiResult? Result { get; init; }
    public IReadOnlyList<HistoryRecord> History { get; init; } = [];
    public IReadOnlyList<WebhookRecord> Webhooks { get; init; } = [];
    public PlatformSession? Platform { get; init; }
    public IReadOnlyList<TopUpRequest> TopUpRequests { get; init; } = [];
}
