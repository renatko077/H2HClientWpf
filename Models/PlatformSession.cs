namespace H2HClientWeb.Models;

public sealed class PlatformSession
{
    public string Token { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Login { get; set; } = "";
    public string Role { get; set; } = "";
    public Guid UserId { get; set; }
    public decimal? Balance { get; set; }
}
