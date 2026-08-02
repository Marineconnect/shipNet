using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
[Route("KvhSolutions/SubscriptionOperations")]
public sealed class KvhSubscriptionOperationsController(
    IKvhSubscriptionOperationService operationService,
    ISqlAuthService authService,
    ILogger<KvhSubscriptionOperationsController> logger) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(
        int page = 1,
        int pageSize = 20,
        string? search = null,
        string? operationType = null,
        string? status = null,
        int? tenantId = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        string? createdBy = null)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessSolutions(currentUser)) return Forbid();

        var model = await operationService.GetBatchesAsync(
            new KvhSubscriptionOperationFilter
            {
                Search = search,
                OperationType = operationType,
                Status = status,
                TenantId = tenantId,
                DateFrom = dateFrom,
                DateTo = dateTo,
                CreatedBy = createdBy
            },
            page,
            pageSize,
            GetAllowedTenantId(currentUser),
            GetAllowedDeviceId(currentUser),
            CanManageSolutions(currentUser),
            HttpContext.RequestAborted);

        return View("~/Views/KvhSolutions/SubscriptionOperations/Index.cshtml", model);
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(KvhSubscriptionOperationCreateRequest request)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessSolutions(currentUser)) return Forbid();
        if (!CanManageSolutions(currentUser)) return Forbid();

        try
        {
            var id = await operationService.CreateBatchAsync(request, GetCurrentUserId(), GetRequestedBy(), GetAllowedTenantId(currentUser), HttpContext.RequestAborted);
            TempData["KvhSolutionSuccess"] = "Da tao dot Pause/Resume.";
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot create KVH subscription operation batch.");
            TempData["KvhSolutionError"] = ex.GetBaseException().Message;
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Details(long id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessSolutions(currentUser)) return Forbid();
        var model = await operationService.GetBatchAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), CanManageSolutions(currentUser), HttpContext.RequestAborted);
        if (model is null) return NotFound();

        ViewData["ImportPreviewJson"] = TempData["KvhOperationImportPreview"] as string;
        return View("~/Views/KvhSolutions/SubscriptionOperations/Details.cshtml", model);
    }

    [HttpPost("{id:long}/AddDevices")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDevices(long id, KvhSubscriptionOperationAddDevicesRequest request)
    {
        return await ExecuteBatchActionAsync(id, async (user, token) =>
        {
            var count = await operationService.AddDevicesAsync(id, request.DeviceIds, GetCurrentUserId(), GetRequestedBy(), GetAllowedTenantId(user), GetAllowedDeviceId(user), token);
            TempData["KvhSolutionSuccess"] = $"Da them {count} thiet bi.";
        });
    }

    [HttpPost("{id:long}/ImportPreview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportPreview(long id, IFormFile file)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessSolutions(currentUser)) return Forbid();
        if (!CanManageSolutions(currentUser)) return Forbid();

        try
        {
            var preview = await operationService.PreviewImportAsync(id, file, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            var count = await operationService.ConfirmImportAsync(id, preview, GetCurrentUserId(), GetRequestedBy(), GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            var message = $"Đã import {count} dòng hợp lệ từ {preview.TotalRows} dòng Excel.";
            if (preview.ErrorRows > 0)
            {
                var sampleErrors = preview.Rows
                    .Where(row => !row.IsValid)
                    .Take(5)
                    .Select(row => $"dòng {row.RowNumber}: {row.Message}")
                    .ToArray();
                message += $" Có {preview.ErrorRows} dòng lỗi";
                if (sampleErrors.Length > 0)
                {
                    message += $": {string.Join("; ", sampleErrors)}";
                }
                message += ".";
            }
            TempData["KvhSolutionSuccess"] = message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot preview KVH subscription operation import for batch {BatchId}.", id);
            TempData["KvhSolutionError"] = ex.GetBaseException().Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:long}/ConfirmImport")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmImport(long id, string previewJson)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessSolutions(currentUser)) return Forbid();
        if (!CanManageSolutions(currentUser)) return Forbid();

        try
        {
            var preview = JsonSerializer.Deserialize<KvhSubscriptionOperationImportPreview>(previewJson) ?? new KvhSubscriptionOperationImportPreview();
            var count = await operationService.ConfirmImportAsync(id, preview, GetCurrentUserId(), GetRequestedBy(), GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            TempData["KvhSolutionSuccess"] = $"Da import {count} dong hop le.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot confirm KVH subscription operation import for batch {BatchId}.", id);
            TempData["KvhSolutionError"] = ex.GetBaseException().Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:long}/RemoveItem")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveItem(long id, KvhSubscriptionOperationRemoveItemRequest request)
    {
        return await ExecuteBatchActionAsync(id, async (user, token) =>
        {
            await operationService.RemoveItemAsync(id, request.ItemId, GetCurrentUserId(), GetRequestedBy(), GetAllowedTenantId(user), GetAllowedDeviceId(user), token);
            TempData["KvhSolutionSuccess"] = "Da xoa thiet bi khoi dot.";
        });
    }

    [HttpPost("{id:long}/Validate")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ValidateBatch(long id)
    {
        return ExecuteBatchActionAsync(id, async (user, token) =>
        {
            var count = await operationService.ValidateBatchAsync(id, GetAllowedTenantId(user), GetAllowedDeviceId(user), token);
            TempData["KvhSolutionSuccess"] = $"Da kiem tra {count} dong.";
        });
    }

    [HttpPost("{id:long}/Start")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Start(long id)
    {
        return ExecuteBatchActionAsync(id, async (user, token) =>
        {
            await operationService.StartBatchAsync(id, GetCurrentUserId(), GetRequestedBy(), GetAllowedTenantId(user), GetAllowedDeviceId(user), token);
            TempData["KvhSolutionSuccess"] = "Da dua dot vao hang doi xu ly.";
        });
    }

    [HttpPost("{id:long}/Cancel")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Cancel(long id)
    {
        return ExecuteBatchActionAsync(id, async (user, token) =>
        {
            await operationService.CancelBatchAsync(id, GetCurrentUserId(), GetRequestedBy(), GetAllowedTenantId(user), GetAllowedDeviceId(user), token);
            TempData["KvhSolutionSuccess"] = "Da gui yeu cau huy dot.";
        });
    }

    [HttpPost("{id:long}/RetryFailed")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RetryFailed(long id)
    {
        return ExecuteBatchActionAsync(id, async (user, token) =>
        {
            await operationService.RetryFailedAsync(id, GetCurrentUserId(), GetRequestedBy(), GetAllowedTenantId(user), GetAllowedDeviceId(user), token);
            TempData["KvhSolutionSuccess"] = "Da dua cac dong loi vao hang doi chay lai.";
        });
    }

    [HttpGet("{id:long}/Status")]
    public async Task<IActionResult> Status(long id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessSolutions(currentUser)) return Forbid();
        var model = await operationService.GetBatchAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), CanManageSolutions(currentUser), HttpContext.RequestAborted);
        return model is null
            ? NotFound()
            : Json(new
            {
                model.Id,
                model.Status,
                model.ProgressPercent,
                model.TotalItems,
                model.CompletedItems,
                model.ReadyItems,
                model.QueuedItems,
                model.PendingItems,
                model.JobSuccessItems,
                model.JobFailedItems,
                model.VerifiedItems,
                model.VerificationMismatchItems,
                model.CancelledItems
            });
    }

    [HttpGet("{id:long}/Export")]
    public async Task<IActionResult> Export(long id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessSolutions(currentUser)) return Forbid();
        var content = await operationService.ExportAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"kvh-operation-{id}.xlsx");
    }

    [HttpGet("DownloadTemplate")]
    public IActionResult DownloadTemplate()
    {
        if (!string.Equals(User.Identity?.Name?.Trim(), "admin", StringComparison.OrdinalIgnoreCase)) return Forbid();

        var content = operationService.BuildTemplate();
        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "kvh-subscription-operation-template.xlsx");
    }

    private async Task<IActionResult> ExecuteBatchActionAsync(long id, Func<AuthUserRecord?, CancellationToken, Task> action)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessSolutions(currentUser)) return Forbid();
        if (!CanManageSolutions(currentUser)) return Forbid();

        try
        {
            await action(currentUser, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "KVH subscription operation action failed for batch {BatchId}.", id);
            TempData["KvhSolutionError"] = ex.GetBaseException().Message;
        }

        return RedirectToAction(nameof(Details), new { id });
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

    private int? GetCurrentUserId()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(userIdValue, out var userId) ? userId : null;
    }

    private string GetRequestedBy() => User.FindFirstValue("DisplayName") ?? User.Identity?.Name ?? "system";

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

    private static bool CanManageSolutions(AuthUserRecord? user)
    {
        return CanAccessSolutions(user) && user?.IsViewOnly != true;
    }

    private static bool CanAccessSolutions(AuthUserRecord? user)
    {
        return string.Equals(user?.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase);
    }
}
