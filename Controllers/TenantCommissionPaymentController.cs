using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public sealed class TenantCommissionPaymentController(
    ITenantCommissionPaymentService tenantCommissionPaymentService,
    ISqlAuthService authService,
    ILogger<TenantCommissionPaymentController> logger) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        int page = 1,
        int pageSize = 20,
        int? tenantId = null,
        DateTime? paymentDateFrom = null,
        DateTime? paymentDateTo = null,
        DateTime? periodFrom = null,
        DateTime? periodTo = null,
        string? sourceMode = null,
        string? keyword = null,
        string sortBy = "paymentDate",
        string sortDirection = "desc")
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
        {
            return Forbid();
        }

        var allowedTenantId = GetAllowedTenantId(currentUser);
        var model = await tenantCommissionPaymentService.GetIndexAsync(
            new TenantCommissionPaymentFilterViewModel
            {
                TenantId = tenantId,
                PaymentDateFrom = paymentDateFrom,
                PaymentDateTo = paymentDateTo,
                PeriodFrom = periodFrom,
                PeriodTo = periodTo,
                SourceMode = sourceMode,
                Keyword = keyword,
                SortBy = sortBy,
                SortDirection = sortDirection
            },
            page,
            pageSize,
            allowedTenantId,
            CanCreatePayment(currentUser),
            HttpContext.RequestAborted);

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> EligibleCycles(int tenantId, DateTime? dateFrom = null, DateTime? dateTo = null, string? search = null)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
        {
            return Forbid();
        }

        try
        {
            var cycles = await tenantCommissionPaymentService.SearchEligibleCyclesAsync(
                tenantId,
                dateFrom,
                dateTo,
                search,
                GetAllowedTenantId(currentUser),
                HttpContext.RequestAborted);
            return Json(cycles);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpGet]
    public async Task<IActionResult> Detail(long id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser is null)
        {
            return Forbid();
        }

        var detail = await tenantCommissionPaymentService.GetDetailAsync(id, GetAllowedTenantId(currentUser), HttpContext.RequestAborted);
        if (detail is null)
        {
            return NotFound(new { success = false, message = "Payment was not found." });
        }

        return Json(new { success = true, data = detail });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateManual(TenantCommissionManualPaymentInput input)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanCreatePayment(currentUser))
        {
            return Forbid();
        }

        try
        {
            var paymentId = await tenantCommissionPaymentService.CreateManualPaymentAsync(
                input,
                currentUser!.Id,
                currentUser.Username,
                GetAllowedTenantId(currentUser),
                HttpContext.RequestAborted);
            TempData["CommissionPaymentSuccess"] = $"Đã ghi nhận thanh toán hoa hồng #{paymentId}.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Tenant commission manual payment rejected.");
            TempData["CommissionPaymentError"] = exception.GetBaseException().Message;
        }

        return RedirectToAction(nameof(Index), new { tenantId = input.TenantId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateBillingCycles(TenantCommissionCyclePaymentInput input)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanCreatePayment(currentUser))
        {
            return Forbid();
        }

        try
        {
            var paymentId = await tenantCommissionPaymentService.CreateBillingCyclePaymentAsync(
                input,
                currentUser!.Id,
                currentUser.Username,
                GetAllowedTenantId(currentUser),
                HttpContext.RequestAborted);
            TempData["CommissionPaymentSuccess"] = $"Đã ghi nhận thanh toán hoa hồng #{paymentId}.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Tenant commission billing-cycle payment rejected.");
            TempData["CommissionPaymentError"] = exception.GetBaseException().Message;
        }

        return RedirectToAction(nameof(Index), new { tenantId = input.TenantId });
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

    private static bool CanCreatePayment(AuthUserRecord? user)
    {
        return user is not null &&
            !user.IsViewOnly &&
            (user.IsAdmin || string.Equals(user.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase));
    }

    private static int? GetAllowedTenantId(AuthUserRecord? user)
    {
        return user?.IsTenantUser == true || user?.IsShipAdmin == true || user?.IsCrew == true
            ? user.TenantId ?? -1
            : null;
    }
}
