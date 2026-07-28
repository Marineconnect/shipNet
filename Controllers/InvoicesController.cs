using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[ApiController]
[Route("api/invoices")]
public sealed class InvoicesController(
    IInvoicePdfService invoicePdfService,
    IInvoiceIntegrationLogService invoiceIntegrationLogService,
    ISqlAuthService authService,
    IOptions<InvoicePdfIntegrationOptions> integrationOptions,
    IOptions<InvoiceIntegrationLogOptions> logOptions,
    ILogger<InvoicesController> logger) : ControllerBase
{
    private readonly InvoicePdfIntegrationOptions integration = integrationOptions.Value;
    private readonly InvoiceIntegrationLogOptions integrationLogOptions = logOptions.Value;

    [HttpPost("{invoiceCode}/pdf")]
    [RequestSizeLimit(250_000_000)]
    public async Task<IActionResult> UploadPdf(
        string invoiceCode,
        [FromForm] IFormFile? file,
        [FromForm] string transactionCode = "",
        [FromForm] string sourceSystem = "",
        [FromForm] DateTime? generatedAt = null,
        [FromForm] string externalReference = "")
    {
        var keyResult = ValidateApiKey();
        if (keyResult is not null)
        {
            return keyResult;
        }

        try
        {
            var result = await invoicePdfService.UploadAsync(new InvoicePdfUploadRequest
            {
                InvoiceCode = invoiceCode,
                File = file,
                TransactionCode = transactionCode,
                SourceSystem = string.IsNullOrWhiteSpace(sourceSystem) ? "InvoiceGenerator" : sourceSystem,
                GeneratedAt = generatedAt,
                ExternalReference = externalReference,
                UploadedBy = string.IsNullOrWhiteSpace(sourceSystem) ? "InvoiceGenerator" : sourceSystem
            }, HttpContext.RequestAborted);

            return Ok(result);
        }
        catch (InvoicePdfError error)
        {
            return Error(error);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "External invoice PDF upload failed. InvoiceCode={InvoiceCode}.", invoiceCode);
            return StatusCode(StatusCodes.Status500InternalServerError, ErrorBody("storage_error", "Không thể lưu file PDF.", "Cannot save the PDF file."));
        }
    }

    [Authorize]
    [HttpGet("{invoiceCode}/pdf/file")]
    public async Task<IActionResult> GetPdfFile(string invoiceCode, [FromQuery] bool download = false)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }
        if (!CanViewIntegrationLogs(user))
        {
            return Forbid();
        }

        try
        {
            var result = await invoicePdfService.OpenReadAsync(invoiceCode, GetAllowedTenantId(user), GetAllowedDeviceId(user), HttpContext.RequestAborted);
            if (result is null)
            {
                return NotFound(ErrorBody("pdf_not_found", "Invoice chưa có file PDF.", "No PDF file has been uploaded for this invoice."));
            }

            var dispositionName = SanitizeDownloadName(result.Record.FileName);
            return File(result.Stream, "application/pdf", download ? dispositionName : null, enableRangeProcessing: true);
        }
        catch (InvoicePdfError error)
        {
            return Error(error);
        }
    }

    [Authorize]
    [HttpGet("{invoiceCode}/integration-logs")]
    public async Task<IActionResult> GetIntegrationLogs(string invoiceCode, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string eventType = "")
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }
        if (!CanViewIntegrationLogs(user))
        {
            return Forbid();
        }

        try
        {
            var items = await invoiceIntegrationLogService.GetLogsAsync(
                invoiceCode,
                page,
                pageSize,
                eventType,
                GetAllowedTenantId(user),
                GetAllowedDeviceId(user),
                HttpContext.RequestAborted);

            return Ok(new { success = true, items });
        }
        catch (InvoicePdfError error)
        {
            return Error(error);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load invoice integration logs. InvoiceCode={InvoiceCode}.", invoiceCode);
            return StatusCode(StatusCodes.Status500InternalServerError, ErrorBody(
                "integration_log_load_failed",
                $"Khong the tai lich su tich hop. Chi tiet: {exception.GetBaseException().Message}",
                $"Cannot load integration logs. Detail: {exception.GetBaseException().Message}"));
        }
    }

    [Authorize]
    [HttpGet("{invoiceCode}/integration-logs/{logId:long}")]
    public async Task<IActionResult> GetIntegrationLogDetail(string invoiceCode, long logId)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
        {
            return Unauthorized();
        }
        if (!CanViewIntegrationLogs(user))
        {
            return Forbid();
        }

        try
        {
            var entry = await invoiceIntegrationLogService.GetLogDetailAsync(
                invoiceCode,
                logId,
                GetAllowedTenantId(user),
                GetAllowedDeviceId(user),
                HttpContext.RequestAborted);
            if (entry is null)
            {
                return NotFound(ErrorBody("log_not_found", "Không tìm thấy log tích hợp.", "Integration log was not found."));
            }

            var maxLength = Math.Clamp(integrationLogOptions.MaxPayloadDisplayLength <= 0 ? 200000 : integrationLogOptions.MaxPayloadDisplayLength, 1000, 2_000_000);
            var payload = entry.PayloadJson ?? string.Empty;
            var truncated = payload.Length > maxLength;
            if (truncated)
            {
                payload = payload[..maxLength];
            }

            entry.PayloadJson = string.Empty;

            return Ok(new
            {
                success = true,
                log = entry,
                payloadJson = payload,
                payloadTruncated = truncated
            });
        }
        catch (InvoicePdfError error)
        {
            return Error(error);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load invoice integration log detail. InvoiceCode={InvoiceCode}; LogId={LogId}.", invoiceCode, logId);
            return StatusCode(StatusCodes.Status500InternalServerError, ErrorBody(
                "integration_log_detail_load_failed",
                $"Khong the tai JSON log tich hop. Chi tiet: {exception.GetBaseException().Message}",
                $"Cannot load integration log JSON. Detail: {exception.GetBaseException().Message}"));
        }
    }

    private IActionResult? ValidateApiKey()
    {
        var headerName = string.IsNullOrWhiteSpace(integration.HeaderName)
            ? "X-ShipNet-Api-Key"
            : integration.HeaderName.Trim();
        var configuredKey = integration.ApiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            logger.LogError("Invoice PDF upload API key is not configured.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ErrorBody("api_key_not_configured", "API key chưa được cấu hình.", "The API key is not configured."));
        }

        if (!Request.Headers.TryGetValue(headerName, out var providedValues) || string.IsNullOrWhiteSpace(providedValues.FirstOrDefault()))
        {
            logger.LogWarning("Invoice PDF upload rejected because API key is missing.");
            return Unauthorized(ErrorBody("missing_api_key", "Thiếu API key.", "API key is required."));
        }

        var providedKey = providedValues.First()!.Trim();
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);
        var providedBytes = Encoding.UTF8.GetBytes(providedKey);
        if (configuredBytes.Length != providedBytes.Length || !CryptographicOperations.FixedTimeEquals(configuredBytes, providedBytes))
        {
            logger.LogWarning("Invoice PDF upload rejected because API key is invalid.");
            return StatusCode(StatusCodes.Status403Forbidden, ErrorBody("invalid_api_key", "API key không hợp lệ.", "API key is invalid."));
        }

        return null;
    }

    private async Task<AuthUserRecord?> GetCurrentUserAsync()
    {
        var userIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(userIdValue, out var userId)
            ? await authService.GetUserByIdAsync(userId, HttpContext.RequestAborted)
            : null;
    }

    private static int? GetAllowedTenantId(AuthUserRecord user)
    {
        return user.IsTenantUser || user.IsShipAdmin || user.IsCrew ? user.TenantId ?? -1 : null;
    }

    private static int? GetAllowedDeviceId(AuthUserRecord user)
    {
        return user.IsShipAdmin || user.IsCrew ? user.DeviceId ?? -1 : null;
    }

    private static bool CanViewIntegrationLogs(AuthUserRecord user)
    {
        return string.Equals(user.Username?.Trim(), "admin", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeDownloadName(string value)
    {
        var fileName = Path.GetFileName(value);
        return string.IsNullOrWhiteSpace(fileName) ? "invoice.pdf" : fileName;
    }

    private ObjectResult Error(InvoicePdfError error)
    {
        return StatusCode(error.StatusCode, ErrorBody(error.ErrorCode, error.Message, error.MessageEn));
    }

    private static object ErrorBody(string errorCode, string message, string messageEn)
    {
        return new
        {
            success = false,
            errorCode,
            message,
            messageEn
        };
    }
}
