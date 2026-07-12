using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging.EventLog;
using System.Security.Cryptography.X509Certificates;
using StarlinkDeviceManager.Filters;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.InvoiceRabbitMq.json", optional: true, reloadOnChange: true);
builder.WebHost.UseIISIntegration();

if (OperatingSystem.IsWindows())
{
    builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.None);
}

if (builder.Environment.IsDevelopment())
{
    var certificatePath = Path.Combine(builder.Environment.ContentRootPath, "certs", "portal.shipnet.local.pfx");
    var httpsPort = int.TryParse(Environment.GetEnvironmentVariable("SHIPNET_HTTPS_PORT"), out var configuredPort)
        ? configuredPort
        : 5001;
    var certificateThumbprint = "43E5E80555563FAB2A7256B4A19D5475CEFDBF56";

    builder.WebHost.ConfigureKestrel(options =>
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        var certificates = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            certificateThumbprint,
            validOnly: false);

        var certificate = certificates.Count > 0
            ? certificates[0]
            : new X509Certificate2(
                certificatePath,
                "ShipnetLocalDev!2026",
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);

        options.ListenLocalhost(httpsPort, listenOptions =>
        {
            listenOptions.UseHttps(certificate);
        });
    });
}

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<ViewOnlyGuardFilter>();
});
builder.Services.AddDataProtection();
builder.Services.AddHttpClient();
builder.Services.AddScoped<ISqlAuthService, SqlAuthService>();
builder.Services.AddScoped<IDeviceService, DeviceService>();
builder.Services.AddScoped<ITenantService, TenantService>();
builder.Services.AddScoped<IPricingPlanService, PricingPlanService>();
builder.Services.AddScoped<IMonthlySubscriptionService, MonthlySubscriptionService>();
builder.Services.AddScoped<ICurrencyExchangeService, CurrencyExchangeService>();
builder.Services.AddScoped<ISystemSettingsService, SystemSettingsService>();
builder.Services.AddScoped<IPaymentTransactionService, PaymentTransactionService>();
builder.Services.AddScoped<ITelegramNotificationService, TelegramNotificationService>();
builder.Services.AddScoped<IKitExportService, KitExportService>();
builder.Services.Configure<InvoiceRabbitMqOptions>(builder.Configuration.GetSection(InvoiceRabbitMqOptions.SectionName));
builder.Services.AddSingleton<IInvoiceRabbitMqPublisher, InvoiceRabbitMqPublisher>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(24);
        options.SlidingExpiration = false;
    });

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.Run();
