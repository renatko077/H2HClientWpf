namespace H2HClientWeb.Models;

public sealed class ApiResult
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string RequestJson { get; set; } = "";
    public string ResponseJson { get; set; } = "";
    public string? SessionId { get; set; }
    public string? WalletAddress { get; set; }
    public string? RedirectUrl { get; set; }
    public string? Error { get; set; }
    public double DurationMs { get; set; }
}
