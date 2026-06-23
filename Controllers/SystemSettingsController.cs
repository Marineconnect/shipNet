using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class SystemSettingsController(
    ISystemSettingsService systemSettingsService,
    ISqlAuthService authService,
    ILogger<SystemSettingsController> logger) : Controller
{
    private const string IndexViewPath = "~/Views/SystemSettings/Index.cshtml";

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        return View(IndexViewPath, new SystemSettingsIndexViewModel
        {
            Settings = await systemSettingsService.GetSettingsAsync(HttpContext.RequestAborted)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var model = await systemSettingsService.GetSettingByIdAsync(id, HttpContext.RequestAborted);
        if (model is null)
        {
            return NotFound();
        }

        return View(IndexViewPath, new SystemSettingsIndexViewModel
        {
            Settings = await systemSettingsService.GetSettingsAsync(HttpContext.RequestAborted),
            EditForm = model,
            OpenEditModal = true
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(SystemSettingsIndexViewModel requestModel)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var model = requestModel.EditForm;
        if (!ModelState.IsValid)
        {
            return View(IndexViewPath, new SystemSettingsIndexViewModel
            {
                Settings = await systemSettingsService.GetSettingsAsync(HttpContext.RequestAborted),
                EditForm = model,
                OpenEditModal = true
            });
        }

        var (userId, username) = GetCurrentAuditContext();
        try
        {
            await systemSettingsService.UpdateSettingAsync(model, userId, username, HttpContext.RequestAborted);
            TempData["SystemSettingsSuccess"] = "Cập nhật cài đặt thành công.";
            return RedirectToAction(nameof(Index));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update system setting id {SettingId}.", model.Id);
            ModelState.AddModelError(string.Empty, $"Không thể cập nhật cài đặt. Chi tiết: {exception.GetBaseException().Message}");
            return View(IndexViewPath, new SystemSettingsIndexViewModel
            {
                Settings = await systemSettingsService.GetSettingsAsync(HttpContext.RequestAborted),
                EditForm = model,
                OpenEditModal = true
            });
        }
    }

    private async Task<bool> IsSystemAdminAsync()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, out var userId))
        {
            return false;
        }

        var currentUser = await authService.GetUserByIdAsync(userId, HttpContext.RequestAborted);
        return currentUser is not null &&
            !currentUser.IsTenantUser &&
            !currentUser.IsShipAdmin &&
            !currentUser.IsCrew;
    }

    private (int? UserId, string Username) GetCurrentAuditContext()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
        var username = User.Identity?.Name;

        return (userId, string.IsNullOrWhiteSpace(username) ? "system" : username);
    }
}
