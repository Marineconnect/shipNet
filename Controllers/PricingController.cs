using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class PricingController(
    IPricingPlanService pricingPlanService,
    ITenantService tenantService,
    ISqlAuthService authService,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<PricingController> logger) : Controller
{
    private const string PricingIndexViewPath = "~/Views/Pricing/Index.cshtml";
    private const string DuplicatePlanCodeErrorMessage = "Mã gói đã tồn tại.";
    private const string PricingSaveErrorMessage = "Không thể lưu gói cước. Vui lòng thử lại.";
    private const string PricingDeleteErrorMessage = "Không thể xóa gói cước. Vui lòng thử lại.";

    private readonly IDataProtector _tenantPricingProtector = dataProtectionProvider.CreateProtector("TenantPricingExport.v1");

    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, int pageSize = 10, string tab = "product", int tenantPage = 1, int tenantPageSize = 10, int? tenantId = null, string? tenantSearch = null)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        return View(PricingIndexViewPath, await BuildIndexViewModelAsync(page: page, pageSize: pageSize, activeTab: tab, tenantPage: tenantPage, tenantPageSize: tenantPageSize, tenantId: tenantId, tenantSearch: tenantSearch));
    }

    [HttpGet]
    public IActionResult Create()
    {
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> DownloadTemplate()
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var plans = await pricingPlanService.GetPlansForExportAsync(HttpContext.RequestAborted);
        var content = PricingPlanExcelTemplate.CreateTemplate(plans);
        return File(
            content,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"shipNet_pricing-plans-{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    [HttpGet]
    public async Task<IActionResult> DownloadTenantPricing(int? tenantId = null, string? tenantSearch = null)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var prices = await pricingPlanService.GetTenantPricesForExportAsync(tenantId, tenantSearch, HttpContext.RequestAborted);
        var content = PricingPlanExcelTemplate.CreateTenantPricingExport(prices, ProtectTenantId);
        return File(
            content,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"shipNet_tenant-pricing-{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Import(IFormFile? importFile, int page = 1, int pageSize = 10)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        if (importFile is null || importFile.Length == 0)
        {
            TempData["PricingError"] = "Vui lòng chọn file Excel để import.";
            return RedirectToAction(nameof(Index), new { page, pageSize });
        }

        var extension = Path.GetExtension(importFile.FileName);
        if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            TempData["PricingError"] = "File import chỉ hỗ trợ định dạng .xlsx hoặc .csv.";
            return RedirectToAction(nameof(Index), new { page, pageSize });
        }

        try
        {
            await using var stream = importFile.OpenReadStream();
            var parseResult = PricingPlanExcelTemplate.Parse(stream, importFile.FileName);
            if (parseResult.Errors.Count > 0)
            {
                TempData["PricingError"] = string.Join(" ", parseResult.Errors.Take(5));
                return RedirectToAction(nameof(Index), new { page, pageSize });
            }

            if (parseResult.Plans.Count == 0)
            {
                TempData["PricingError"] = "File import không có dòng dữ liệu hợp lệ.";
                return RedirectToAction(nameof(Index), new { page, pageSize });
            }

            var (userId, username) = GetCurrentAuditContext();
            var importResult = await pricingPlanService.ImportPlansAsync(parseResult.Plans, userId, username, HttpContext.RequestAborted);
            TempData["PricingSuccess"] = $"Import thành công. Thêm mới: {importResult.CreatedCount}, cập nhật: {importResult.UpdatedCount}.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to import pricing plans.");
            TempData["PricingError"] = $"Không thể import gói cước. Chi tiết: {exception.GetBaseException().Message}";
        }

        return RedirectToAction(nameof(Index), new { page = 1, pageSize });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreviewTenantPricingDevices(IFormFile? importFile)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        if (importFile is null || importFile.Length == 0)
        {
            return BadRequest(new { success = false, message = "Vui lòng chọn file Excel để xem trước thiết bị." });
        }

        var extension = Path.GetExtension(importFile.FileName);
        if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "File import chỉ hỗ trợ định dạng .xlsx hoặc .csv." });
        }

        try
        {
            await using var stream = importFile.OpenReadStream();
            var parseResult = PricingPlanExcelTemplate.ParseTenantPricing(stream, importFile.FileName, UnprotectTenantId);
            if (parseResult.Errors.Count > 0)
            {
                return BadRequest(new { success = false, message = string.Join(" ", parseResult.Errors.Take(5)) });
            }

            if (parseResult.Prices.Count == 0)
            {
                return BadRequest(new { success = false, message = "File import không có dòng dữ liệu hợp lệ." });
            }

            var preview = await pricingPlanService.GetTenantPricingDevicePreviewAsync(parseResult.Prices, HttpContext.RequestAborted);
            return Json(new { success = true, tenants = preview.Tenants, errors = preview.Errors });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to preview tenant pricing import devices.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { success = false, message = $"Không thể xem trước thiết bị. Chi tiết: {exception.GetBaseException().Message}" });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportTenantPricing(IFormFile? importFile, int tenantPage = 1, int tenantPageSize = 10, int? tenantId = null, string? tenantSearch = null, List<int>? selectedDeviceIds = null)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        if (importFile is null || importFile.Length == 0)
        {
            TempData["PricingError"] = "Vui lòng chọn file Excel để import.";
            return RedirectToAction(nameof(Index), new { tab = "tenant", tenantPage, tenantPageSize, tenantId, tenantSearch });
        }

        var extension = Path.GetExtension(importFile.FileName);
        if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            TempData["PricingError"] = "File import chỉ hỗ trợ định dạng .xlsx hoặc .csv.";
            return RedirectToAction(nameof(Index), new { tab = "tenant", tenantPage, tenantPageSize, tenantId, tenantSearch });
        }

        try
        {
            await using var stream = importFile.OpenReadStream();
            var parseResult = PricingPlanExcelTemplate.ParseTenantPricing(stream, importFile.FileName, UnprotectTenantId);
            if (parseResult.Errors.Count > 0)
            {
                TempData["PricingError"] = string.Join(" ", parseResult.Errors.Take(5));
                return RedirectToAction(nameof(Index), new { tab = "tenant", tenantPage, tenantPageSize, tenantId, tenantSearch });
            }

            if (parseResult.Prices.Count == 0)
            {
                TempData["PricingError"] = "File import không có dòng dữ liệu hợp lệ.";
                return RedirectToAction(nameof(Index), new { tab = "tenant", tenantPage, tenantPageSize, tenantId, tenantSearch });
            }

            var (userId, username) = GetCurrentAuditContext();
            var importResult = await pricingPlanService.ImportTenantPricesAsync(parseResult.Prices, userId, username, selectedDeviceIds, HttpContext.RequestAborted);
            if (importResult.Errors.Count > 0)
            {
                TempData["PricingError"] = string.Join(" ", importResult.Errors.Take(5));
            }
            else
            {
                var deviceSummary = (importResult.DeviceCreatedCount + importResult.DeviceUpdatedCount + importResult.DeviceSkippedCount) > 0
                    ? $" Thiết bị - thêm mới: {importResult.DeviceCreatedCount}, cập nhật: {importResult.DeviceUpdatedCount}, bỏ qua: {importResult.DeviceSkippedCount}."
                    : string.Empty;
                TempData["PricingSuccess"] = $"Import giá tenant thành công. Thêm mới: {importResult.CreatedCount}, cập nhật: {importResult.UpdatedCount}.{deviceSummary}";
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to import tenant pricing.");
            TempData["PricingError"] = $"Không thể import giá tenant. Chi tiết: {exception.GetBaseException().Message}";
        }

        return RedirectToAction(nameof(Index), new { tab = "tenant", tenantPage = 1, tenantPageSize, tenantId, tenantSearch });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PricingPlanIndexViewModel requestModel)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var model = requestModel.CreateForm;
        NormalizePlanModel(model);
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.EditForm));
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.TenantPriceCreateForm));
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.TenantPriceEditForm));
        await ValidateDuplicatePlanCodeAsync(model, nameof(PricingPlanIndexViewModel.CreateForm), excludePlanId: null);
        ValidateStatus(model, nameof(PricingPlanIndexViewModel.CreateForm));

        if (!ModelState.IsValid)
        {
            return View(
                PricingIndexViewPath,
                await BuildIndexViewModelAsync(
                    createForm: model,
                    openCreateModal: true,
                    page: model.CurrentPage,
                    pageSize: model.PageSize));
        }

        var (userId, username) = GetCurrentAuditContext();

        try
        {
            await pricingPlanService.CreatePlanAsync(model, userId, username, HttpContext.RequestAborted);
            TempData["PricingSuccess"] = "Thêm gói cước thành công.";
            return RedirectToAction(nameof(Index), new { page = 1, pageSize = model.PageSize });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create pricing plan.");
            ModelState.AddModelError(string.Empty, BuildPricingSaveErrorMessage(exception));
            return View(
                PricingIndexViewPath,
                await BuildIndexViewModelAsync(
                    createForm: model,
                    openCreateModal: true,
                    page: model.CurrentPage,
                    pageSize: model.PageSize));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, int page = 1, int pageSize = 10)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var plan = await pricingPlanService.GetPlanByIdAsync(id, HttpContext.RequestAborted);
        if (plan is null)
        {
            return NotFound();
        }

        plan.CurrentPage = page;
        plan.PageSize = pageSize;
        return View(
            PricingIndexViewPath,
            await BuildIndexViewModelAsync(
                editForm: plan,
                openEditModal: true,
                page: page,
                pageSize: pageSize));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(PricingPlanIndexViewModel requestModel)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var model = requestModel.EditForm;
        NormalizePlanModel(model);
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.CreateForm));
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.TenantPriceCreateForm));
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.TenantPriceEditForm));
        await ValidateDuplicatePlanCodeAsync(model, nameof(PricingPlanIndexViewModel.EditForm), model.Id);
        ValidateStatus(model, nameof(PricingPlanIndexViewModel.EditForm));

        if (!ModelState.IsValid)
        {
            return View(
                PricingIndexViewPath,
                await BuildIndexViewModelAsync(
                    editForm: model,
                    openEditModal: true,
                    page: model.CurrentPage,
                    pageSize: model.PageSize));
        }

        var (userId, username) = GetCurrentAuditContext();

        try
        {
            await pricingPlanService.UpdatePlanAsync(model, userId, username, HttpContext.RequestAborted);
            TempData["PricingSuccess"] = "Cập nhật gói cước thành công.";
            return RedirectToAction(nameof(Index), new { page = model.CurrentPage, pageSize = model.PageSize });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update pricing plan id {PlanId}.", model.Id);
            ModelState.AddModelError(string.Empty, BuildPricingSaveErrorMessage(exception));
            return View(
                PricingIndexViewPath,
                await BuildIndexViewModelAsync(
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
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var (userId, username) = GetCurrentAuditContext();

        try
        {
            await pricingPlanService.DeletePlanAsync(id, userId, username, HttpContext.RequestAborted);
            TempData["PricingSuccess"] = "Xóa gói cước thành công.";
        }
        catch (KeyNotFoundException)
        {
            TempData["PricingError"] = "Gói cước không tồn tại hoặc đã bị xóa.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete pricing plan id {PlanId}.", id);
            TempData["PricingError"] = PricingDeleteErrorMessage;
        }

        return RedirectToAction(nameof(Index), new { page, pageSize });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTenantPricing(PricingPlanIndexViewModel requestModel)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var model = requestModel.TenantPriceCreateForm;
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.CreateForm));
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.EditForm));
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.TenantPriceEditForm));
        await ValidateTenantPricingDuplicateAsync(model, nameof(PricingPlanIndexViewModel.TenantPriceCreateForm), null);

        if (!ModelState.IsValid)
        {
            return View(
                PricingIndexViewPath,
                await BuildIndexViewModelAsync(
                    tenantPriceCreateForm: model,
                    openTenantPriceCreateModal: true,
                    activeTab: "tenant",
                    tenantPage: model.CurrentPage,
                    tenantPageSize: model.PageSize));
        }

        var (userId, username) = GetCurrentAuditContext();
        try
        {
            await pricingPlanService.CreateTenantPriceAsync(model, userId, username, HttpContext.RequestAborted);
            TempData["PricingSuccess"] = "Thêm giá tenant thành công.";
            return RedirectToAction(nameof(Index), new { tab = "tenant", tenantPage = 1, tenantPageSize = model.PageSize });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create tenant pricing.");
            ModelState.AddModelError(string.Empty, BuildPricingSaveErrorMessage(exception));
            return View(
                PricingIndexViewPath,
                await BuildIndexViewModelAsync(
                    tenantPriceCreateForm: model,
                    openTenantPriceCreateModal: true,
                    activeTab: "tenant",
                    tenantPage: model.CurrentPage,
                    tenantPageSize: model.PageSize));
        }
    }

    [HttpGet]
    public async Task<IActionResult> EditTenantPricing(int id, int tenantPage = 1, int tenantPageSize = 10, int? tenantId = null, string? tenantSearch = null)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var price = await pricingPlanService.GetTenantPriceByIdAsync(id, HttpContext.RequestAborted);
        if (price is null)
        {
            return NotFound();
        }

        price.CurrentPage = tenantPage;
        price.PageSize = tenantPageSize;
        return View(
            PricingIndexViewPath,
            await BuildIndexViewModelAsync(
                tenantPriceEditForm: price,
                openTenantPriceEditModal: true,
                activeTab: "tenant",
                tenantPage: tenantPage,
                tenantPageSize: tenantPageSize,
                tenantId: tenantId,
                tenantSearch: tenantSearch));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditTenantPricing(PricingPlanIndexViewModel requestModel)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var model = requestModel.TenantPriceEditForm;
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.CreateForm));
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.EditForm));
        RemoveModelStateForPrefix(nameof(PricingPlanIndexViewModel.TenantPriceCreateForm));
        await ValidateTenantPricingDuplicateAsync(model, nameof(PricingPlanIndexViewModel.TenantPriceEditForm), model.Id);

        if (!ModelState.IsValid)
        {
            return View(
                PricingIndexViewPath,
                await BuildIndexViewModelAsync(
                    tenantPriceEditForm: model,
                    openTenantPriceEditModal: true,
                    activeTab: "tenant",
                    tenantPage: model.CurrentPage,
                    tenantPageSize: model.PageSize));
        }

        var (userId, username) = GetCurrentAuditContext();
        try
        {
            await pricingPlanService.UpdateTenantPriceAsync(model, userId, username, HttpContext.RequestAborted);
            TempData["PricingSuccess"] = "Cập nhật giá tenant thành công.";
            return RedirectToAction(nameof(Index), new { tab = "tenant", tenantPage = model.CurrentPage, tenantPageSize = model.PageSize });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update tenant pricing id {TenantPricingId}.", model.Id);
            ModelState.AddModelError(string.Empty, BuildPricingSaveErrorMessage(exception));
            return View(
                PricingIndexViewPath,
                await BuildIndexViewModelAsync(
                    tenantPriceEditForm: model,
                    openTenantPriceEditModal: true,
                    activeTab: "tenant",
                    tenantPage: model.CurrentPage,
                    tenantPageSize: model.PageSize));
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTenantPricing(int id, int tenantPage = 1, int tenantPageSize = 10, int? tenantId = null, string? tenantSearch = null)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var (userId, username) = GetCurrentAuditContext();
        try
        {
            await pricingPlanService.DeleteTenantPriceAsync(id, userId, username, HttpContext.RequestAborted);
            TempData["PricingSuccess"] = "Xóa giá tenant thành công.";
        }
        catch (KeyNotFoundException)
        {
            TempData["PricingError"] = "Giá tenant không tồn tại hoặc đã bị xóa.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete tenant pricing id {TenantPricingId}.", id);
            TempData["PricingError"] = PricingDeleteErrorMessage;
        }

        return RedirectToAction(nameof(Index), new { tab = "tenant", tenantPage, tenantPageSize, tenantId, tenantSearch });
    }

    private async Task<PricingPlanIndexViewModel> BuildIndexViewModelAsync(
        PricingPlanFormViewModel? createForm = null,
        bool openCreateModal = false,
        PricingPlanFormViewModel? editForm = null,
        bool openEditModal = false,
        TenantPricingFormViewModel? tenantPriceCreateForm = null,
        bool openTenantPriceCreateModal = false,
        TenantPricingFormViewModel? tenantPriceEditForm = null,
        bool openTenantPriceEditModal = false,
        string activeTab = "product",
        int page = 1,
        int pageSize = 10,
        int tenantPage = 1,
        int tenantPageSize = 10,
        int? tenantId = null,
        string? tenantSearch = null)
    {
        var normalizedTenantId = tenantId.GetValueOrDefault() > 0 ? tenantId : null;
        var normalizedTenantSearch = (tenantSearch ?? string.Empty).Trim();
        var planPage = await pricingPlanService.GetPlansAsync(page, pageSize, HttpContext.RequestAborted);
        var tenantPricePage = await pricingPlanService.GetTenantPricesAsync(tenantPage, tenantPageSize, normalizedTenantId, normalizedTenantSearch, HttpContext.RequestAborted);
        var tenantOptions = await tenantService.GetTenantOptionsAsync(cancellationToken: HttpContext.RequestAborted);
        var planOptions = await pricingPlanService.GetPlanOptionsAsync(HttpContext.RequestAborted);
        var resolvedPage = planPage.CurrentPage;
        var resolvedPageSize = planPage.PageSize;
        var resolvedTenantPage = tenantPricePage.CurrentPage;
        var resolvedTenantPageSize = tenantPricePage.PageSize;

        createForm ??= new PricingPlanFormViewModel();
        createForm.CurrentPage = resolvedPage;
        createForm.PageSize = resolvedPageSize;

        editForm ??= new PricingPlanFormViewModel();
        editForm.CurrentPage = resolvedPage;
        editForm.PageSize = resolvedPageSize;

        tenantPriceCreateForm ??= new TenantPricingFormViewModel();
        tenantPriceCreateForm.CurrentPage = resolvedTenantPage;
        tenantPriceCreateForm.PageSize = resolvedTenantPageSize;

        tenantPriceEditForm ??= new TenantPricingFormViewModel();
        tenantPriceEditForm.CurrentPage = resolvedTenantPage;
        tenantPriceEditForm.PageSize = resolvedTenantPageSize;

        return new PricingPlanIndexViewModel
        {
            Plans = planPage.Plans,
            TenantPrices = tenantPricePage.Prices,
            TenantOptions = tenantOptions,
            PricingPlanOptions = planOptions,
            CreateForm = createForm,
            EditForm = editForm,
            TenantPriceCreateForm = tenantPriceCreateForm,
            TenantPriceEditForm = tenantPriceEditForm,
            OpenCreateModal = openCreateModal,
            OpenEditModal = openEditModal,
            OpenTenantPriceCreateModal = openTenantPriceCreateModal,
            OpenTenantPriceEditModal = openTenantPriceEditModal,
            ActiveTab = string.Equals(activeTab, "tenant", StringComparison.OrdinalIgnoreCase) ? "tenant" : "product",
            CurrentPage = resolvedPage,
            PageSize = resolvedPageSize,
            TotalPlans = planPage.TotalPlans,
            TenantPricingCurrentPage = resolvedTenantPage,
            TenantPricingPageSize = resolvedTenantPageSize,
            TotalTenantPrices = tenantPricePage.TotalPrices,
            TenantPricingTenantId = normalizedTenantId,
            TenantPricingSearch = normalizedTenantSearch
        };
    }

    private async Task ValidateDuplicatePlanCodeAsync(PricingPlanFormViewModel model, string prefix, int? excludePlanId)
    {
        if (await pricingPlanService.IsPlanCodeInUseAsync(model.PlanCode, excludePlanId, HttpContext.RequestAborted))
        {
            ModelState.AddModelError($"{prefix}.{nameof(PricingPlanFormViewModel.PlanCode)}", DuplicatePlanCodeErrorMessage);
        }
    }

    private async Task ValidateTenantPricingDuplicateAsync(TenantPricingFormViewModel model, string prefix, int? excludeTenantPriceId)
    {
        if (model.TenantId <= 0 || model.PricingPlanId <= 0)
        {
            return;
        }

        if (await pricingPlanService.IsTenantPlanPriceInUseAsync(model.TenantId, model.PricingPlanId, excludeTenantPriceId, HttpContext.RequestAborted))
        {
            ModelState.AddModelError($"{prefix}.{nameof(TenantPricingFormViewModel.PricingPlanId)}", "Tenant này đã có giá cho gói được chọn.");
        }
    }

    private void ValidateStatus(PricingPlanFormViewModel model, string prefix)
    {
        if (!string.Equals(model.Status, "active", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(model.Status, "inactive", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError($"{prefix}.{nameof(PricingPlanFormViewModel.Status)}", "Trạng thái không hợp lệ.");
        }
    }

    private async Task<bool> IsSystemAdminAsync()
    {
        var currentUser = await GetCurrentUserAsync();
        return currentUser is not null &&
            !currentUser.IsViewOnly &&
            !currentUser.IsTenantUser &&
            !currentUser.IsShipAdmin &&
            !currentUser.IsCrew;
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

    private string ProtectTenantId(int tenantId)
    {
        return _tenantPricingProtector.Protect(tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private int? UnprotectTenantId(string protectedTenantId)
    {
        if (string.IsNullOrWhiteSpace(protectedTenantId))
        {
            return null;
        }

        try
        {
            var value = _tenantPricingProtector.Unprotect(protectedTenantId.Trim());
            return int.TryParse(value, out var tenantId) ? tenantId : null;
        }
        catch
        {
            return null;
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

    private static void NormalizePlanModel(PricingPlanFormViewModel model)
    {
        model.PlanName = (model.PlanName ?? string.Empty).Trim();
        model.PlanCode = (model.PlanCode ?? string.Empty).Trim();
        model.Status = string.IsNullOrWhiteSpace(model.Status)
            ? "active"
            : model.Status.Trim().ToLowerInvariant();
    }

    private static string BuildPricingSaveErrorMessage(Exception exception)
    {
        var rootException = exception.GetBaseException();

        if (rootException is SqlException sqlException)
        {
            if (sqlException.Number is 2627 or 2601)
            {
                return DuplicatePlanCodeErrorMessage;
            }

            if (sqlException.Number == 8152 || sqlException.Message.Contains("truncated", StringComparison.OrdinalIgnoreCase))
            {
                return "Không thể lưu gói cước vì một hoặc nhiều trường vượt quá độ dài cho phép.";
            }

            if (sqlException.Number == -2)
            {
                return "Không thể lưu gói cước vì thao tác cơ sở dữ liệu bị quá thời gian chờ.";
            }

            if (sqlException.Number is 53 or 4060 or 10060 or 233)
            {
                return "Không thể lưu gói cước vì không kết nối được tới cơ sở dữ liệu.";
            }

            return $"Không thể lưu gói cước do lỗi cơ sở dữ liệu: {sqlException.Message}";
        }

        return $"{PricingSaveErrorMessage} Chi tiết: {rootException.Message}";
    }
}
