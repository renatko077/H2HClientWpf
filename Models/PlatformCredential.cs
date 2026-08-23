namespace H2HClientWeb.Models;

public sealed class PlatformCredential
{
    public long Id { get; set; }
    public bool IsProd { get; set; }
    public string BaseUrl { get; set; } = "";
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
}
