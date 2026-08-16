using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class MonthlySubscriptionController(
    IMonthlySubscriptionService subscriptionService,
    IInvoicePdfService invoicePdfService,
    IKvhPaymentResumeService kvhPaymentResumeService,
    ITenantService tenantService,
    ISqlAuthService authService,
    ILogger<MonthlySubscriptionController> logger) : Controller
{
    private const string IndexViewPath = "~/Views/MonthlySubscription/Index.cshtml";
    private const string DetailViewPath = "~/Views/MonthlySubscription/Details.cshtml";

    [HttpGet]
    public async Task<IActionResult> Index(
        int page = 1,
        int pageSize = 10,
        int? tenantId = null,
        int? deviceId = null,
        int? pricingPlanId = null,
        string? kitId = null,
        string? status = null,
        string? invoiceStatus = null,
        DateTime? monthFrom = null,
        DateTime? monthTo = null,
        DateTime? nextBillingFrom = null,
        DateTime? nextBillingTo = null,
        DateTime? invoicePaidFrom = null,
        DateTime? invoicePaidTo = null,
        bool openCreate = false)
    {
        var currentUser = await GetCurrentUserAsync();
        var allowedTenantId = GetAllowedTenantId(currentUser);
        var allowedDeviceId = GetAllowedDeviceId(currentUser);
        var filter = new MonthlySubscriptionFilterViewModel
        {
            TenantId = tenantId,
            DeviceId = deviceId,
            PricingPlanId = pricingPlanId,
            KitId = kitId,
            Status = status,
            InvoiceStatus = invoiceStatus,
            MonthFrom = monthFrom,
            MonthTo = monthTo,
            NextBillingFrom = nextBillingFrom,
            NextBillingTo = nextBillingTo,
            InvoicePaidFrom = invoicePaidFrom,
            InvoicePaidTo = invoicePaidTo
        };

        if (allowedTenantId.HasValue)
        {
            filter.TenantId = allowedTenantId.Value;
        }

        if (allowedDeviceId.HasValue)
        {
            filter.DeviceId = allowedDeviceId.Value;
        }

        var pageResult = await subscriptionService.GetSubscriptionsAsync(filter, page, pageSize, allowedTenantId, allowedDeviceId, HttpContext.RequestAborted);
        var tenants = await tenantService.GetTenantOptionsAsync(allowedTenantId, HttpContext.RequestAborted);
        var devices = await subscriptionService.GetDeviceOptionsAsync(allowedTenantId, allowedDeviceId, HttpContext.RequestAborted);
        var plans = await subscriptionService.GetPlanOptionsAsync(allowedTenantId, allowedDeviceId, HttpContext.RequestAborted);
        var createForm = new CreateMonthlySubscriptionViewModel();
        if (openCreate && filter.DeviceId.HasValue)
        {
            var selectedDevice = devices.FirstOrDefault(device => device.Id == filter.DeviceId.Value);
            if (selectedDevice is not null)
            {
                createForm.TenantId = selectedDevice.TenantId;
                createForm.DeviceId = selectedDevice.Id;
            }
        }

        return View(IndexViewPath, new MonthlySubscriptionIndexViewModel
        {
            Subscriptions = pageResult.Subscriptions,
            Summary = pageResult.Summary,
            Tenants = tenants,
            Devices = devices,
            Plans = plans,
            CreateForm = createForm,
            Filter = filter,
            CurrentPage = pageResult.CurrentPage,
            PageSize = pageResult.PageSize,
            TotalItems = pageResult.TotalItems,
            IsTenantScoped = allowedTenantId.HasValue,
            CanManageSubscriptions = CanManageSubscriptions(currentUser),
            CanCreateSubscriptions = CanCreateSubscriptions(currentUser),
            OpenCreateModal = openCreate && CanCreateSubscriptions(currentUser)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(MonthlySubscriptionIndexViewModel requestModel)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanCreateSubscriptions(currentUser))
        {
            return Forbid();
        }

        var model = requestModel.CreateForm;
        NormalizeCreateModel(model);
        if (!ValidateCreateModel(model))
        {
            TempData["SubscriptionError"] = "Vui lÃ²ng nháº­p Ä‘áº§y Ä‘á»§ thÃ´ng tin billing cycle há»£p lá»‡.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            var subscriptionIds = await subscriptionService.CreateSubscriptionAsync(model, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            var firstSubscriptionId = subscriptionIds.FirstOrDefault();
            TempData["SubscriptionSuccess"] = subscriptionIds.Count == 1
                ? $"Táº¡o billing cycle #{firstSubscriptionId} thÃ nh cÃ´ng."
                : $"Táº¡o {subscriptionIds.Count} billing cycle theo tá»«ng chu ká»³ thÃ nh cÃ´ng.";
            return firstSubscriptionId > 0
                ? RedirectToAction(nameof(Details), new { id = firstSubscriptionId })
                : RedirectToAction(nameof(Index));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create billing cycle.");
            TempData["SubscriptionError"] = $"KhÃ´ng thá»ƒ táº¡o billing cycle. Chi tiáº¿t: {exception.GetBaseException().Message}";
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id)
    {
        var currentUser = await GetCurrentUserAsync();
        var detail = await subscriptionService.GetSubscriptionDetailAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
        if (detail is null)
        {
            return NotFound();
        }

        detail.CanManageSubscriptions = CanManageSubscriptions(currentUser);
        detail.CanViewQrSessions = CanViewQrSessions(currentUser);
        detail.CanViewIntegrationLogs = CanViewIntegrationLogs(currentUser);
        var pdfFiles = detail.CanViewIntegrationLogs
            ? await invoicePdfService.GetCurrentFilesForSubscriptionAsync(detail.Subscription.Id, false, false, HttpContext.RequestAborted)
            : [];
        foreach (var invoice in detail.Invoices)
        {
            invoice.PdfFile = pdfFiles.GetValueOrDefault(invoice.Id) ?? new InvoicePdfFileViewModel
            {
                Available = false,
                CanReplace = false,
                CanDelete = false
            };
        }
        return View(DetailViewPath, detail);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadInvoicePdf(int subscriptionId, string invoiceCode, IFormFile? file)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            var result = await invoicePdfService.UploadAsync(new InvoicePdfUploadRequest
            {
                InvoiceCode = invoiceCode,
                File = file,
                SourceSystem = "ShipNet",
                UploadedByUserId = userId,
                UploadedBy = username,
                AllowedTenantId = GetAllowedTenantId(currentUser),
                AllowedDeviceId = GetAllowedDeviceId(currentUser)
            }, HttpContext.RequestAborted);

            return Json(new { success = true, pdfFile = result });
        }
        catch (InvoicePdfError error)
        {
            Response.StatusCode = error.StatusCode;
            return Json(new { success = false, errorCode = error.ErrorCode, message = error.Message, messageEn = error.MessageEn });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to upload invoice PDF. InvoiceCode={InvoiceCode}.", invoiceCode);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Json(new { success = false, errorCode = "storage_error", message = "KhÃ´ng thá»ƒ lÆ°u file PDF.", messageEn = "Cannot save the PDF file." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteInvoicePdf(int subscriptionId, string invoiceCode)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            await invoicePdfService.DeleteAsync(invoiceCode, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            return Json(new { success = true });
        }
        catch (InvoicePdfError error)
        {
            Response.StatusCode = error.StatusCode;
            return Json(new { success = false, errorCode = error.ErrorCode, message = error.Message, messageEn = error.MessageEn });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to delete invoice PDF. InvoiceCode={InvoiceCode}.", invoiceCode);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Json(new { success = false, errorCode = "delete_failed", message = "KhÃ´ng thá»ƒ xÃ³a file PDF.", messageEn = "Cannot delete the PDF file." });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateInvoice(CreateSubscriptionInvoiceViewModel model)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            await subscriptionService.CreateInvoiceAsync(model, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            TempData["SubscriptionSuccess"] = "Táº¡o invoice thÃ nh cÃ´ng.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to create subscription invoice.");
            TempData["SubscriptionError"] = $"KhÃ´ng thá»ƒ táº¡o invoice. Chi tiáº¿t: {exception.GetBaseException().Message}";
        }

        return RedirectToAction(nameof(Details), new { id = model.SubscriptionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> KvhPaymentResumePrecheck([FromForm] int invoiceId, [FromForm] int subscriptionId)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        var result = await kvhPaymentResumeService.PrecheckAsync(
            invoiceId,
            subscriptionId,
            GetAllowedTenantId(currentUser),
            GetAllowedDeviceId(currentUser),
            HttpContext.RequestAborted);

        return Json(new
        {
            success = result.Success,
            message = result.Message,
            deviceId = result.DeviceId,
            deviceName = result.DeviceName,
            vesselName = result.VesselName,
            kitNumber = result.KitNumber,
            kvhStatus = result.KvhStatus,
            canResume = result.CanResume
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateInvoice(UpdateSubscriptionInvoiceViewModel model)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            var operationCorrelationId = $"INV-{model.InvoiceId}-{Guid.NewGuid():N}";
            model.OperationCorrelationId = operationCorrelationId;
            var updateResult = await subscriptionService.UpdateInvoiceAsync(model, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            var successMessage = "Cap nhat invoice thanh cong.";
            string? warningMessage = null;
            if (model.ResumeKvh && updateResult.BecamePaid)
            {
                var resumeResult = await kvhPaymentResumeService.HandlePaidSubscriptionAsync(new KvhPaymentResumeRequest
                {
                    SubscriptionId = updateResult.SubscriptionId,
                    Source = DeviceActivitySources.ManualInvoiceUpdate,
                    ActorType = DeviceActivityActorTypes.User,
                    UserId = userId,
                    PerformedBy = username,
                    ReferenceType = "INVOICE",
                    ReferenceId = updateResult.InvoiceId.ToString(),
                    CorrelationId = operationCorrelationId,
                    AllowedTenantId = GetAllowedTenantId(currentUser),
                    AllowedDeviceId = GetAllowedDeviceId(currentUser),
                    DetailJson = DeviceActivityLogEntry.ToSafeJson(new { updateResult.InvoiceId, updateResult.InvoiceNumber, updateResult.OldStatus, updateResult.NewStatus })
                }, HttpContext.RequestAborted);

                if (!resumeResult.Success)
                {
                    warningMessage = $"Invoice da cap nhat Paid nhung KVH Resume that bai: {resumeResult.Message}";
                }
                else if (resumeResult.Skipped)
                {
                    warningMessage = $"Invoice da cap nhat Paid. KVH Resume duoc bo qua: {resumeResult.Message}";
                }
                else if (resumeResult.ResumeSubmitted)
                {
                    successMessage = "Cap nhat invoice thanh cong va da gui lenh Resume KVH.";
                }
            }
            TempData["SubscriptionSuccess"] = successMessage;
            if (!string.IsNullOrWhiteSpace(warningMessage))
            {
                TempData["SubscriptionWarning"] = warningMessage;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update subscription invoice.");
            TempData["SubscriptionError"] = $"KhÃ´ng thá»ƒ cáº­p nháº­t invoice. Chi tiáº¿t: {exception.GetBaseException().Message}";
        }

        return RedirectToAction(nameof(Details), new { id = model.SubscriptionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateBilling(UpdateMonthlySubscriptionBillingViewModel model)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            await subscriptionService.UpdateSubscriptionBillingAsync(model, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            TempData["SubscriptionSuccess"] = "Cáº­p nháº­t billing period thÃ nh cÃ´ng.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update billing cycle billing.");
            TempData["SubscriptionError"] = $"KhÃ´ng thá»ƒ cáº­p nháº­t billing period. Chi tiáº¿t: {exception.GetBaseException().Message}";
        }

        return RedirectToAction(nameof(Details), new { id = model.SubscriptionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatus(UpdateMonthlySubscriptionStatusViewModel model)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageSubscriptions(currentUser))
        {
            return Forbid();
        }

        try
        {
            var (userId, username) = GetCurrentAuditContext();
            await subscriptionService.UpdateSubscriptionStatusAsync(model, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            TempData["SubscriptionSuccess"] = "Cáº­p nháº­t tráº¡ng thÃ¡i billing cycle thÃ nh cÃ´ng.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update billing cycle status.");
            TempData["SubscriptionError"] = $"KhÃ´ng thá»ƒ cáº­p nháº­t tráº¡ng thÃ¡i billing cycle. Chi tiáº¿t: {exception.GetBaseException().Message}";
        }

        return RedirectToAction(nameof(Details), new { id = model.SubscriptionId });
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

    private static bool CanManageSubscriptions(AuthUserRecord? user)
    {
        return user is not null && !user.IsViewOnly && (IsAdminAccount(user) || IsTenantAdmin(user));
    }

    private static bool CanCreateSubscriptions(AuthUserRecord? user)
    {
        return user is not null && !user.IsViewOnly && !user.IsShipAdmin && !user.IsCrew && (IsAdminAccount(user) || IsTenantAdmin(user));
    }

    private static bool CanViewQrSessions(AuthUserRecord? user)
    {
        return IsAdminAccount(user);
    }

    private static bool CanViewIntegrationLogs(AuthUserRecord? user)
    {
        return IsAdminAccount(user);
    }

    private static bool IsAdminAccount(AuthUserRecord? user)
    {
        return user is not null &&
            (user.IsAdmin || string.Equals(user.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTenantAdmin(AuthUserRecord? user)
    {
        return user?.IsTenantUser == true && user.HasTenantScope;
    }

    private (int? UserId, string Username) GetCurrentAuditContext()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
        var username = string.IsNullOrWhiteSpace(User.Identity?.Name) ? "system" : User.Identity.Name!;
        return (userId, username);
    }

    private static void NormalizeCreateModel(CreateMonthlySubscriptionViewModel model)
    {
        model.SubscriptionType = string.IsNullOrWhiteSpace(model.SubscriptionType) ? "Personal" : model.SubscriptionType.Trim();
        model.UsageMonth = model.UsageMonth == default ? default : new DateTime(model.UsageMonth.Year, model.UsageMonth.Month, 1);
        model.StartDate = model.StartDate.Date;
        model.EndDate = model.EndDate.Date;
        model.NextBillingDate = model.NextBillingDate.Date;
    }

    private static bool ValidateCreateModel(CreateMonthlySubscriptionViewModel model)
    {
        return model.TenantId > 0
            && model.DeviceId > 0
            && model.PricingPlanId > 0
            && model.UsageMonth != default
            && model.StartDate != default
            && model.EndDate != default
            && model.NextBillingDate != default
            && model.StartDate <= model.EndDate;
    }
}
