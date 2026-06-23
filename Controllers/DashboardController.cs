using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[Authorize]
public class DashboardController(
    IDeviceService deviceService,
    ITenantService tenantService,
    ISqlAuthService authService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, int pageSize = 10, string? search = null)
    {
        return View("~/Views/Dashboard/Index.cshtml", await BuildDashboardViewModelAsync(page, pageSize, search));
    }

    [HttpGet]
    public async Task<IActionResult> DevicePageData(int page = 1, int pageSize = 10, string? search = null)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            var normalizedSearch = NormalizeSearchTerm(search);
            var result = await deviceService.GetDevicesAsync(page, pageSize, normalizedSearch, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            return Json(new
            {
                currentPage = page < 1 ? 1 : page,
                pageSize = pageSize <= 0 ? 10 : pageSize,
                totalDevices = result.TotalDevices,
                searchTerm = normalizedSearch ?? string.Empty,
                devices = result.Devices
            });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = ex.Message,
                detail = ex.ToString()
            });
        }
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, int page = 1, int pageSize = 10, string? search = null)
    {
        var currentUser = await GetCurrentUserAsync();
        var device = await deviceService.GetDeviceByIdAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
        if (device is null)
        {
            return NotFound();
        }

        var model = await BuildDashboardViewModelAsync(page, pageSize, search);
        model.SelectedDeviceId = id;
        return View("~/Views/Dashboard/Index.cshtml", model);
    }

    [HttpGet]
    public async Task<IActionResult> DeviceEditData(int id)
    {
        var currentUser = await GetCurrentUserAsync();
        if (!CanManageDevices(currentUser))
        {
            return Forbid();
        }

        var device = await deviceService.GetDeviceByIdAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
        if (device is null)
        {
            return AjaxError(
                StatusCodes.Status404NotFound,
                "device_not_found",
                "Khong tim thay thiet bi hoac ban khong co quyen truy cap.",
                "The device was not found or you do not have access.");
        }

        return Json(device);
    }

    [HttpGet]
    public async Task<IActionResult> DetailData(int id)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
            var device = await deviceService.GetDeviceDetailAsync(id, userId, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (device is null)
            {
                return AjaxError(
                    StatusCodes.Status404NotFound,
                    "device_not_found",
                    "Khong tim thay thiet bi hoac ban khong co quyen truy cap.",
                    "The device was not found or you do not have access.");
            }

            if (!CanViewMap(currentUser))
            {
                device.Latitude = string.Empty;
                device.Longitude = string.Empty;
            }

            return Json(device);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                message = ex.Message,
                detail = ex.ToString()
            });
        }
    }

    [HttpGet]
    public async Task<IActionResult> DeviceWifiData(int id)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            var result = await deviceService.GetDeviceWifiAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "device_not_found" => StatusCodes.Status404NotFound,
                    "missing_wifi_identifiers" or "router_device_not_found" or "wifi_endpoint_not_found" => StatusCodes.Status400BadRequest,
                    "missing_api_credentials" => StatusCodes.Status500InternalServerError,
                    "missing_access_token" => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status502BadGateway
                };

                return StatusCode(statusCode, result);
            }

            return Json(result);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new DeviceWifiResult
            {
                Success = false,
                ErrorCode = "server_exception",
                Message = ex.Message,
                MessageEn = ex.Message,
                RawResponse = ex.ToString()
            });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateDeviceWifi([FromForm] UpdateDeviceWifiRequest request)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            var result = await deviceService.UpdateDeviceWifiAsync(request, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "device_not_found" => StatusCodes.Status404NotFound,
                    "missing_wifi_identifiers" or "router_device_not_found" or "wifi_validation_required" => StatusCodes.Status400BadRequest,
                    "missing_api_credentials" => StatusCodes.Status500InternalServerError,
                    "missing_access_token" => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status502BadGateway
                };

                return StatusCode(statusCode, result);
            }

            return Json(result);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new DeviceCommandResult
            {
                Success = false,
                ErrorCode = "server_exception",
                Message = ex.Message,
                MessageEn = ex.Message,
                RawResponse = ex.ToString()
            });
        }
    }

    [HttpGet]
    public async Task<IActionResult> DevicePlanData(int id)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            var result = await deviceService.GetDevicePlanManagementAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "device_not_found" => StatusCodes.Status404NotFound,
                    "missing_tenant" => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status500InternalServerError
                };

                return StatusCode(statusCode, result);
            }

            return Json(result);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new DevicePlanManagementResult
            {
                Success = false,
                ErrorCode = "server_exception",
                Message = ex.Message,
                MessageEn = ex.Message
            });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDevicePlan([FromForm] SaveDevicePlanRequest request)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            if (!CanManageDevices(currentUser))
            {
                return Forbid();
            }

            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
            var username = string.IsNullOrWhiteSpace(User.Identity?.Name) ? "system" : User.Identity.Name!;
            var result = await deviceService.SaveDevicePlanAsync(request, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "device_not_found" => StatusCodes.Status404NotFound,
                    "missing_tenant" or "validation_required" or "invalid_price" or "plan_not_found" => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status500InternalServerError
                };

                return StatusCode(statusCode, result);
            }

            return Json(result);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new SaveDevicePlanResult
            {
                Success = false,
                ErrorCode = "server_exception",
                Message = ex.Message,
                MessageEn = ex.Message
            });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteDevicePlan([FromForm] DeleteDevicePlanRequest request)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            if (!CanManageDevices(currentUser))
            {
                return Forbid();
            }

            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
            var username = string.IsNullOrWhiteSpace(User.Identity?.Name) ? "system" : User.Identity.Name!;
            var result = await deviceService.DeleteDevicePlanAsync(request, userId, username, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "device_not_found" or "plan_not_found" => StatusCodes.Status404NotFound,
                    "validation_required" => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status500InternalServerError
                };

                return StatusCode(statusCode, result);
            }

            return Json(result);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new DeleteDevicePlanResult
            {
                Success = false,
                ErrorCode = "server_exception",
                Message = ex.Message,
                MessageEn = ex.Message
            });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RebootDeviceRouter(int id)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            var result = await deviceService.RebootDeviceRouterAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (!result.Success)
            {
                var statusCode = result.ErrorCode switch
                {
                    "device_not_found" => StatusCodes.Status404NotFound,
                    "missing_wifi_identifiers" or "router_device_not_found" => StatusCodes.Status400BadRequest,
                    "missing_api_credentials" => StatusCodes.Status500InternalServerError,
                    "missing_access_token" => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status502BadGateway
                };

                return StatusCode(statusCode, result);
            }

            return Json(result);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new DeviceCommandResult
            {
                Success = false,
                ErrorCode = "server_exception",
                Message = ex.Message,
                MessageEn = ex.Message,
                RawResponse = ex.ToString()
            });
        }
    }

    [HttpGet]
    public async Task<IActionResult> TelemetryTimeline(int id, long start, long end, string metric = "uplink_throughput")
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            var device = await deviceService.GetDeviceByIdAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (device is null)
            {
                return AjaxError(
                    StatusCodes.Status404NotFound,
                    "device_not_found",
                    "Khong tim thay thiet bi hoac ban khong co quyen truy cap.",
                    "The device was not found or you do not have access.");
            }

            var result = await deviceService.GetTelemetryTimelineAsync(id, start, end, metric, HttpContext.RequestAborted);
            if (!result.Success)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    success = false,
                    message = result.Message,
                    messageEn = result.MessageEn,
                    rawResponse = result.RawResponse,
                    terminalId = result.TerminalId,
                    metric = result.Metric,
                    unit = result.Unit,
                    start = result.Start,
                    end = result.End
                });
            }

            return Json(new
            {
                success = true,
                terminalId = result.TerminalId,
                metric = result.Metric,
                unit = result.Unit,
                start = result.Start,
                end = result.End,
                points = result.Points
            });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                success = false,
                message = ex.Message,
                detail = ex.ToString()
            });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateDevice([FromForm] CreateDeviceRequest request)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            if (!CanManageDevices(currentUser))
            {
                return Forbid();
            }

            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
            var result = await deviceService.CreateDeviceAsync(request, userId, HttpContext.RequestAborted);
            return Json(result);
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException is null
                ? ex.ToString()
                : $"{ex}\n\nINNER EXCEPTION:\n{ex.InnerException}";

            return StatusCode(StatusCodes.Status500InternalServerError, new CreateDeviceResult
            {
                ErrorCode = "server_exception",
                Message = ex.Message,
                MessageEn = ex.Message,
                ApiResult = detail
            });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateDevice([FromForm] UpdateDeviceRequest request)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            if (!CanManageDevices(currentUser))
            {
                return Forbid();
            }

            var allowedTenantId = GetAllowedTenantId(currentUser);
            var existingDevice = await deviceService.GetDeviceByIdAsync(request.Id, allowedTenantId, GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (existingDevice is null)
            {
                return AjaxError(
                    StatusCodes.Status404NotFound,
                    "device_not_found",
                    "Khong tim thay thiet bi hoac ban khong co quyen truy cap.",
                    "The device was not found or you do not have access.");
            }

            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
            var result = await deviceService.UpdateDeviceAsync(request, userId, HttpContext.RequestAborted);
            return Json(result);
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException is null
                ? ex.ToString()
                : $"{ex}\n\nINNER EXCEPTION:\n{ex.InnerException}";

            return StatusCode(StatusCodes.Status500InternalServerError, new UpdateDeviceResult
            {
                ErrorCode = "server_exception",
                Message = ex.Message,
                MessageEn = ex.Message,
                ApiResult = detail
            });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteDevice(int id)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            if (!CanManageDevices(currentUser))
            {
                return Forbid();
            }

            var existingDevice = await deviceService.GetDeviceByIdAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (existingDevice is null)
            {
                return AjaxError(
                    StatusCodes.Status404NotFound,
                    "device_not_found",
                    "Khong tim thay thiet bi hoac ban khong co quyen truy cap.",
                    "The device was not found or you do not have access.");
            }

            var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
            int? userId = int.TryParse(userIdValue, out var parsedUserId) ? parsedUserId : null;
            var result = await deviceService.DeleteDeviceAsync(id, userId, HttpContext.RequestAborted);
            return Json(result);
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException is null
                ? ex.ToString()
                : $"{ex}\n\nINNER EXCEPTION:\n{ex.InnerException}";

            return StatusCode(StatusCodes.Status500InternalServerError, new DeleteDeviceResult
            {
                ErrorCode = "server_exception",
                Message = ex.Message,
                MessageEn = ex.Message
            });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshExpiredDevice(int id)
    {
        try
        {
            var currentUser = await GetCurrentUserAsync();
            var existingDevice = await deviceService.GetDeviceByIdAsync(id, GetAllowedTenantId(currentUser), GetAllowedDeviceId(currentUser), HttpContext.RequestAborted);
            if (existingDevice is null)
            {
                return AjaxError(
                    StatusCodes.Status404NotFound,
                    "device_not_found",
                    "Khong tim thay thiet bi hoac ban khong co quyen truy cap.",
                    "The device was not found or you do not have access.");
            }

            var result = await deviceService.RefreshExpiredDeviceAsync(id, HttpContext.RequestAborted);
            return Json(result);
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException is null
                ? ex.ToString()
                : $"{ex}\n\nINNER EXCEPTION:\n{ex.InnerException}";

            return StatusCode(StatusCodes.Status500InternalServerError, new RefreshDeviceResult
            {
                ErrorCode = "server_exception",
                Message = ex.Message,
                MessageEn = ex.Message,
                ApiResult = detail
            });
        }
    }

    private async Task<DeviceDashboardViewModel> BuildDashboardViewModelAsync(int page, int pageSize, string? search)
    {
        var currentUser = await GetCurrentUserAsync();
        var normalizedPage = page < 1 ? 1 : page;
        var normalizedPageSize = pageSize <= 0 ? 10 : pageSize;
        var normalizedSearch = NormalizeSearchTerm(search);
        var allowedTenantId = GetAllowedTenantId(currentUser);
        var allowedDeviceId = GetAllowedDeviceId(currentUser);
        var devicePage = await deviceService.GetDevicesAsync(normalizedPage, normalizedPageSize, normalizedSearch, allowedTenantId, allowedDeviceId, HttpContext.RequestAborted);
        var tenants = await tenantService.GetTenantOptionsAsync(allowedTenantId, HttpContext.RequestAborted);

        return new DeviceDashboardViewModel
        {
            Devices = devicePage.Devices,
            Tenants = tenants,
            CurrentPage = normalizedPage,
            PageSize = normalizedPageSize,
            TotalDevices = devicePage.TotalDevices,
            SearchTerm = normalizedSearch ?? string.Empty,
            IsTenantScoped = currentUser?.IsTenantUser == true || currentUser?.IsShipAdmin == true || currentUser?.IsCrew == true,
            CurrentTenantId = currentUser?.TenantId,
            CurrentTenantName = tenants.FirstOrDefault(tenant => tenant.Id == currentUser?.TenantId)?.TenantName,
            CanManageDevices = CanManageDevices(currentUser),
            CanViewMap = CanViewMap(currentUser)
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

    private static bool CanManageDevices(AuthUserRecord? user)
    {
        return user is not null && !user.IsTenantUser && !user.IsShipAdmin && !user.IsCrew;
    }

    private static bool CanViewMap(AuthUserRecord? user)
    {
        return user is not null && !user.IsShipAdmin && !user.IsCrew;
    }

    private static string? NormalizeSearchTerm(string? search)
    {
        return string.IsNullOrWhiteSpace(search) ? null : search.Trim();
    }

    private IActionResult AjaxError(int statusCode, string errorCode, string message, string messageEn)
    {
        if (IsAjaxRequest())
        {
            return StatusCode(statusCode, new
            {
                success = false,
                errorCode,
                message,
                messageEn
            });
        }

        return StatusCode(statusCode);
    }

    private bool IsAjaxRequest()
    {
        return string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    }
}
