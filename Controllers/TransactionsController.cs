using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class TransactionsController(
    IPaymentTransactionService paymentTransactionService,
    ISqlAuthService authService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(
        int page = 1,
        int pageSize = 20,
        string? search = null,
        string? invoiceNumber = null,
        string? paymentStatus = null,
        string? paymentMethod = null,
        string? qrState = null,
        int? tenantId = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessTransactions(currentUser))
        {
            return Forbid();
        }

        var model = await paymentTransactionService.GetTransactionsAsync(
            new PaymentTransactionFilterViewModel
            {
                Search = search,
                InvoiceNumber = invoiceNumber,
                PaymentStatus = paymentStatus,
                PaymentMethod = paymentMethod,
                QrState = qrState,
                TenantId = tenantId,
                DateFrom = dateFrom,
                DateTo = dateTo
            },
            page,
            pageSize,
            GetAllowedTenantId(currentUser),
            GetAllowedDeviceId(currentUser),
            CanManageTransactions(currentUser),
            HttpContext.RequestAborted);
        model.IsTransactionReupAdmin = IsTransactionReupAdmin(currentUser);

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int invoiceId)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessTransactions(currentUser))
        {
            return Forbid();
        }

        var detail = await paymentTransactionService.GetTransactionDetailAsync(
            invoiceId,
            GetAllowedTenantId(currentUser),
            GetAllowedDeviceId(currentUser),
            HttpContext.RequestAborted);

        return detail is null ? NotFound() : Json(detail);
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

    private static bool CanAccessTransactions(AuthUserRecord? user)
    {
        return user is not null
            && !user.IsShipAdmin
            && !user.IsCrew
            && (user.CanManageTransactions
                || string.Equals(user.UserType?.Trim(), ManagedUserType.Admin, StringComparison.OrdinalIgnoreCase)
                || string.Equals(user.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase));
    }

    private static bool CanManageTransactions(AuthUserRecord? user)
    {
        return CanAccessTransactions(user) && user?.IsViewOnly != true;
    }

    private static bool IsTransactionReupAdmin(AuthUserRecord? user)
    {
        return string.Equals(user?.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase);
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
