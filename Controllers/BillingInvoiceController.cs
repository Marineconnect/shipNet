using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public sealed class BillingInvoiceController(
    IBillingInvoiceReportService billingInvoiceReportService,
    ISqlAuthService authService,
    ILogger<BillingInvoiceController> logger) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        int page = 1,
        int pageSize = 20,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        string? billingCycle = null,
        int? billingYear = null,
        int? tenantId = null,
        int? deviceId = null,
        string? vessel = null,
        string? kitId = null,
        int? pricingPlanId = null,
        string? invoiceType = null,
        string? invoiceStatus = null,
        string? paymentStatus = null,
        string? metricFilter = null,
        string? invoiceValidity = null,
        string? source = null,
        string? invoiceNumber = null,
        string? search = null,
        string sortBy = "createdAt",
        string sortDirection = "desc")
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessBillingInvoice(currentUser))
        {
            return Forbid();
        }

        var filter = new BillingInvoiceFilterViewModel
        {
            DateFrom = dateFrom,
            DateTo = dateTo,
            BillingCycle = billingCycle,
            BillingYear = billingYear,
            TenantId = tenantId,
            DeviceId = deviceId,
            Vessel = vessel,
            KitId = kitId,
            PricingPlanId = pricingPlanId,
            InvoiceType = invoiceType,
            InvoiceStatus = invoiceStatus,
            PaymentStatus = paymentStatus,
            MetricFilter = metricFilter,
            InvoiceValidity = invoiceValidity,
            Source = source,
            InvoiceNumber = invoiceNumber,
            Search = search,
            SortBy = sortBy,
            SortDirection = sortDirection
        };

        var allowedTenantId = GetAllowedTenantId(currentUser);
        var allowedDeviceId = GetAllowedDeviceId(currentUser);
        var pageResult = await billingInvoiceReportService.GetInvoicesAsync(filter, page, pageSize, allowedTenantId, allowedDeviceId, HttpContext.RequestAborted);
        var model = await billingInvoiceReportService.GetIndexOptionsAsync(filter, allowedTenantId, allowedDeviceId, HttpContext.RequestAborted);
        model.Items = pageResult.Items;
        model.Summary = pageResult.Summary;
        model.CurrentPage = pageResult.CurrentPage;
        model.PageSize = pageResult.PageSize;
        model.TotalItems = pageResult.TotalItems;
        model.IsTransactionReupAdmin = IsTransactionReupAdmin(currentUser);

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Export(
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        string? billingCycle = null,
        int? billingYear = null,
        int? tenantId = null,
        int? deviceId = null,
        string? vessel = null,
        string? kitId = null,
        int? pricingPlanId = null,
        string? invoiceType = null,
        string? invoiceStatus = null,
        string? paymentStatus = null,
        string? metricFilter = null,
        string? invoiceValidity = null,
        string? source = null,
        string? invoiceNumber = null,
        string? search = null,
        string sortBy = "createdAt",
        string sortDirection = "desc")
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessBillingInvoice(currentUser))
        {
            return Forbid();
        }

        try
        {
            var canViewCostPrice = CanViewCostPrice(currentUser);
            var bytes = await billingInvoiceReportService.ExportCsvAsync(new BillingInvoiceFilterViewModel
            {
                DateFrom = dateFrom,
                DateTo = dateTo,
                BillingCycle = billingCycle,
                BillingYear = billingYear,
                TenantId = tenantId,
                DeviceId = deviceId,
                Vessel = vessel,
                KitId = kitId,
                PricingPlanId = pricingPlanId,
                InvoiceType = invoiceType,
                InvoiceStatus = invoiceStatus,
                PaymentStatus = paymentStatus,
                MetricFilter = metricFilter,
                InvoiceValidity = invoiceValidity,
                Source = source,
                InvoiceNumber = invoiceNumber,
                Search = search,
                SortBy = sortBy,
                SortDirection = sortDirection
            }, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), canViewCostPrice, HttpContext.RequestAborted);

            return File(bytes, "text/csv; charset=utf-8", $"billing-invoices-{DateTime.Now:yyyyMMddHHmmss}.csv");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to export Billing & Invoice report.");
            throw;
        }
    }

    private async Task<AuthUserRecord?> GetCurrentUserAsync()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, out var userId))
        {
            return null;
        }

        return await authService.GetUserByIdAsync(userId, HttpContext.RequestAborted);
    }

    private static bool CanAccessBillingInvoice(AuthUserRecord? user) => user is not null;

    private static bool IsTransactionReupAdmin(AuthUserRecord? user)
    {
        return user is not null &&
            (user.IsAdmin || string.Equals(user.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase));
    }

    private static bool CanViewCostPrice(AuthUserRecord? user)
    {
        return user is not null &&
            (user.IsAdmin ||
             string.Equals(user.UserType?.Trim(), ManagedUserType.Admin, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(user.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase));
    }

    private static int? GetAllowedTenantId(AuthUserRecord? user)
    {
        return user?.IsTenantUser == true || user?.IsShipAdmin == true || user?.IsCrew == true
            ? user.TenantId ?? -1
            : null;
    }

    private static int? GetAllowedDeviceId(AuthUserRecord? user)
    {
        return user?.IsShipAdmin == true || user?.IsCrew == true
            ? user.DeviceId ?? -1
            : null;
    }
}
