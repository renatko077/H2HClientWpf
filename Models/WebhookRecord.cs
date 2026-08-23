namespace H2HClientWeb.Models;

public sealed class WebhookRecord
{
    public long Id { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = "";
    public string Body { get; set; } = "";
    public bool? SignatureValid { get; set; }
}
