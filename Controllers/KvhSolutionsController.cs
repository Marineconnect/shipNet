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
    public async Task<IActionResult> Index(
        string tab = "devices",
        int page = 1,
        int pageSize = 20,
        string? search = null,
        string? status = null,
        string? region = null,
        string? planName = null,
        int? tenantId = null,
        string? syncState = null,
        DateTime? syncDateFrom = null,
        DateTime? syncDateTo = null,
        int historyPage = 1,
        int historyPageSize = 20,
        string? historySearch = null,
        int? historyTenantId = null,
        string? historyResult = null,
        string? historySource = null,
        DateTime? historyDateFrom = null,
        DateTime? historyDateTo = null)
    {
        tab = NormalizeTab(tab);
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessSolutions(currentUser))
        {
            return Forbid();
        }

        var model = await kvhSubscriptionService.GetSolutionsAsync(
            new KvhSolutionFilter
            {
                Search = search,
                Status = status,
                Region = region,
                PlanName = planName,
                TenantId = tenantId,
                SyncState = syncState,
                SyncDateFrom = syncDateFrom,
                SyncDateTo = syncDateTo
            },
            page,
            pageSize,
            GetAllowedTenantId(currentUser),
            GetAllowedDeviceId(currentUser),
            CanManageSolutions(currentUser),
            HttpContext.RequestAborted);
        model.ActiveTab = tab;
        model.SyncHistory = tab == "history"
            ? await kvhSubscriptionService.GetSyncHistoryAsync(
                new KvhSyncHistoryFilter
                {
                    Search = historySearch,
                    TenantId = historyTenantId,
                    Result = historyResult,
                    SyncSource = historySource,
                    DateFrom = historyDateFrom,
                    DateTo = historyDateTo
                },
                historyPage,
                historyPageSize,
                GetAllowedTenantId(currentUser),
                GetAllowedDeviceId(currentUser),
                HttpContext.RequestAborted)
            : new KvhSyncHistoryPageResult
            {
                Filter = new KvhSyncHistoryFilter
                {
                    Search = historySearch,
                    TenantId = historyTenantId,
                    Result = historyResult,
                    SyncSource = historySource,
                    DateFrom = historyDateFrom,
                    DateTo = historyDateTo
                },
                CurrentPage = historyPage < 1 ? 1 : historyPage,
                PageSize = historyPageSize is 20 or 50 or 100 ? historyPageSize : 20
            };
        model.RecentBatches = tab is "devices" or "batches"
            ? (await kvhBulkSyncService.GetRecentBatchesAsync(GetAllowedTenantId(currentUser), HttpContext.RequestAborted)).ToList()
            : [];
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Details(int deviceId)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanAccessSolutions(currentUser))
        {
            return Forbid();
        }

        var detail = await kvhSubscriptionService.GetSolutionDetailAsync(deviceId, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), CanManageSolutions(currentUser), HttpContext.RequestAborted);
        return detail is null ? NotFound() : View(detail);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(int deviceId)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSolutions(currentUser))
        {
            return Forbid();
        }

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
        if (!CanAccessSolutions(currentUser))
        {
            return Forbid();
        }

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
        return CanAccessSolutions(user) && user?.IsViewOnly != true;
    }

    private static bool CanAccessSolutions(AuthUserRecord? user)
    {
        return string.Equals(user?.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTab(string? tab) =>
        string.Equals(tab, "history", StringComparison.OrdinalIgnoreCase) ? "history" :
        string.Equals(tab, "batches", StringComparison.OrdinalIgnoreCase) ? "batches" :
        "devices";
}
