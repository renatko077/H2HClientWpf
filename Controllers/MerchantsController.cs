using System.Text.Json;
using H2HClientWeb.Data;
using H2HClientWeb.Models;
using H2HClientWeb.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;

namespace H2HClientWeb.Controllers;

public sealed class MerchantsController : Controller
{
    private const string ResultKey = "OperationResult";
    private const string EnvironmentKey = "Environment:IsProd";
    private readonly AppRepository _repository;
    private readonly PaymentApiClient _api;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public MerchantsController(
        AppRepository repository, PaymentApiClient api, IConfiguration configuration, IWebHostEnvironment environment)
    {
        _repository = repository;
        _api = api;
        _configuration = configuration;
        _environment = environment;
    }

    [HttpGet]
    public IActionResult Index(bool prod = false, string? platformError = null)
    {
        prod = HostedProduction;
        SetEnvironment(prod);
        ViewBag.PlatformError = platformError;
        return View(_repository.GetMerchants(prod));
    }

    [HttpGet]
    public IActionResult Edit(int? id, bool prod = false)
    {
        prod = HostedProduction;
        if (id is null)
            return View(new Merchant
            {
                IsProd = prod,
                BaseUrl = prod ? ProductionPaymentBaseUrl() : "https://localhost:7056"
            });
        var merchant = _repository.GetMerchant(id.Value);
        if (merchant is null) return NotFound();

        merchant.TestApiKey = "";
        merchant.LiveApiKey = "";
        merchant.Secret = "";
        return View(merchant);
    }

    [HttpPost]
    public IActionResult Edit(Merchant merchant)
    {
        merchant.IsProd = HostedProduction;
        if (merchant.Id == 0)
        {
            if (string.IsNullOrWhiteSpace(merchant.LiveApiKey)) ModelState.AddModelError(nameof(merchant.LiveApiKey), "Укажите API key.");
            if (string.IsNullOrWhiteSpace(merchant.Secret)) ModelState.AddModelError(nameof(merchant.Secret), "Укажите Secret Key.");
        }

        if (!ModelState.IsValid) return View(merchant);
        try
        {
            var id = _repository.SaveMerchant(merchant);
            return RedirectToAction(nameof(Dashboard), new { id, prod = merchant.IsProd });
        }
        catch (SqliteException exception)
        {
            ModelState.AddModelError("", exception.SqliteErrorCode == 19
                ? "Мерчант с таким именем уже существует."
                : exception.Message);
            return View(merchant);
        }
    }

    [HttpPost]
    public IActionResult Delete(int id)
    {
        var prod = _repository.GetMerchant(id)?.IsProd ?? false;
        _repository.DeleteMerchant(id);
        return RedirectToAction(nameof(Index), new { prod });
    }

    [HttpGet]
    public async Task<IActionResult> Dashboard(int id, bool prod = false)
    {
        var merchant = _repository.GetMerchant(id);
        if (merchant is null) return NotFound();
        prod = merchant.IsProd;
        if (prod != HostedProduction)
            return RedirectToAction(nameof(Index), new { prod = HostedProduction });
        SetEnvironment(prod);

        var platform = GetPlatformSession(prod);
        if (platform is not null)
            platform.Balance = await _api.GetBalanceAsync(platform.BaseUrl, platform.Token, platform.Role);

        var model = new DashboardViewModel
        {
            Merchant = merchant,
            IsProd = prod,
            WebhookUrl = BuildWebhookUrl(prod),
            Result = ReadResult(),
            History = _repository.GetHistory(id),
            Webhooks = _repository.GetWebhooks(),
            Platform = platform,
            TopUpRequests = _repository.GetTopUpRequests(id)
        };
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> CreateH2h(H2hPaymentInput input)
    {
        var merchant = _repository.GetMerchant(input.MerchantId);
        if (merchant is null) return NotFound();
        if (!ModelState.IsValid) return RedirectWithError(input, ValidationError());

        var callbackUrl = input.UseWebhook ? BuildWebhookUrl(input.IsProd) : null;
        var result = await _api.CreateSessionAsync(
            merchant, input.IsProd, input.MerchantOrderId.Trim(), input.Amount,
            input.Currency.Trim().ToUpperInvariant(), input.ExchangeRate, callbackUrl,
            ParseTestScenario(input.TestScenario));

        _repository.AddHistory(new HistoryRecord
        {
            MerchantId = merchant.Id,
            Type = "H2H",
            MerchantOrderId = input.MerchantOrderId,
            SessionId = result.SessionId ?? "",
            Amount = input.Amount,
            Currency = input.Currency,
            Status = result.Success ? "Создан" : "Ошибка"
        });
        return RedirectWithResult(input, result);
    }

    [HttpPost]
    public async Task<IActionResult> CreateDebit(DebitPaymentInput input)
    {
        var merchant = _repository.GetMerchant(input.MerchantId);
        if (merchant is null) return NotFound();
        if (!ModelState.IsValid) return RedirectWithError(input, ValidationError());

        var callbackUrl = input.UseWebhook ? BuildWebhookUrl(input.IsProd) : null;
        var result = await _api.CreateDebitSessionAsync(
            merchant, input.IsProd, input.MerchantOrderId.Trim(), input.Amount,
            input.Currency.Trim().ToUpperInvariant(), input.CardNumber.Trim(), callbackUrl,
            ParseTestScenario(input.TestScenario));

        _repository.AddHistory(new HistoryRecord
        {
            MerchantId = merchant.Id,
            Type = "Дебит H2H",
            MerchantOrderId = input.MerchantOrderId,
            SessionId = result.SessionId ?? "",
            Amount = input.Amount,
            Currency = input.Currency,
            Status = result.Success ? "Создан" : "Ошибка"
        });
        return RedirectWithResult(input, result);
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(SessionActionInput input)
    {
        var merchant = _repository.GetMerchant(input.MerchantId);
        if (merchant is null) return NotFound();
        if (!ModelState.IsValid) return RedirectWithError(input, ValidationError());
        var result = await _api.CancelSessionAsync(merchant, input.IsProd, input.SessionId);
        _repository.AddHistory(new HistoryRecord
        {
            MerchantId = merchant.Id,
            Type = "Отмена",
            SessionId = input.SessionId.ToString(),
            Status = result.Success ? "Отменён" : "Ошибка"
        });
        return RedirectWithResult(input, result);
    }

    [HttpPost]
    [RequestFormLimits(MultipartBodyLengthLimit = 55 * 1024 * 1024)]
    public async Task<IActionResult> Dispute(DisputeInput input)
    {
        var merchant = _repository.GetMerchant(input.MerchantId);
        if (merchant is null) return NotFound();
        if (!ModelState.IsValid) return RedirectWithError(input, ValidationError());
        if (input.Files.Count is < 1 or > PaymentApiClient.MaxDisputeFiles)
            return RedirectWithError(input, $"Выберите от 1 до {PaymentApiClient.MaxDisputeFiles} фотографий.");

        var attachments = new List<DisputeAttachment>();
        foreach (var file in input.Files)
        {
            if (file.Length <= 0 || file.Length > PaymentApiClient.MaxDisputeFileBytes)
                return RedirectWithError(input, $"Файл {file.FileName} пустой или больше 10 МБ.");

            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var contentType = extension switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => null
            };
            if (contentType is null)
                return RedirectWithError(input, $"Формат {extension} не поддерживается. Используйте JPG, PNG или WEBP.");

            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream, HttpContext.RequestAborted);
            attachments.Add(new DisputeAttachment(
                Path.GetFileName(file.FileName), contentType, Convert.ToBase64String(stream.ToArray())));
        }

        var result = await _api.OpenDisputeAsync(merchant, input.IsProd, input.SessionId, attachments);
        _repository.AddHistory(new HistoryRecord
        {
            MerchantId = merchant.Id,
            Type = "Диспут",
            SessionId = input.SessionId.ToString(),
            Status = result.Success ? $"Открыт ({attachments.Count} фото)" : "Ошибка"
        });
        return RedirectWithResult(input, result);
    }

    [HttpPost]
    public async Task<IActionResult> PlatformLogin(PlatformLoginInput input)
    {
        if (_repository.GetMerchant(input.MerchantId) is null) return NotFound();
        if (!ModelState.IsValid) return RedirectWithError(input, ValidationError());
        var login = await _api.LoginAsync(input.PlatformBaseUrl, input.Login, input.Password);
        if (!login.Success || string.IsNullOrWhiteSpace(login.Token))
            return RedirectWithError(input, login.Error ?? "Ошибка входа в платформу.");

        var session = new PlatformSession
        {
            Token = login.Token,
            BaseUrl = input.PlatformBaseUrl,
            Login = login.Login ?? input.Login,
            Role = login.Role ?? "",
            UserId = login.UserId ?? Guid.Empty
        };
        HttpContext.Session.SetJson(PlatformKey(input.IsProd), session);
        return RedirectWithResult(input, new ApiResult
        {
            Success = true,
            ResponseJson = $"Авторизация успешна. Роль: {session.Role}; UserId: {session.UserId}"
        });
    }

    [HttpPost]
    public async Task<IActionResult> HeaderPlatformLogin(HeaderPlatformLoginInput input)
    {
        SetEnvironment(input.IsProd);
        var saved = _repository.GetPlatformCredential(input.IsProd, input.Login);
        var loginName = input.Login.Trim();
        var password = string.IsNullOrWhiteSpace(input.Password) ? saved?.Password ?? "" : input.Password;
        if (string.IsNullOrWhiteSpace(loginName) || string.IsNullOrWhiteSpace(password))
            return RedirectToAction(nameof(Index), new { prod = input.IsProd, platformError = "Укажите логин и пароль." });

        var baseUrl = PlatformBaseUrl(input.IsProd);
        var login = await _api.LoginAsync(baseUrl, loginName, password);
        if (!login.Success || string.IsNullOrWhiteSpace(login.Token))
            return RedirectToAction(nameof(Index), new
            {
                prod = input.IsProd,
                platformError = login.Error ?? $"Не удалось войти в {(input.IsProd ? "PROD" : "DEV")}"
            });

        var session = new PlatformSession
        {
            Token = login.Token,
            BaseUrl = baseUrl,
            Login = login.Login ?? loginName,
            Role = login.Role ?? "",
            UserId = login.UserId ?? Guid.Empty
        };
        HttpContext.Session.SetJson(PlatformKey(input.IsProd), session);
        if (input.Remember)
            _repository.SavePlatformCredential(new PlatformCredential
            {
                IsProd = input.IsProd,
                BaseUrl = baseUrl,
                Login = loginName,
                Password = password
            });

        return RedirectToAction(nameof(Index), new { prod = input.IsProd });
    }

    [HttpPost]
    public IActionResult DeletePlatformCredential(bool isProd, string login)
    {
        if (!string.IsNullOrWhiteSpace(login))
            _repository.DeletePlatformCredential(isProd, login);
        SetEnvironment(isProd);
        return RedirectToAction(nameof(Index), new { prod = isProd });
    }

    [HttpPost]
    public async Task<IActionResult> PlatformLogout(int? merchantId, bool isProd)
    {
        var session = GetPlatformSession(isProd);
        if (session is not null) await _api.LogoutAsync(session.BaseUrl, session.Token);
        HttpContext.Session.Remove(PlatformKey(isProd));
        return merchantId is > 0
            ? RedirectToAction(nameof(Dashboard), new { id = merchantId, prod = isProd })
            : RedirectToAction(nameof(Index), new { prod = isProd });
    }

    [HttpPost]
    public async Task<IActionResult> Withdraw(WithdrawalInput input)
    {
        if (_repository.GetMerchant(input.MerchantId) is null) return NotFound();
        if (!ModelState.IsValid) return RedirectWithError(input, ValidationError());
        var platform = GetPlatformSession(input.IsProd);
        if (platform is null) return RedirectWithError(input, "Сначала войдите в платформу.");

        var result = await _api.CreateWithdrawalRequestAsync(
            platform.BaseUrl, platform.Token, input.Amount, input.WalletAddress.Trim());
        _repository.AddHistory(new HistoryRecord
        {
            MerchantId = input.MerchantId,
            Type = "TRC-20 вывод",
            MerchantOrderId = "WITHDRAW",
            Amount = input.Amount,
            Currency = "USDT",
            Status = result.Success ? "Успешно" : "Ошибка"
        });
        return RedirectWithResult(input, result);
    }

    [HttpPost]
    public IActionResult CreateTopUpRequest(TopUpRequestInput input)
    {
        if (_repository.GetMerchant(input.MerchantId) is null) return NotFound();
        if (!ModelState.IsValid) return RedirectWithError(input, ValidationError());
        var merchant = _repository.GetMerchant(input.MerchantId)!;
        var platform = GetPlatformSession(input.IsProd);

        var request = new TopUpRequest
        {
            Id = $"TRC20-{Guid.NewGuid():N}"[..14].ToUpperInvariant(),
            MerchantId = input.MerchantId,
            IsProd = input.IsProd,
            PlatformLogin = platform?.Login ?? merchant.Name,
            PlatformUserId = platform?.UserId ?? Guid.Empty,
            Amount = input.Amount
        };
        _repository.AddTopUpRequest(request);
        var result = new ApiResult
        {
            Success = true,
            ResponseJson = JsonSerializer.Serialize(new
            {
                requestId = request.Id,
                amount = request.Amount,
                currency = "USDT",
                status = request.Status,
                environment = request.IsProd ? "PROD" : "DEV",
                user = request.PlatformLogin
            })
        };
        _repository.AddHistory(new HistoryRecord
        {
            MerchantId = input.MerchantId,
            Type = "TRC-20 заявка",
            MerchantOrderId = request.Id,
            Amount = input.Amount,
            Currency = "USDT",
            Status = request.Status
        });
        return RedirectWithResult(input, result);
    }

    [HttpPost]
    public IActionResult ClearHistory(int merchantId, bool isProd)
    {
        _repository.ClearHistory(merchantId);
        return RedirectToAction(nameof(Dashboard), new { id = merchantId, prod = isProd });
    }

    [HttpPost]
    public IActionResult ClearWebhooks(int merchantId, bool isProd)
    {
        _repository.ClearWebhooks();
        return RedirectToAction(nameof(Dashboard), new { id = merchantId, prod = isProd });
    }

    private IActionResult RedirectWithResult(MerchantOperationInput input, ApiResult result)
    {
        TempData[ResultKey] = JsonSerializer.Serialize(result);
        return RedirectToAction(nameof(Dashboard), new { id = input.MerchantId, prod = input.IsProd });
    }

    private IActionResult RedirectWithError(MerchantOperationInput input, string error) =>
        RedirectWithResult(input, new ApiResult { Success = false, Error = error });

    private ApiResult? ReadResult()
    {
        var json = TempData[ResultKey] as string;
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ApiResult>(json);
    }

    private static int? ParseTestScenario(string? value) =>
        int.TryParse(value, out var scenario) && scenario is >= 0 and <= 2 ? scenario : null;

    private string ValidationError() => string.Join(" ", ModelState.Values
        .SelectMany(value => value.Errors)
        .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage) ? "Проверьте введённые данные." : error.ErrorMessage));

    private string BuildWebhookUrl(bool isProd)
    {
        if (isProd)
        {
            var hostedUrl = _configuration["Payment:HostedWebUrl"] ?? "https://www.traderstop.club/";
            return $"{hostedUrl.TrimEnd('/')}/api/webhooks/payment";
        }
        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}/api/webhooks/payment";
    }

    private string ProductionPaymentBaseUrl() =>
        _configuration["Payment:ProductionBaseUrl"]
        ?? "https://apexpayapi-ffcpb8bcccdzhuea.canadacentral-01.azurewebsites.net/api";

    private string PlatformBaseUrl(bool isProd) => isProd
        ? _configuration["Platform:ProductionBaseUrl"] ?? "https://api.traderstop.club"
        : _configuration["Platform:DevelopmentBaseUrl"] ?? "https://localhost:7150";

    private void SetEnvironment(bool isProd) => HttpContext.Session.SetString(EnvironmentKey, isProd ? "true" : "false");

    private bool HostedProduction => !_environment.IsDevelopment();
    private static string PlatformKey(bool isProd) => isProd ? "Platform:Prod" : "Platform:Dev";
    private PlatformSession? GetPlatformSession(bool isProd) => HttpContext.Session.GetJson<PlatformSession>(PlatformKey(isProd));
}
