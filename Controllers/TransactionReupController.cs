using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public sealed class TransactionReupController(
    ITransactionReupService service,
    ISqlAuthService authService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? message = null, bool success = false)
    {
        var user = await GetCurrentUserAsync();
        if (!IsAdmin(user)) return Forbid();
        var model = new TransactionReupIndexViewModel
        {
            Message = message ?? string.Empty,
            IsSuccess = success
        };
        try
        {
            model.Batches = (await service.GetBatchesAsync(HttpContext.RequestAborted)).ToList();
        }
        catch (InvalidOperationException exception)
        {
            model.Message = exception.Message;
            model.IsSuccess = false;
        }
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Import([Bind(Prefix = "Import")] TransactionReupImportViewModel model)
    {
        var user = await GetCurrentUserAsync();
        if (!IsAdmin(user)) return Forbid();
        if (!ModelState.IsValid)
        {
            var invalidModel = new TransactionReupIndexViewModel
            {
                Import = model,
                Message = "Please correct the import form errors before continuing.",
                IsSuccess = false
            };
            try
            {
                invalidModel.Batches = (await service.GetBatchesAsync(HttpContext.RequestAborted)).ToList();
            }
            catch (InvalidOperationException exception)
            {
                invalidModel.Message = exception.Message;
            }
            return View(nameof(Index), invalidModel);
        }
        try
        {
            var result = await service.ImportAsync(model, user!, HttpContext.RequestAborted);
            return RedirectToAction(nameof(Index), new { message = $"{result.Message} ID {result.FirstInvoiceNumber} - {result.LastInvoiceNumber}; next {result.NextInvoiceNumber}.", success = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            var errorModel = new TransactionReupIndexViewModel
            {
                Import = model,
                Message = exception.Message
            };
            try
            {
                errorModel.Batches = (await service.GetBatchesAsync(HttpContext.RequestAborted)).ToList();
            }
            catch (InvalidOperationException)
            {
            }
            return View(nameof(Index), errorModel);
        }
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        if (!IsAdmin(await GetCurrentUserAsync())) return Forbid();
        var model = await service.GetDetailsAsync(id, HttpContext.RequestAborted);
        return model is null ? NotFound() : View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Download(int id)
    {
        if (!IsAdmin(await GetCurrentUserAsync())) return Forbid();
        var path = await service.GetOriginalFilePathAsync(id, HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(path)) return NotFound();
        var stream = await serviceFile(path);
        if (stream is null) return NotFound();
        var details = await service.GetDetailsAsync(id, HttpContext.RequestAborted);
        return File(stream, "application/octet-stream", details?.Batch.OriginalFileName ?? "transaction-reup-import");

        async Task<Stream?> serviceFile(string relativePath)
        {
            var storage = HttpContext.RequestServices.GetRequiredService<ITransactionReupFileStorage>();
            return await storage.OpenReadAsync(relativePath, HttpContext.RequestAborted);
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryFailed(int id)
    {
        var user = await GetCurrentUserAsync();
        if (!IsAdmin(user)) return Forbid();
        await service.RetryFailedAsync(id, user!, HttpContext.RequestAborted);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryItem(int itemId)
    {
        var user = await GetCurrentUserAsync();
        if (!IsAdmin(user)) return Forbid();
        await service.RetryItemAsync(itemId, user!, HttpContext.RequestAborted);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Payload(int itemId)
    {
        if (!IsAdmin(await GetCurrentUserAsync())) return Forbid();
        var item = await service.GetItemAsync(itemId, HttpContext.RequestAborted);
        return item is null ? NotFound() : Content(item.PayloadJson, "application/json");
    }

    private async Task<AuthUserRecord?> GetCurrentUserAsync()
    {
        return int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await authService.GetUserByIdAsync(id, HttpContext.RequestAborted)
            : null;
    }

    private static bool IsAdmin(AuthUserRecord? user) =>
        string.Equals(user?.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase);
}
