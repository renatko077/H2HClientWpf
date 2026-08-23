using System.Security.Cryptography;
using System.Text;
using H2HClientWeb.Data;
using H2HClientWeb.Models;
using H2HClientWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace H2HClientWeb.Controllers;

[AllowAnonymous]
[ApiController]
public sealed class WebhookController : ControllerBase
{
    private readonly AppRepository _repository;
    private readonly IConfiguration _configuration;

    public WebhookController(AppRepository repository, IConfiguration configuration)
    {
        _repository = repository;
        _configuration = configuration;
    }

    [HttpPost("/api/webhooks/payment")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Receive()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var suppliedSignature = Request.Headers["X-ApexPay-Signature"].FirstOrDefault();
        bool? signatureValid = null;

        if (!string.IsNullOrWhiteSpace(suppliedSignature))
        {
            signatureValid = _repository.GetMerchants().Any(merchant =>
                FixedEquals(HmacSigner.ComputeHmacSha256Hex(body, merchant.Secret), suppliedSignature));
        }

        _repository.AddWebhook(new WebhookRecord
        {
            Method = Request.Method,
            Path = Request.Path,
            Body = body,
            SignatureValid = signatureValid
        });

        if (_configuration.GetValue<bool>("Webhooks:RequireValidSignature") && signatureValid != true)
            return Unauthorized(new { received = false, error = "Invalid signature" });

        return Ok(new { received = true });
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left.ToLowerInvariant()));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right.ToLowerInvariant()));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
