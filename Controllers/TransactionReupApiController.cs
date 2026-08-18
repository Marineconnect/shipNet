using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[ApiController]
[Route("api/transaction-reup")]
public sealed class TransactionReupApiController(
    ITransactionReupService transactionReupService,
    IOptions<InvoicePdfIntegrationOptions> integrationOptions,
    ILogger<TransactionReupApiController> logger) : ControllerBase
{
    private readonly InvoicePdfIntegrationOptions integration = integrationOptions.Value;

    [HttpPost("items/{itemId:int}/pdf")]
    [RequestSizeLimit(250_000_000)]
    public async Task<IActionResult> UploadItemPdf(
        int itemId,
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
            var result = await transactionReupService.SaveItemPdfAsync(
                itemId,
                file,
                transactionCode,
                string.IsNullOrWhiteSpace(sourceSystem) ? "InvoiceGenerator" : sourceSystem,
                generatedAt,
                externalReference,
                HttpContext.RequestAborted);

            return Ok(new
            {
                success = true,
                result.ItemId,
                result.InvoiceCode,
                result.TransactionCode,
                result.FileName,
                result.FileSize,
                result.Sha256,
                result.ReceivedAtUtc
            });
        }
        catch (InvalidOperationException exception)
        {
            var code = ExtractErrorCode(exception.Message);
            var status = code switch
            {
                "REUP-INVOICE-NOT-FOUND" => StatusCodes.Status404NotFound,
                "REUP-PDF-MISSING" or "REUP-PDF-CALLBACK-INVALID" or "REUP-PDF-CALLBACK-MISMATCH" => StatusCodes.Status400BadRequest,
                "REUP-PDF-INVALID" => StatusCodes.Status415UnsupportedMediaType,
                _ => StatusCodes.Status500InternalServerError
            };
            return StatusCode(status, ErrorBody(code, exception.GetBaseException().Message));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Transaction Reup PDF callback failed. ItemId={ItemId}.", itemId);
            return StatusCode(StatusCodes.Status500InternalServerError, ErrorBody("REUP-PDF-SAVE-FAILED", exception.GetBaseException().Message));
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
            logger.LogError("Transaction Reup PDF callback API key is not configured.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ErrorBody("api_key_not_configured", "The API key is not configured."));
        }

        if (!Request.Headers.TryGetValue(headerName, out var providedValues) || string.IsNullOrWhiteSpace(providedValues.FirstOrDefault()))
        {
            logger.LogWarning("Transaction Reup PDF callback rejected because API key is missing.");
            return Unauthorized(ErrorBody("missing_api_key", "API key is required."));
        }

        var providedKey = providedValues.First()!.Trim();
        var configuredBytes = Encoding.UTF8.GetBytes(configuredKey);
        var providedBytes = Encoding.UTF8.GetBytes(providedKey);
        if (configuredBytes.Length != providedBytes.Length || !CryptographicOperations.FixedTimeEquals(configuredBytes, providedBytes))
        {
            logger.LogWarning("Transaction Reup PDF callback rejected because API key is invalid.");
            return StatusCode(StatusCodes.Status403Forbidden, ErrorBody("invalid_api_key", "API key is invalid."));
        }

        return null;
    }

    private static string ExtractErrorCode(string message)
    {
        var index = message.IndexOf(':', StringComparison.Ordinal);
        var code = index > 0 ? message[..index].Trim() : message.Trim();
        return string.IsNullOrWhiteSpace(code) ? "REUP-PDF-SAVE-FAILED" : code;
    }

    private static object ErrorBody(string errorCode, string message) => new
    {
        success = false,
        errorCode,
        message
    };
}
