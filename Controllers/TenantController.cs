using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class TenantController(
    ITenantService tenantService,
    ISqlAuthService authService,
    IWebHostEnvironment environment,
    ILogger<TenantController> logger) : Controller
{
    private const string TenantIndexViewPath = "~/Views/Tenant/Index.cshtml";
    private const string TenantFormViewPath = "~/Views/Tenant/Form.cshtml";
    private const long MaxLogoSizeBytes = 3 * 1024 * 1024;
    private const string TenantSaveErrorMessage = "Không thể lưu tenant. Vui lòng thử lại.";
    private const string TenantDeleteErrorMessage = "Không thể xóa tenant. Vui lòng thử lại.";
    private static readonly HashSet<string> AllowedLogoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif"
    };

    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, int pageSize = 10)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser?.IsShipAdmin == true || currentUser?.IsCrew == true)
        {
            return Forbid();
        }

        return View(TenantIndexViewPath, await BuildIndexViewModelAsync(currentUser, page: page, pageSize: pageSize));
    }

    [HttpGet]
    public IActionResult Create()
    {
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TenantIndexViewModel requestModel)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser?.IsShipAdmin == true || currentUser?.IsCrew == true)
        {
            return Forbid();
        }

        if (currentUser?.IsTenantUser == true)
        {
            TempData["TenantError"] = "Tài khoản tenant không có quyền thêm tenant.";
            return RedirectToAction(nameof(Index), new { page = requestModel.CreateForm.CurrentPage, pageSize = requestModel.CreateForm.PageSize });
        }

        var model = requestModel.CreateForm;
        NormalizeTenantModel(model);
        RemoveModelStateForPrefix(nameof(TenantIndexViewModel.EditForm));
        ValidateLogoFile(
            model.LogoFile,
            $"{nameof(TenantIndexViewModel.CreateForm)}.{nameof(TenantFormViewModel.LogoFile)}");

        if (!ModelState.IsValid)
        {
            return View(
                TenantIndexViewPath,
                await BuildIndexViewModelAsync(
                    currentUser,
                    createForm: model,
                    openCreateModal: true,
                    page: model.CurrentPage,
                    pageSize: model.PageSize));
        }

        var (userId, username) = GetCurrentAuditContext();

        try
        {
            if (model.LogoFile is not null)
            {
                model.ExistingLogoPath = await SaveLogoAsync(model.LogoFile, HttpContext.RequestAborted);
            }

            await tenantService.CreateTenantAsync(model, userId, username, HttpContext.RequestAborted);
            TempData["TenantSuccess"] = "Thêm tenant thành công.";
            return RedirectToAction(nameof(Index), new { page = 1, pageSize = model.PageSize });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create tenant.");
            ModelState.AddModelError(string.Empty, BuildTenantSaveErrorMessage(exception));
            return View(
                TenantIndexViewPath,
                await BuildIndexViewModelAsync(
                    currentUser,
                    createForm: model,
                    openCreateModal: true,
                    page: model.CurrentPage,
                    pageSize: model.PageSize));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, int page = 1, int pageSize = 10)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser?.IsShipAdmin == true || currentUser?.IsCrew == true)
        {
            return Forbid();
        }

        if (currentUser?.IsTenantUser == true && currentUser.TenantId != id)
        {
            return NotFound();
        }

        var tenant = await tenantService.GetTenantByIdAsync(id, HttpContext.RequestAborted);
        if (tenant is null)
        {
            return NotFound();
        }

        tenant.CurrentPage = page;
        tenant.PageSize = pageSize;
        return View(
            TenantIndexViewPath,
            await BuildIndexViewModelAsync(
                currentUser,
                editForm: tenant,
                openEditModal: true,
                page: page,
                pageSize: pageSize));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(TenantIndexViewModel requestModel)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser?.IsShipAdmin == true || currentUser?.IsCrew == true)
        {
            return Forbid();
        }

        var model = requestModel.EditForm;
        if (currentUser?.IsTenantUser == true && currentUser.TenantId != model.Id)
        {
            return NotFound();
        }

        NormalizeTenantModel(model);
        RemoveModelStateForPrefix(nameof(TenantIndexViewModel.CreateForm));
        ValidateLogoFile(
            model.LogoFile,
            $"{nameof(TenantIndexViewModel.EditForm)}.{nameof(TenantFormViewModel.LogoFile)}");

        if (!ModelState.IsValid)
        {
            return View(
                TenantIndexViewPath,
                await BuildIndexViewModelAsync(
                    currentUser,
                    editForm: model,
                    openEditModal: true,
                    page: model.CurrentPage,
                    pageSize: model.PageSize));
        }

        var (userId, username) = GetCurrentAuditContext();

        try
        {
            var existingTenant = await tenantService.GetTenantByIdAsync(model.Id, HttpContext.RequestAborted);
            if (existingTenant is null)
            {
                return NotFound();
            }

            model.ExistingLogoPath = existingTenant.ExistingLogoPath;
            if (model.LogoFile is not null)
            {
                model.ExistingLogoPath = await SaveLogoAsync(model.LogoFile, HttpContext.RequestAborted);
            }

            await tenantService.UpdateTenantAsync(model, userId, username, HttpContext.RequestAborted);
            TempData["TenantSuccess"] = "Cập nhật tenant thành công.";
            return RedirectToAction(nameof(Index), new { page = model.CurrentPage, pageSize = model.PageSize });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update tenant id {TenantId}.", model.Id);
            ModelState.AddModelError(string.Empty, BuildTenantSaveErrorMessage(exception));
            return View(
                TenantIndexViewPath,
                await BuildIndexViewModelAsync(
                    currentUser,
                    editForm: model,
                    openEditModal: true,
                    page: model.CurrentPage,
                    pageSize: model.PageSize));
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, int page = 1, int pageSize = 10)
    {
        var currentUser = await GetCurrentUserAsync();
        if (currentUser?.IsShipAdmin == true || currentUser?.IsCrew == true)
        {
            return Forbid();
        }

        if (currentUser?.IsTenantUser == true)
        {
            TempData["TenantError"] = "Tài khoản tenant không có quyền xóa tenant.";
            return RedirectToAction(nameof(Index), new { page, pageSize });
        }

        var (userId, username) = GetCurrentAuditContext();

        try
        {
            await tenantService.DeleteTenantAsync(id, userId, username, HttpContext.RequestAborted);
            TempData["TenantSuccess"] = "Xóa tenant thành công.";
        }
        catch (KeyNotFoundException)
        {
            TempData["TenantError"] = "Tenant không tồn tại hoặc đã bị xóa.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete tenant id {TenantId}.", id);
            TempData["TenantError"] = TenantDeleteErrorMessage;
        }

        return RedirectToAction(nameof(Index), new { page, pageSize });
    }

    private async Task<TenantIndexViewModel> BuildIndexViewModelAsync(
        AuthUserRecord? currentUser,
        TenantFormViewModel? createForm = null,
        bool openCreateModal = false,
        TenantFormViewModel? editForm = null,
        bool openEditModal = false,
        int page = 1,
        int pageSize = 10)
    {
        var tenantPage = await tenantService.GetTenantsAsync(page, pageSize, currentUser?.IsTenantUser == true ? currentUser.TenantId ?? -1 : null, HttpContext.RequestAborted);
        var resolvedPage = tenantPage.CurrentPage;
        var resolvedPageSize = tenantPage.PageSize;

        createForm ??= new TenantFormViewModel();
        createForm.CurrentPage = resolvedPage;
        createForm.PageSize = resolvedPageSize;

        editForm ??= new TenantFormViewModel();
        editForm.CurrentPage = resolvedPage;
        editForm.PageSize = resolvedPageSize;

        return new TenantIndexViewModel
        {
            Tenants = tenantPage.Tenants,
            CreateForm = createForm,
            EditForm = editForm,
            OpenCreateModal = openCreateModal,
            OpenEditModal = openEditModal,
            CurrentPage = resolvedPage,
            PageSize = resolvedPageSize,
            TotalTenants = tenantPage.TotalTenants,
            IsTenantScoped = currentUser?.IsTenantUser == true
        };
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

    private (int? UserId, string Username) GetCurrentAuditContext()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
        var username = User.Identity?.Name;

        return (userId, string.IsNullOrWhiteSpace(username) ? "system" : username);
    }

    private void ValidateLogoFile(IFormFile? logoFile, string modelKey)
    {
        if (logoFile is null || logoFile.Length == 0)
        {
            return;
        }

        var extension = Path.GetExtension(logoFile.FileName);
        if (!AllowedLogoExtensions.Contains(extension))
        {
            ModelState.AddModelError(modelKey, "Logo chỉ hỗ trợ các file JPG, PNG, WEBP hoặc GIF.");
        }

        if (logoFile.Length > MaxLogoSizeBytes)
        {
            ModelState.AddModelError(modelKey, "Logo tối đa 3MB.");
        }
    }

    private void RemoveModelStateForPrefix(string prefix)
    {
        var keys = ModelState.Keys
            .Where(key => key.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith($"{prefix}.", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in keys)
        {
            ModelState.Remove(key);
        }
    }

    private async Task<string> SaveLogoAsync(IFormFile logoFile, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(logoFile.FileName).ToLowerInvariant();
        var uploadsRoot = Path.Combine(environment.WebRootPath, "uploads", "tenant-logos");
        Directory.CreateDirectory(uploadsRoot);

        var fileName = $"tenant-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}";
        var filePath = Path.Combine(uploadsRoot, fileName);

        await using var stream = System.IO.File.Create(filePath);
        await logoFile.CopyToAsync(stream, cancellationToken);

        return $"/uploads/tenant-logos/{fileName}";
    }

    private static void NormalizeTenantModel(TenantFormViewModel model)
    {
        model.TenantName = (model.TenantName ?? string.Empty).Trim();
        model.Email = NormalizeOptionalValue(model.Email);
        model.Phone = NormalizeOptionalValue(model.Phone);
        model.Description = NormalizeOptionalValue(model.Description);
        model.Address = NormalizeOptionalValue(model.Address);
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string BuildTenantSaveErrorMessage(Exception exception)
    {
        var rootException = exception.GetBaseException();

        if (rootException is IOException)
        {
            return "Không thể lưu tenant vì upload logo thất bại. Vui lòng thử lại với file khác.";
        }

        if (rootException is SqlException sqlException)
        {
            if (sqlException.Number is 2627 or 2601)
            {
                return "Không thể lưu tenant vì dữ liệu bị trùng với bản ghi đã có trong cơ sở dữ liệu.";
            }

            if (sqlException.Number == 8152 || sqlException.Message.Contains("truncated", StringComparison.OrdinalIgnoreCase))
            {
                return "Không thể lưu tenant vì một hoặc nhiều trường vượt quá độ dài cho phép của cơ sở dữ liệu.";
            }

            if (sqlException.Number == -2)
            {
                return "Không thể lưu tenant vì thao tác với cơ sở dữ liệu bị quá thời gian chờ.";
            }

            if (sqlException.Number is 53 or 4060 or 10060 or 233)
            {
                return "Không thể lưu tenant vì không kết nối được tới cơ sở dữ liệu.";
            }

            return $"Không thể lưu tenant do lỗi cơ sở dữ liệu: {sqlException.Message}";
        }

        if (rootException is InvalidOperationException invalidOperationException)
        {
            return $"Không thể lưu tenant: {invalidOperationException.Message}";
        }

        return $"{TenantSaveErrorMessage} Chi tiết: {rootException.Message}";
    }
}
