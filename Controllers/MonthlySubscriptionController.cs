using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class MonthlySubscriptionController(
    IMonthlySubscriptionService subscriptionService,
    ITenantService tenantService,
    ISqlAuthService authService,
    ILogger<MonthlySubscriptionController> logger) : Controller
{
    private const string IndexViewPath = "~/Views/MonthlySubscription/Index.cshtml";
    private const string DetailViewPath = "~/Views/MonthlySubscription/Details.cshtml";

    [HttpGet]
    public async Task<IActionResult> Index(
        int page = 1,
        int pageSize = 10,
        int? tenantId = null,
        int? deviceId = null,
        int? pricingPlanId = null,
        string? kitId = null,
        string? status = null,
        string? invoiceStatus = null,
        DateTime? monthFrom = null,
        DateTime? monthTo = null,
        DateTime? nextBillingFrom = null,
        DateTime? nextBillingTo = null,
        DateTime? invoicePaidFrom = null,
        DateTime? invoicePaidTo = null)
    {
        var currentUser = await GetCurrentUserAsync();
        var allowedTenantId = GetAllowedTenantId(currentUser);
        var allowedDeviceId = GetAllowedDeviceId(currentUser);
        var filter = new MonthlySubscriptionFilterViewModel
        {
            TenantId = tenantId,
            DeviceId = deviceId,
            PricingPlanId = pricingPlanId,
            KitId = kitId,
            Status = status,
            InvoiceStatus = invoiceStatus,
            MonthFrom = monthFrom,
            MonthTo = monthTo,
            NextBillingFrom = nextBillingFrom,
            NextBillingTo = nextBillingTo,
            InvoicePaidFrom = invoicePaidFrom,
            InvoicePaidTo = invoicePaidTo
        };

        if (allowedTenantId.HasValue)
        {
            filter.TenantId = allowedTenantId.Value;
        }

        if (allowedDeviceId.HasValue)
        {
            filter.DeviceId = allowedDeviceId.Value;
        }

        var pageResult = await subscriptionService.GetSubscriptionsAsync(filter, page, pageSize, allowedTenantId, allowedDeviceId, HttpContext.RequestAborted);
        var tenants = await tenantService.GetTenantOptionsAsync(allowedTenantId, HttpContext.RequestAborted);
        var devices = await subscriptionService.GetDeviceOptionsAsync(allowedTenantId, allowedDeviceId, HttpContext.RequestAborted);
        var plans = await subscriptionService.GetPlanOptionsAsync(allowedTenantId, allowedDeviceId, HttpContext.RequestAborted);

        return View(IndexViewPath, new MonthlySubscriptionIndexViewModel
        {
            Subscriptions = pageResult.Subscriptions,
            Summary = pageResult.Summary,
            Tenants = tenants,
            Devices = devices,
            Plans = plans,
            Filter = filter,
            CurrentPage = pageResult.CurrentPage,
            PageSize = pageResult.PageSize,
            TotalItems = pageResult.TotalItems,
            IsTenantScoped = allowedTenantId.HasValue,
            CanManageSubscriptions = CanManageSubscriptions(currentUser)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(MonthlySubscriptionIndexViewModel requestModel)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        var model = requestModel.CreateForm;
        NormalizeCreateModel(model);
        if (!ValidateCreateModel(model))
        {
            TempData["SubscriptionError"] = "Vui lòng nhập đầy đủ thông tin subscription hợp lệ.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            var subscriptionIds = await subscriptionService.CreateSubscriptionAsync(model, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            var firstSubscriptionId = subscriptionIds.FirstOrDefault();
            TempData["SubscriptionSuccess"] = subscriptionIds.Count == 1
                ? $"Tạo subscription #{firstSubscriptionId} thành công."
                : $"Tạo {subscriptionIds.Count} subscription theo từng chu kỳ thành công.";
            return firstSubscriptionId > 0
                ? RedirectToAction(nameof(Details), new { id = firstSubscriptionId })
                : RedirectToAction(nameof(Index));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create monthly subscription.");
            TempData["SubscriptionError"] = $"Không thể tạo subscription. Chi tiết: {exception.GetBaseException().Message}";
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var currentUser = await GetCurrentUserAsync();
        var detail = await subscriptionService.GetSubscriptionDetailAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
        if (detail is null)
        {
            return NotFound();
        }

        detail.CanManageSubscriptions = CanManageSubscriptions(currentUser);
        detail.CanViewQrSessions = CanViewQrSessions(currentUser);
        return View(DetailViewPath, detail);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateInvoice(CreateSubscriptionInvoiceViewModel model)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            await subscriptionService.CreateInvoiceAsync(model, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            TempData["SubscriptionSuccess"] = "Tạo invoice thành công.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create subscription invoice.");
            TempData["SubscriptionError"] = $"Không thể tạo invoice. Chi tiết: {exception.GetBaseException().Message}";
        }

        return RedirectToAction(nameof(Details), new { id = model.SubscriptionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateInvoice(UpdateSubscriptionInvoiceViewModel model)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            await subscriptionService.UpdateInvoiceAsync(model, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            TempData["SubscriptionSuccess"] = "Cập nhật invoice thành công.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update subscription invoice.");
            TempData["SubscriptionError"] = $"Không thể cập nhật invoice. Chi tiết: {exception.GetBaseException().Message}";
        }

        return RedirectToAction(nameof(Details), new { id = model.SubscriptionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateBilling(UpdateMonthlySubscriptionBillingViewModel model)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            await subscriptionService.UpdateSubscriptionBillingAsync(model, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            TempData["SubscriptionSuccess"] = "Cập nhật kỳ billing subscription thành công.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update subscription billing.");
            TempData["SubscriptionError"] = $"Không thể cập nhật kỳ billing subscription. Chi tiết: {exception.GetBaseException().Message}";
        }

        return RedirectToAction(nameof(Details), new { id = model.SubscriptionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(UpdateMonthlySubscriptionStatusViewModel model)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            await subscriptionService.UpdateSubscriptionStatusAsync(model, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            TempData["SubscriptionSuccess"] = "Cập nhật trạng thái subscription thành công.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update subscription status.");
            TempData["SubscriptionError"] = $"Không thể cập nhật trạng thái subscription. Chi tiết: {exception.GetBaseException().Message}";
        }

        return RedirectToAction(nameof(Details), new { id = model.SubscriptionId });
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

    private static bool CanManageSubscriptions(AuthUserRecord? user)
    {
        return user is not null && !user.IsViewOnly && !user.IsShipAdmin && !user.IsCrew;
    }

    private static bool CanViewQrSessions(AuthUserRecord? user)
    {
        return string.Equals(user?.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase);
    }

    private (int? UserId, string Username) GetCurrentAuditContext()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
        var username = string.IsNullOrWhiteSpace(User.Identity?.Name) ? "system" : User.Identity.Name!;
        return (userId, username);
    }

    private static void NormalizeCreateModel(CreateMonthlySubscriptionViewModel model)
    {
        model.SubscriptionType = string.IsNullOrWhiteSpace(model.SubscriptionType) ? "Personal" : model.SubscriptionType.Trim();
        model.UsageMonth = model.UsageMonth == default ? default : new DateTime(model.UsageMonth.Year, model.UsageMonth.Month, 1);
        model.StartDate = model.StartDate.Date;
        model.EndDate = model.EndDate.Date;
        model.NextBillingDate = model.NextBillingDate.Date;
    }

    private static bool ValidateCreateModel(CreateMonthlySubscriptionViewModel model)
    {
        return model.TenantId > 0
            && model.DeviceId > 0
            && model.PricingPlanId > 0
            && model.UsageMonth != default
            && model.StartDate != default
            && model.EndDate != default
            && model.NextBillingDate != default
            && model.StartDate <= model.EndDate;
    }
}
