using System.ComponentModel.DataAnnotations;

namespace H2HClientWeb.Models;

public abstract class MerchantOperationInput
{
    [Required] public int MerchantId { get; set; }
    public bool IsProd { get; set; }
}

public sealed class H2hPaymentInput : MerchantOperationInput
{
    [Required] public string MerchantOrderId { get; set; } = "";
    [Range(0.01, double.MaxValue)] public decimal Amount { get; set; }
    [Required] public string Currency { get; set; } = "UAH";
    [Range(0.000001, double.MaxValue)] public decimal ExchangeRate { get; set; } = 1;
    public bool UseWebhook { get; set; } = true;
    public string? TestScenario { get; set; }
}

public sealed class DebitPaymentInput : MerchantOperationInput
{
    [Required] public string MerchantOrderId { get; set; } = "";
    [Range(0.01, double.MaxValue)] public decimal Amount { get; set; }
    [Required] public string Currency { get; set; } = "UAH";
    [Required] public string CardNumber { get; set; } = "";
    public bool UseWebhook { get; set; } = true;
    public string? TestScenario { get; set; }
}

public sealed class SessionActionInput : MerchantOperationInput
{
    [Required] public Guid SessionId { get; set; }
}

public sealed class DisputeInput : MerchantOperationInput
{
    [Required] public Guid SessionId { get; set; }
    public List<IFormFile> Files { get; set; } = [];
}

public sealed class PlatformLoginInput : MerchantOperationInput
{
    [Required, Url] public string PlatformBaseUrl { get; set; } = "";
    [Required] public string Login { get; set; } = "";
    [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
}

public sealed class HeaderPlatformLoginInput
{
    public bool IsProd { get; set; }
    [Required] public string Login { get; set; } = "";
    [DataType(DataType.Password)] public string Password { get; set; } = "";
    public bool Remember { get; set; } = true;
}

public sealed class WithdrawalInput : MerchantOperationInput
{
    [Range(0.01, double.MaxValue)] public decimal Amount { get; set; }
    [Required] public string WalletAddress { get; set; } = "";
}

public sealed class DepositInput : MerchantOperationInput
{
    [Range(0.01, double.MaxValue)] public decimal Amount { get; set; }
    [Required, DataType(DataType.Password)] public string PrivateKey { get; set; } = "";
}

public sealed class TopUpRequestInput : MerchantOperationInput
{
    [Range(0.01, double.MaxValue)] public decimal Amount { get; set; }
}
