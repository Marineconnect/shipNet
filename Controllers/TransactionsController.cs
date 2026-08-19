using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class TransactionsController(
    IPaymentTransactionService paymentTransactionService,
    ITransactionReupService transactionReupService,
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
        DateTime? dateTo = null,
        string? message = null)
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
        model.Message = message ?? string.Empty;

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

    [HttpGet]
    public async Task<IActionResult> ReupPdfCandidates(
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
        if (!IsTransactionReupAdmin(currentUser) || currentUser?.IsViewOnly == true)
        {
            return Forbid();
        }

        var filter = new PaymentTransactionFilterViewModel
        {
            Search = search,
            InvoiceNumber = invoiceNumber,
            PaymentStatus = paymentStatus,
            PaymentMethod = paymentMethod,
            QrState = qrState,
            TenantId = tenantId,
            DateFrom = dateFrom,
            DateTo = dateTo
        };

        var invoiceIds = await paymentTransactionService.GetFilteredTransactionInvoiceIdsAsync(
            filter,
            GetAllowedTenantId(currentUser),
            GetAllowedDeviceId(currentUser),
            HttpContext.RequestAborted);
        var candidates = await paymentTransactionService.GetTransactionReupCandidatesAsync(
            invoiceIds,
            GetAllowedTenantId(currentUser),
            GetAllowedDeviceId(currentUser),
            HttpContext.RequestAborted);

        return Json(new
        {
            success = true,
            count = candidates.Count,
            items = candidates.Select(item => new
            {
                item.InvoiceId,
                item.InvoiceNumber,
                PaymentNo = item.SourceTransactionCode,
                item.SourceTransactionCode,
                item.SourceRequestCode,
                item.TenantName,
                item.VesselName,
                item.KitNumber
            })
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReupPdf(TransactionReupSelectionRequest request)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!IsTransactionReupAdmin(currentUser) || currentUser?.IsViewOnly == true)
        {
            return Forbid();
        }

        try
        {
            var result = await transactionReupService.CreateFromTransactionSelectionAsync(
                request,
                currentUser!,
                GetAllowedTenantId(currentUser),
                GetAllowedDeviceId(currentUser),
                HttpContext.RequestAborted);

            return RedirectToAction(nameof(TransactionReupController.Details), "TransactionReup", new { id = result.BatchId, clearReupSelection = true });
        }
        catch (InvalidOperationException exception)
        {
            return RedirectToAction(nameof(Index), new
            {
                message = exception.Message,
                page = 1,
                pageSize = 20,
                search = request.Filter.Search,
                invoiceNumber = request.Filter.InvoiceNumber,
                paymentStatus = request.Filter.PaymentStatus,
                paymentMethod = request.Filter.PaymentMethod,
                qrState = request.Filter.QrState,
                tenantId = request.Filter.TenantId,
                dateFrom = request.Filter.DateFrom?.ToString("yyyy-MM-dd"),
                dateTo = request.Filter.DateTo?.ToString("yyyy-MM-dd")
            });
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
        return user is not null &&
            (user.IsAdmin || string.Equals(user.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase));
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
