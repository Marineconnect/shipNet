using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class CurrencyExchangeController(
    ICurrencyExchangeService currencyExchangeService,
    ISqlAuthService authService,
    ILogger<CurrencyExchangeController> logger) : Controller
{
    private const string IndexViewPath = "~/Views/CurrencyExchange/Index.cshtml";
    private const string DuplicateRateMessage = "Mốc tỷ giá cho cặp tiền và ngày hiệu lực này đã tồn tại.";

    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, int pageSize = 10)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        return View(IndexViewPath, await BuildIndexViewModelAsync(page: page, pageSize: pageSize));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Convert(CurrencyExchangeIndexViewModel requestModel, int page = 1, int pageSize = 10)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var form = requestModel.ConversionForm;
        NormalizeConversionForm(form);
        RemoveModelStateForPrefix(nameof(CurrencyExchangeIndexViewModel.CreateForm));
        RemoveModelStateForPrefix(nameof(CurrencyExchangeIndexViewModel.EditForm));

        CurrencyConversionResultViewModel? result = null;
        if (ModelState.IsValid)
        {
            result = await currencyExchangeService.ConvertAsync(form, HttpContext.RequestAborted);
            if (result is null)
            {
                ModelState.AddModelError($"{nameof(CurrencyExchangeIndexViewModel.ConversionForm)}.{nameof(CurrencyConversionFormViewModel.ToCurrency)}", "Chưa có tỷ giá active phù hợp với ngày quy đổi.");
            }
        }

        return View(IndexViewPath, await BuildIndexViewModelAsync(conversionForm: form, conversionResult: result, page: page, pageSize: pageSize));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CurrencyExchangeIndexViewModel requestModel)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var model = requestModel.CreateForm;
        NormalizeRateForm(model);
        RemoveModelStateForPrefix(nameof(CurrencyExchangeIndexViewModel.EditForm));
        RemoveModelStateForPrefix(nameof(CurrencyExchangeIndexViewModel.ConversionForm));
        await ValidateDuplicateAsync(model, nameof(CurrencyExchangeIndexViewModel.CreateForm), null);
        ValidateRate(model, nameof(CurrencyExchangeIndexViewModel.CreateForm));

        if (!ModelState.IsValid)
        {
            return View(IndexViewPath, await BuildIndexViewModelAsync(createForm: model, openCreateModal: true, page: model.CurrentPage, pageSize: model.PageSize));
        }

        var (userId, username) = GetCurrentAuditContext();
        try
        {
            await currencyExchangeService.CreateRateAsync(model, userId, username, HttpContext.RequestAborted);
            TempData["CurrencyExchangeSuccess"] = "Thêm mốc tỷ giá thành công.";
            return RedirectToAction(nameof(Index), new { page = 1, pageSize = model.PageSize });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create currency exchange rate.");
            ModelState.AddModelError(string.Empty, BuildSaveErrorMessage(exception));
            return View(IndexViewPath, await BuildIndexViewModelAsync(createForm: model, openCreateModal: true, page: model.CurrentPage, pageSize: model.PageSize));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, int page = 1, int pageSize = 10)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var model = await currencyExchangeService.GetRateByIdAsync(id, HttpContext.RequestAborted);
        if (model is null)
        {
            return NotFound();
        }

        model.CurrentPage = page;
        model.PageSize = pageSize;
        return View(IndexViewPath, await BuildIndexViewModelAsync(editForm: model, openEditModal: true, page: page, pageSize: pageSize));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(CurrencyExchangeIndexViewModel requestModel)
    {
        if (!await IsSystemAdminAsync())
        {
            return Forbid();
        }

        var model = requestModel.EditForm;
        NormalizeRateForm(model);
        RemoveModelStateForPrefix(nameof(CurrencyExchangeIndexViewModel.CreateForm));
        RemoveModelStateForPrefix(nameof(CurrencyExchangeIndexViewModel.ConversionForm));
        await ValidateDuplicateAsync(model, nameof(CurrencyExchangeIndexViewModel.EditForm), model.Id);
        ValidateRate(model, nameof(CurrencyExchangeIndexViewModel.EditForm));

        if (!ModelState.IsValid)
        {
            return View(IndexViewPath, await BuildIndexViewModelAsync(editForm: model, openEditModal: true, page: model.CurrentPage, pageSize: model.PageSize));
        }

        var (userId, username) = GetCurrentAuditContext();
        try
        {
            await currencyExchangeService.UpdateRateAsync(model, userId, username, HttpContext.RequestAborted);
            TempData["CurrencyExchangeSuccess"] = "Cập nhật mốc tỷ giá thành công.";
            return RedirectToAction(nameof(Index), new { page = model.CurrentPage, pageSize = model.PageSize });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update currency exchange rate id {RateId}.", model.Id);
            ModelState.AddModelError(string.Empty, BuildSaveErrorMessage(exception));
            return View(IndexViewPath, await BuildIndexViewModelAsync(editForm: model, openEditModal: true, page: model.CurrentPage, pageSize: model.PageSize));
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
            await currencyExchangeService.DeleteRateAsync(id, userId, username, HttpContext.RequestAborted);
            TempData["CurrencyExchangeSuccess"] = "Xóa mốc tỷ giá thành công.";
        }
        catch (KeyNotFoundException)
        {
            TempData["CurrencyExchangeError"] = "Mốc tỷ giá không tồn tại hoặc đã bị xóa.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete currency exchange rate id {RateId}.", id);
            TempData["CurrencyExchangeError"] = "Không thể xóa mốc tỷ giá. Vui lòng thử lại.";
        }

        return RedirectToAction(nameof(Index), new { page, pageSize });
    }

    private async Task<CurrencyExchangeIndexViewModel> BuildIndexViewModelAsync(
        CurrencyExchangeRateFormViewModel? createForm = null,
        bool openCreateModal = false,
        CurrencyExchangeRateFormViewModel? editForm = null,
        bool openEditModal = false,
        CurrencyConversionFormViewModel? conversionForm = null,
        CurrencyConversionResultViewModel? conversionResult = null,
        int page = 1,
        int pageSize = 10)
    {
        var ratePage = await currencyExchangeService.GetRatesAsync(page, pageSize, HttpContext.RequestAborted);
        createForm ??= new CurrencyExchangeRateFormViewModel();
        createForm.CurrentPage = ratePage.CurrentPage;
        createForm.PageSize = ratePage.PageSize;

        editForm ??= new CurrencyExchangeRateFormViewModel();
        editForm.CurrentPage = ratePage.CurrentPage;
        editForm.PageSize = ratePage.PageSize;

        conversionForm ??= new CurrencyConversionFormViewModel();

        return new CurrencyExchangeIndexViewModel
        {
            Rates = ratePage.Rates,
            CreateForm = createForm,
            EditForm = editForm,
            ConversionForm = conversionForm,
            ConversionResult = conversionResult,
            OpenCreateModal = openCreateModal,
            OpenEditModal = openEditModal,
            CurrentPage = ratePage.CurrentPage,
            PageSize = ratePage.PageSize,
            TotalRates = ratePage.TotalRates
        };
    }

    private async Task ValidateDuplicateAsync(CurrencyExchangeRateFormViewModel model, string prefix, int? excludeId)
    {
        if (await currencyExchangeService.IsRateInUseAsync(model.FromCurrency, model.ToCurrency, model.EffectiveDate, excludeId, HttpContext.RequestAborted))
        {
            ModelState.AddModelError($"{prefix}.{nameof(CurrencyExchangeRateFormViewModel.EffectiveDate)}", DuplicateRateMessage);
        }
    }

    private void ValidateRate(CurrencyExchangeRateFormViewModel model, string prefix)
    {
        if (string.Equals(model.FromCurrency, model.ToCurrency, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError($"{prefix}.{nameof(CurrencyExchangeRateFormViewModel.ToCurrency)}", "Đồng tiền nguồn và đồng tiền đích phải khác nhau.");
        }

        if (!string.Equals(model.Status, "active", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(model.Status, "inactive", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError($"{prefix}.{nameof(CurrencyExchangeRateFormViewModel.Status)}", "Trạng thái không hợp lệ.");
        }
    }

    private static void NormalizeRateForm(CurrencyExchangeRateFormViewModel model)
    {
        model.FromCurrency = NormalizeCurrency(model.FromCurrency);
        model.ToCurrency = NormalizeCurrency(model.ToCurrency);
        model.EffectiveDate = model.EffectiveDate.Date;
        model.Status = string.IsNullOrWhiteSpace(model.Status) ? "active" : model.Status.Trim().ToLowerInvariant();
    }

    private static void NormalizeConversionForm(CurrencyConversionFormViewModel model)
    {
        model.FromCurrency = NormalizeCurrency(model.FromCurrency);
        model.ToCurrency = NormalizeCurrency(model.ToCurrency);
        model.ConversionDate = model.ConversionDate.Date;
    }

    private static string NormalizeCurrency(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();

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

    private static string BuildSaveErrorMessage(Exception exception)
    {
        var rootException = exception.GetBaseException();
        if (rootException is SqlException sqlException && sqlException.Number is 2627 or 2601)
        {
            return DuplicateRateMessage;
        }

        return $"Không thể lưu mốc tỷ giá. Chi tiết: {rootException.Message}";
    }
}
