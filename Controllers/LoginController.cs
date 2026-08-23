using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using H2HClientWeb.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace H2HClientWeb.Controllers;

[AllowAnonymous]
public sealed class LoginController : Controller
{
    private readonly IConfiguration _configuration;

    public LoginController(IConfiguration configuration) => _configuration = configuration;

    [HttpGet("/login")]
    public IActionResult Index(string? returnUrl = null)
    {
        ViewBag.ReturnUrl = returnUrl;
        return View(new LoginViewModel());
    }

    [HttpPost("/login")]
    public async Task<IActionResult> Index(LoginViewModel model, string? returnUrl = null)
    {
        var configuredPassword = Environment.GetEnvironmentVariable("H2H_ADMIN_PASSWORD")
            ?? _configuration["Admin:Password"];

        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            model.Error = "На сервере не задан H2H_ADMIN_PASSWORD.";
            return View(model);
        }

        if (!FixedEquals(model.Password, configuredPassword))
        {
            model.Error = "Неверный пароль.";
            return View(model);
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "H2H Admin")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    [HttpPost("/logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        HttpContext.Session.Clear();
        return RedirectToAction(nameof(Index));
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
