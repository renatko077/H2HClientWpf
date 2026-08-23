namespace H2HClientWeb.Models;

public sealed class HistoryRecord
{
    public long Id { get; set; }
    public int MerchantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Type { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string MerchantOrderId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Status { get; set; } = "";
}
