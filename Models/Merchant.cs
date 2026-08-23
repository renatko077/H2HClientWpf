using System.ComponentModel.DataAnnotations;

namespace H2HClientWeb.Models;

public sealed class Merchant
{
    public int Id { get; set; }
    public bool IsProd { get; set; }
    [Required, StringLength(100), Display(Name = "Название")] public string Name { get; set; } = "";
    [Display(Name = "Test API key")] public string TestApiKey { get; set; } = "";
    [Display(Name = "API key (создаёт заявки)")] public string LiveApiKey { get; set; } = "";
    [Display(Name = "Secret Key")] public string Secret { get; set; } = "";
    [Required, Url, Display(Name = "API Base URL")] public string BaseUrl { get; set; } = "https://localhost:7056";

    public string TestApiKeyMasked => MaskKey(TestApiKey);
    public string LiveApiKeyMasked => MaskKey(LiveApiKey);

    private static string MaskKey(string value) =>
        string.IsNullOrWhiteSpace(value) ? "не задан" : value.Length <= 10 ? "••••••" : value[..10] + "••••••";
}
