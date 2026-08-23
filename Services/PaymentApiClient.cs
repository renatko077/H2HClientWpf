using System.Diagnostics;
using System.Text;
using System.Text.Json;
using H2HClientWeb.Models;

namespace H2HClientWeb.Services;

public sealed class PaymentApiClient
{
    public const int MaxDisputeFiles = 5;
    public const long MaxDisputeFileBytes = 10 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PaymentApiClient(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(90);
        _configuration = configuration;
    }

    public Task<ApiResult> CreateSessionAsync(
        Merchant merchant, bool isProd, string merchantOrderId, decimal amount, string currency,
        decimal exchangeRate, string? callbackUrl, int? testScenario)
    {
        // TestScenario is useful only when the payment API identifies the request by TestApiKey.
        // This client uses LiveApiKey below, so DEV and PROD both create real orders.
        int? effectiveTestScenario = isProd ? null : testScenario ?? 0;
        var body = new
        {
            merchantOrderId,
            amount,
            currency,
            callbackUrl,
            exchangeRate,
            testScenario = effectiveTestScenario
        };
        return SendMerchantAsync(merchant, isProd, HttpMethod.Post, "/api/payment-sessions/h2h", body, true, true);
    }

    public Task<ApiResult> CreateDebitSessionAsync(
        Merchant merchant, bool isProd, string merchantOrderId, decimal amount, string currency,
        string cardNumber, string? callbackUrl, int? testScenario)
    {
        var body = new { merchantOrderId, amount, currency, cardNumber, callbackUrl, testScenario };
        return SendMerchantAsync(merchant, isProd, HttpMethod.Post, "/api/debit-payment-sessions/h2h", body, true, true);
    }

    public Task<ApiResult> CancelSessionAsync(Merchant merchant, bool isProd, Guid sessionId)
    {
        var body = new { sessionId, merchantOrderId = (string?)null };
        return SendMerchantAsync(merchant, isProd, HttpMethod.Patch, "/api/payment-sessions/cancel", body);
    }

    public Task<ApiResult> OpenDisputeAsync(
        Merchant merchant, bool isProd, Guid sessionId, IReadOnlyCollection<DisputeAttachment> files)
    {
        if (files.Count is < 1 or > MaxDisputeFiles)
        {
            return Task.FromResult(Failed($"Для диспута нужно приложить от 1 до {MaxDisputeFiles} фотографий."));
        }

        var body = new
        {
            sessionId,
            merchantOrderId = (string?)null,
            files = files.Select(file => new
            {
                fileName = file.FileName,
                contentType = file.ContentType,
                base64 = file.Base64
            }).ToArray()
        };
        return SendMerchantAsync(merchant, isProd, HttpMethod.Patch, "/api/payment-sessions/dispute", body);
    }

    public async Task<PlatformLoginResult> LoginAsync(string platformBaseUrl, string login, string password)
    {
        var bodyJson = JsonSerializer.Serialize(new { login, password }, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{NormalizePlatformUrl(platformBaseUrl)}/api/Login")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };

        try
        {
            using var response = await _http.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                var environment = platformBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ? "DEV" : "PROD";
                var error = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.NotFound => $"Неверный логин или пароль для {environment}.",
                    System.Net.HttpStatusCode.Forbidden => $"Аккаунт {environment} отключён.",
                    _ => $"Ошибка входа {(int)response.StatusCode}."
                };
                return new(false, error, null, null, null, null);
            }

            string? token = null;
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                token = cookies.SelectMany(cookie => cookie.Split(';'))
                    .Select(part => part.Trim())
                    .FirstOrDefault(part => part.StartsWith("jwt=", StringComparison.OrdinalIgnoreCase))?[4..];
            }

            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var userId = root.TryGetProperty("userId", out var id) && id.TryGetGuid(out var guid) ? guid : Guid.Empty;
            var userLogin = root.TryGetProperty("login", out var loginElement) ? loginElement.GetString() : login;
            var role = root.TryGetProperty("role", out var roleElement) ? roleElement.GetString() : "";
            var requiresTwoFactor = root.TryGetProperty("requiresTwoFactor", out var twoFactorElement) && twoFactorElement.GetBoolean();
            if (requiresTwoFactor)
                return new(false, "Для аккаунта включена двухфакторная авторизация. Сначала войдите на traderstop.club и подтвердите код.", null, userLogin, role, userId);
            if (string.IsNullOrWhiteSpace(token))
                return new(false, "Платформа не вернула сессию входа.", null, userLogin, role, userId);
            return new(true, null, token, userLogin, role, userId);
        }
        catch (Exception exception)
        {
            return new(false, exception.Message, null, null, null, null);
        }
    }

    public async Task LogoutAsync(string platformBaseUrl, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{NormalizePlatformUrl(platformBaseUrl)}/api/Login/logout");
        request.Headers.Add("Cookie", $"jwt={token}");
        try { await _http.SendAsync(request); } catch { }
    }

    public async Task<decimal?> GetBalanceAsync(string platformBaseUrl, string token, string role)
    {
        var endpoint = role.ToLowerInvariant() switch
        {
            "admin" => "/api/admin/balance",
            "merchant" => "/api/merchant/balance",
            _ => "/api/operator/balance"
        };

        using var request = CreatePlatformRequest(HttpMethod.Get, platformBaseUrl, endpoint, token);
        try
        {
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.TryGetProperty("amount", out var amount) ? amount.GetDecimal() : null;
        }
        catch { return null; }
    }

    public Task<ApiResult> CreateWithdrawalRequestAsync(
        string platformBaseUrl, string token, decimal amount, string walletAddress) =>
        SendPlatformAsync(platformBaseUrl, token, HttpMethod.Post, "/api/withdrawal-requests",
            new { amount, tronWalletAddress = walletAddress });

    public async Task<ApiResult> GetDepositWalletAddressAsync(
        string platformBaseUrl, string token, Guid operatorId)
    {
        var path = "/api/operator/tron-deposits/wallet-address?operatorId=" + Uri.EscapeDataString(operatorId.ToString());
        var result = await SendPlatformAsync(platformBaseUrl, token, HttpMethod.Get, path, null);
        if (!result.Success) return result;
        try
        {
            result.WalletAddress = JsonSerializer.Deserialize<string>(result.ResponseJson);
            if (string.IsNullOrWhiteSpace(result.WalletAddress))
            {
                result.Success = false;
                result.Error = "API не вернул TRON-адрес оператора.";
            }
        }
        catch (Exception exception)
        {
            result.Success = false;
            result.Error = exception.Message;
        }
        return result;
    }

    private async Task<ApiResult> SendMerchantAsync(
        Merchant merchant, bool isProd, HttpMethod method, string path, object body,
        bool includeIdempotency = false, bool parseSessionId = false)
    {
        // Orders must be created in both environments. KalachPay intentionally does not
        // create an Order when X-Api-Key is the merchant TestApiKey.
        var apiKey = merchant.LiveApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            return Failed("У мерчанта не задан API key для создания заявок.");
        if (string.IsNullOrWhiteSpace(merchant.Secret)) return Failed("У мерчанта не задан Secret Key.");
        var signingSecret = merchant.Secret.Trim();

        var baseUrl = isProd
            ? _configuration["Payment:ProductionBaseUrl"] ?? merchant.BaseUrl
            : merchant.BaseUrl;
        var bodyJson = JsonSerializer.Serialize(body, JsonOptions);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = HmacSigner.ComputeHmacSha256Hex(bodyJson, signingSecret);

        using var request = new HttpRequestMessage(method, CombineApiUrl(baseUrl, path))
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-ApexPay-Signature", signature);
        if (includeIdempotency) request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        return await SendAndReadAsync(request, bodyJson, parseSessionId);
    }

    private async Task<ApiResult> SendPlatformAsync(
        string platformBaseUrl, string token, HttpMethod method, string path, object? body)
    {
        var bodyJson = body is null ? "" : JsonSerializer.Serialize(body, JsonOptions);
        using var request = CreatePlatformRequest(method, platformBaseUrl, path, token);
        if (body is not null) request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        return await SendAndReadAsync(request, bodyJson, false);
    }

    private static HttpRequestMessage CreatePlatformRequest(
        HttpMethod method, string platformBaseUrl, string path, string token)
    {
        var request = new HttpRequestMessage(method, $"{NormalizePlatformUrl(platformBaseUrl)}{path}");
        request.Headers.Add("Cookie", $"jwt={token}");
        return request;
    }

    private async Task<ApiResult> SendAndReadAsync(HttpRequestMessage request, string requestJson, bool parseSessionId)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new ApiResult { RequestJson = requestJson };
        try
        {
            using var response = await _http.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            result.StatusCode = (int)response.StatusCode;
            result.Success = response.IsSuccessStatusCode;
            result.ResponseJson = string.IsNullOrWhiteSpace(responseBody) ? "(пусто)" : responseBody;

            if (parseSessionId && result.Success && !string.IsNullOrWhiteSpace(responseBody))
            {
                using var document = JsonDocument.Parse(responseBody);
                if (document.RootElement.TryGetProperty("id", out var id)) result.SessionId = id.ToString();
            }
        }
        catch (Exception exception)
        {
            result.Success = false;
            result.Error = $"Ошибка запроса: {exception.Message}";
        }
        finally
        {
            stopwatch.Stop();
            result.DurationMs = stopwatch.Elapsed.TotalMilliseconds;
        }
        return result;
    }

    private static ApiResult Failed(string error) => new() { Success = false, Error = error };

    private static string NormalizePlatformUrl(string value)
    {
        var url = value.TrimEnd('/');
        return url.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? url[..^4] : url;
    }

    private static string CombineApiUrl(string baseUrl, string path)
    {
        var root = baseUrl.TrimEnd('/');
        if (root.EndsWith("/api", StringComparison.OrdinalIgnoreCase) &&
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            path = path[4..];
        return root + (path.StartsWith('/') ? path : "/" + path);
    }
}

public sealed record PlatformLoginResult(
    bool Success, string? Error, string? Token, string? Login, string? Role, Guid? UserId);
