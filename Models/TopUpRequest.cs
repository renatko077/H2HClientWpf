namespace H2HClientWeb.Models;

public sealed class TopUpRequest
{
    public string Id { get; set; } = "";
    public int MerchantId { get; set; }
    public bool IsProd { get; set; }
    public string PlatformLogin { get; set; } = "";
    public Guid PlatformUserId { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Ожидает";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
