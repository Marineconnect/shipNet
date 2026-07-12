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

    [HttpGet]
    public async Task<IActionResult> DownloadSlkTemplate()
    {
        if (!await IsSystemAdminAsync())
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
        if (!await IsSystemAdminAsync())
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

    private async Task<bool> IsSystemAdminAsync()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdValue, out var userId))
        {
            return false;
        }

        var currentUser = await authService.GetUserByIdAsync(userId, HttpContext.RequestAborted);
        return currentUser is not null &&
            !currentUser.IsViewOnly &&
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
