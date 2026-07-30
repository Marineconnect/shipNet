using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class KvhSolutionsController(
    IKvhSubscriptionService kvhSubscriptionService,
    IKvhBulkSyncService kvhBulkSyncService,
    IDeviceService deviceService,
    ISqlAuthService authService,
    ILogger<KvhSolutionsController> logger) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, int pageSize = 20, string? search = null, string? status = null, string? region = null, int? tenantId = null, string? syncState = null)
    {
        var currentUser = await GetCurrentUserAsync();
        var model = await kvhSubscriptionService.GetSolutionsAsync(
            new KvhSolutionFilter { Search = search, Status = status, Region = region, TenantId = tenantId, SyncState = syncState },
            page,
            pageSize,
            GetAllowedTenantId(currentUser),
            GetAllowedDeviceId(currentUser),
            CanManageSolutions(currentUser),
            HttpContext.RequestAborted);
        model.RecentBatches = (await kvhBulkSyncService.GetRecentBatchesAsync(GetAllowedTenantId(currentUser), HttpContext.RequestAborted)).ToList();
        return View("~/Views/KvhSolutions/Index.cshtml", model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int deviceId)
    {
        var currentUser = await GetCurrentUserAsync();
        var detail = await kvhSubscriptionService.GetSolutionDetailAsync(deviceId, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), CanManageSolutions(currentUser), HttpContext.RequestAborted);
        return detail is null ? NotFound() : View("~/Views/KvhSolutions/Details.cshtml", detail);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(int deviceId)
    {
        var currentUser = await GetCurrentUserAsync();
        var existingDevice = await deviceService.GetDeviceByIdAsync(deviceId, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
        if (existingDevice is null)
        {
            return NotFound();
        }

        var result = await kvhSubscriptionService.SyncDeviceSubscriptionAsync(deviceId, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
        TempData[result.Success ? "KvhSolutionSuccess" : "KvhSolutionError"] = result.Success
            ? $"Synchronized {result.ReturnedCount} subscription entry."
            : result.MessageEn;
        return RedirectToAction(nameof(Details), new { deviceId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSyncBatch(KvhBatchCreateRequest request)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSolutions(currentUser))
        {
            return Forbid();
        }

        var result = await kvhBulkSyncService.CreateBatchAsync(
            request,
            GetCurrentUserId(),
            User.FindFirstValue("DisplayName") ?? User.Identity?.Name ?? "system",
            GetAllowedTenantId(currentUser),
            GetAllowedDeviceId(currentUser),
            HttpContext.RequestAborted);

        TempData[result.Success ? "KvhSolutionSuccess" : "KvhSolutionError"] = result.Success
            ? result.Message
            : result.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> BatchStatus(long id)
    {
        var currentUser = await GetCurrentUserAsync();
        var batch = await kvhBulkSyncService.GetBatchAsync(id, GetAllowedTenantId(currentUser), HttpContext.RequestAborted);
        return batch is null ? NotFound() : Json(batch);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSyncBatch(long id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSolutions(currentUser))
        {
            return Forbid();
        }

        await kvhBulkSyncService.RequestCancelAsync(id, GetAllowedTenantId(currentUser), HttpContext.RequestAborted);
        TempData["KvhSolutionSuccess"] = $"Cancel requested for KVH sync batch #{id}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryFailedBatch(long id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSolutions(currentUser))
        {
            return Forbid();
        }

        var result = await kvhBulkSyncService.CreateBatchAsync(
            new KvhBatchCreateRequest { Mode = KvhBatchTypes.RetryFailed, SourceBatchId = id },
            GetCurrentUserId(),
            User.FindFirstValue("DisplayName") ?? User.Identity?.Name ?? "system",
            GetAllowedTenantId(currentUser),
            GetAllowedDeviceId(currentUser),
            HttpContext.RequestAborted);
        TempData[result.Success ? "KvhSolutionSuccess" : "KvhSolutionError"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pause(KvhSolutionCommandRequest request)
    {
        return await SubmitCommandAsync(request, (req, userId, requestedBy, tenantId, deviceId, token) =>
            kvhSubscriptionService.PauseAsync(req, userId, requestedBy, tenantId, deviceId, token), "Pause command submitted.");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resume(KvhSolutionCommandRequest request)
    {
        return await SubmitCommandAsync(request, (req, userId, requestedBy, tenantId, deviceId, token) =>
            kvhSubscriptionService.ResumeAsync(req, userId, requestedBy, tenantId, deviceId, token), "Resume command submitted.");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSchedule(KvhSolutionCommandRequest request)
    {
        return await SubmitCommandAsync(request, (req, userId, requestedBy, tenantId, deviceId, token) =>
            kvhSubscriptionService.CancelScheduleAsync(req, userId, requestedBy, tenantId, deviceId, token), "Cancel schedule command submitted.");
    }

    private async Task<IActionResult> SubmitCommandAsync(
        KvhSolutionCommandRequest request,
        Func<KvhSolutionCommandRequest, int?, string, int?, int?, CancellationToken, Task<KvhCommandSubmitResult>> submit,
        string successMessage)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSolutions(currentUser))
        {
            return Forbid();
        }

        try
        {
            var result = await submit(request, GetCurrentUserId(), User.FindFirstValue("DisplayName") ?? User.Identity?.Name ?? "system", GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            TempData[result.Success ? "KvhSolutionSuccess" : "KvhSolutionError"] = result.Success ? successMessage : result.MessageEn;
            return RedirectToAction(nameof(Details), new { deviceId = request.DeviceId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "KVH Solutions command failed for DeviceId {DeviceId}, SubscriptionId {KvhSubscriptionId}", request.DeviceId, request.KvhSubscriptionId);
            TempData["KvhSolutionError"] = ex.GetBaseException().Message;
            return RedirectToAction(nameof(Details), new { deviceId = request.DeviceId });
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

    private int? GetCurrentUserId()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(userIdValue, out var userId) ? userId : null;
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

    private static bool CanManageSolutions(AuthUserRecord? user)
    {
        return user is not null && !user.IsViewOnly;
    }
}
