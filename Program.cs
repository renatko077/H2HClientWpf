using H2HClientWeb.Data;
using H2HClientWeb.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using System.Diagnostics;
using System.Globalization;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDirectory);

var dataProtection = builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
    .SetApplicationName("H2HClientWeb");
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = "H2HClientWeb.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization(options => options.FallbackPolicy = options.DefaultPolicy);
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "H2HClientWeb.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromHours(8);
});

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute());
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});

builder.Services.AddSingleton<AppRepository>();
builder.Services.AddSingleton<TronTestnetClient>();
builder.Services.AddHttpClient<PaymentApiClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (message, _, _, errors) =>
        {
            var host = message.RequestUri?.Host;
            var isLocal = host is "localhost" or "127.0.0.1";
            return errors == System.Net.Security.SslPolicyErrors.None || isLocal;
        }
    });

var app = builder.Build();

if (app.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("LaunchBrowser"))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault(value =>
                          value.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
                          value.StartsWith("https://localhost", StringComparison.OrdinalIgnoreCase))
                      ?? addresses?.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(address)) return;

        var browserUrl = address
            .Replace("0.0.0.0", "localhost", StringComparison.OrdinalIgnoreCase)
            .Replace("[::]", "localhost", StringComparison.OrdinalIgnoreCase)
            .Replace("+", "localhost", StringComparison.OrdinalIgnoreCase);

        try
        {
            Process.Start(new ProcessStartInfo(browserUrl) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            app.Logger.LogWarning(exception, "Не удалось автоматически открыть браузер по адресу {BrowserUrl}", browserUrl);
        }
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
app.UseStaticFiles();

app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Merchants}/{action=Index}/{id?}");

app.Run();
