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
    IKitExportService kitExportService,
    IWebHostEnvironment environment,
    ILogger<SystemSettingsController> logger) : Controller
{
    private const string IndexViewPath = "~/Views/SystemSettings/Index.cshtml";
    private const string SlkTemplateFileName = "SLK_Template.xls";

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var currentUser = await GetCurrentUserAsync();
        if (!IsSystemAdmin(currentUser))
        {
            return Forbid();
        }

        return View(IndexViewPath, new SystemSettingsIndexViewModel
        {
            Settings = ApplySettingPermissions(await systemSettingsService.GetSettingsAsync(HttpContext.RequestAborted), currentUser)
        });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!IsSystemAdmin(currentUser))
        {
            return Forbid();
        }

        var model = await systemSettingsService.GetSettingByIdAsync(id, HttpContext.RequestAborted);
        if (model is null)
        {
            return NotFound();
        }
        if (IsKvhAutoResumeSetting(model.SettingCode) && !IsExactAdmin(currentUser))
        {
            return Forbid();
        }

        return View(IndexViewPath, new SystemSettingsIndexViewModel
        {
            Settings = ApplySettingPermissions(await systemSettingsService.GetSettingsAsync(HttpContext.RequestAborted), currentUser),
            EditForm = model,
            OpenEditModal = true
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(SystemSettingsIndexViewModel requestModel)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!IsSystemAdmin(currentUser))
        {
            return Forbid();
        }

        var model = requestModel.EditForm;
        if (!ModelState.IsValid)
        {
            return View(IndexViewPath, new SystemSettingsIndexViewModel
            {
                Settings = ApplySettingPermissions(await systemSettingsService.GetSettingsAsync(HttpContext.RequestAborted), currentUser),
                EditForm = model,
                OpenEditModal = true
            });
        }

        var existing = await systemSettingsService.GetSettingByIdAsync(model.Id, HttpContext.RequestAborted);
        if (existing is null)
        {
            return NotFound();
        }
        if (IsKvhAutoResumeSetting(existing.SettingCode) && !IsExactAdmin(currentUser))
        {
            return Forbid();
        }
        model.Category = existing.Category;
        model.SettingCode = existing.SettingCode;
        model.DisplayName = existing.DisplayName;
        model.IsSecret = existing.IsSecret;

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
                Settings = ApplySettingPermissions(await systemSettingsService.GetSettingsAsync(HttpContext.RequestAborted), currentUser),
                EditForm = model,
                OpenEditModal = true
            });
        }
    }

    [HttpGet]
    public async Task<IActionResult> DownloadSlkTemplate()
    {
        var currentUser = await GetCurrentUserAsync();
        if (!IsSystemAdmin(currentUser))
        {
            return Forbid();
        }

        var templatePath = Path.Combine(environment.ContentRootPath, "sample", SlkTemplateFileName);
        if (!System.IO.File.Exists(templatePath))
        {
            logger.LogWarning("SLK template file was not found at {TemplatePath}.", templatePath);
            return NotFound("SLK template file was not found.");
        }

        return PhysicalFile(templatePath, "application/vnd.ms-excel", SlkTemplateFileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> ImportSlkTemplate(IFormFile? importFile)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!IsSystemAdmin(currentUser))
        {
            return Forbid();
        }

        if (importFile is null || importFile.Length == 0)
        {
            return BadRequest("Please choose an SLK_Template.xls file.");
        }

        var extension = Path.GetExtension(importFile.FileName);
        if (!extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Only .xls or .xlsx files are supported.");
        }

        try
        {
            logger.LogInformation("Start importing SLK template file {FileName}. Size={FileSize}.", importFile.FileName, importFile.Length);
            var bytes = await kitExportService.ProcessSlkTemplateAsync(importFile, HttpContext.RequestAborted);
            var resultFileName = $"SLK_Template_result_{DateTime.Now:yyyyMMddHHmmss}{extension}";
            logger.LogInformation("Completed importing SLK template file {FileName}. ResultFile={ResultFile}.", importFile.FileName, resultFileName);
            return File(bytes, "application/vnd.ms-excel", resultFileName);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("SLK template import was canceled by request abort.");
            return StatusCode(StatusCodes.Status499ClientClosedRequest, "Request was canceled.");
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Invalid SLK template import file {FileName}.", importFile.FileName);
            return BadRequest(exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to import SLK template file {FileName}.", importFile.FileName);
            return StatusCode(StatusCodes.Status500InternalServerError, exception.GetBaseException().Message);
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

    private static bool IsSystemAdmin(AuthUserRecord? currentUser) =>
        currentUser is not null &&
            !currentUser.IsViewOnly &&
            !currentUser.IsTenantUser &&
            !currentUser.IsShipAdmin &&
            !currentUser.IsCrew;

    private static bool IsExactAdmin(AuthUserRecord? currentUser) =>
        string.Equals(currentUser?.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase);

    private static bool IsKvhAutoResumeSetting(string? settingCode) =>
        string.Equals(settingCode?.Trim(), SystemSettingsService.KvhAutoResumeEnabledSettingCode, StringComparison.OrdinalIgnoreCase);

    private static List<SystemSettingViewModel> ApplySettingPermissions(List<SystemSettingViewModel> settings, AuthUserRecord? currentUser)
    {
        var isExactAdmin = IsExactAdmin(currentUser);
        foreach (var setting in settings)
        {
            setting.CanEdit = !IsKvhAutoResumeSetting(setting.SettingCode) || isExactAdmin;
        }

        return settings;
    }

    private (int? UserId, string Username) GetCurrentAuditContext()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
        var username = User.Identity?.Name;

        return (userId, string.IsNullOrWhiteSpace(username) ? "system" : username);
    }
}
