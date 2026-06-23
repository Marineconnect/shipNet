using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

namespace StarlinkDeviceManager.Controllers;

[AllowAnonymous]
public class PaymentsController(
    IConfiguration configuration,
    IPaymentTransactionService paymentTransactionService,
    ILogger<PaymentsController> logger) : Controller
{
    [Authorize]
    [HttpGet]
    public async Task<IActionResult> NinePayQrInfo(int invoiceId)
    {
        return await NinePayBankTransferInfo(invoiceId);
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> NinePayBankTransferInfo(int invoiceId)
    {
        if (invoiceId <= 0)
        {
            return BadRequest(new { success = false, message = "Missing invoice id." });
        }

        try
        {
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
            var createdBy = User.Identity?.Name ?? User.FindFirst("Username")?.Value ?? User.FindFirst("name")?.Value ?? string.Empty;
            var paymentInfo = await paymentTransactionService.CreateNinePayBankTransferInfoAsync(invoiceId, clientIp, createdBy, HttpContext.RequestAborted);
            return Ok(new { success = true, data = paymentInfo });
        }
        catch (Exception exception)
        {
            var baseException = exception.GetBaseException();
            var debugLog = baseException.GetType().GetProperty("DebugLog")?.GetValue(baseException)?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(debugLog))
            {
                debugLog = $"TraceId: {HttpContext.TraceIdentifier}\nException: {baseException.GetType().Name}\nMessage: {baseException.Message}";
            }

            logger.LogError(exception, "Failed to create 9Pay bank transfer info for invoice {InvoiceId}.", invoiceId);
            return BadRequest(new { success = false, message = baseException.Message, debug = debugLog });
        }
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> NinePaySubscriptionQr([FromBody] NinePaySubscriptionQrRequest request)
    {
        if (request.SubscriptionIds.Count == 0)
        {
            return BadRequest(new { success = false, message = "Select at least one subscription." });
        }

        try
        {
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
            var createdBy = User.Identity?.Name ?? User.FindFirst("Username")?.Value ?? User.FindFirst("name")?.Value ?? string.Empty;
            var paymentInfo = await paymentTransactionService.CreateNinePayBankTransferForSubscriptionsAsync(request.SubscriptionIds, clientIp, createdBy, HttpContext.RequestAborted);
            return Ok(new { success = true, data = paymentInfo });
        }
        catch (Exception exception)
        {
            var baseException = exception.GetBaseException();
            var debugLog = baseException.GetType().GetProperty("DebugLog")?.GetValue(baseException)?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(debugLog))
            {
                debugLog = $"TraceId: {HttpContext.TraceIdentifier}\nException: {baseException.GetType().Name}\nMessage: {baseException.Message}";
            }

            logger.LogError(exception, "Failed to create 9Pay QR for subscriptions.");
            return BadRequest(new { success = false, message = baseException.Message, debug = debugLog });
        }
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> NinePaySampleCreateBankTransferTest()
    {
        try
        {
            var result = await paymentTransactionService.RunNinePaySampleCreateBankTransferAsync(HttpContext.RequestAborted);
            return Ok(new { success = true, data = result });
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to run 9Pay sample create-bank-transfer test.");
            return BadRequest(new { success = false, message = exception.GetBaseException().Message });
        }
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> NinePayInvoiceStatus(int invoiceId)
    {
        if (invoiceId <= 0)
        {
            return BadRequest(new { success = false, message = "Missing invoice id." });
        }

        var status = await paymentTransactionService.GetNinePayInvoicePaymentStatusAsync(invoiceId, HttpContext.RequestAborted);
        if (status is null)
        {
            return NotFound(new { success = false, message = "Invoice was not found." });
        }

        return Ok(new { success = true, data = status });
    }

    [Authorize]
    [Authorize]
    [HttpGet]
    public async Task<IActionResult> NinePayQrSessionIpn(int qrSessionId)
    {
        if (!User.HasClaim("UserType", ManagedUserType.Admin))
        {
            return Forbid();
        }

        if (qrSessionId <= 0)
        {
            return BadRequest(new { success = false, message = "Missing QR session id." });
        }

        var detail = await paymentTransactionService.GetNinePayQrSessionIpnDetailAsync(qrSessionId, HttpContext.RequestAborted);
        if (detail is null)
        {
            return NotFound(new { success = false, message = "QR session was not found." });
        }

        return Ok(new { success = true, data = detail });
    }

    [AcceptVerbs("GET", "POST")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> NinePayReturn()
    {
        if (HttpMethods.IsPost(Request.Method))
        {
            logger.LogWarning("9Pay POST received on return URL. Forwarding request to IPN processor for compatibility.");
            return await NinePayIpn();
        }

        var values = Request.Query.ToDictionary(item => item.Key, item => item.Value.ToString());
        logger.LogInformation("9Pay return received with query: {Query}", JsonSerializer.Serialize(values));

        var queryJson = JsonSerializer.Serialize(values);
        var html = $$"""
            <!doctype html>
            <html lang="vi">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>9Pay return</title>
              <style>
                body{font-family:Arial,sans-serif;margin:0;min-height:100vh;display:grid;place-items:center;background:#0f172a;color:#e5eefb}
                main{max-width:360px;padding:24px;text-align:center}
                h1{font-size:20px;margin:0 0 8px}
                p{margin:0;color:#a9b8d0;line-height:1.5}
              </style>
            </head>
            <body>
              <main>
                <h1>Payment returned</h1>
                <p>This popup can be closed. The invoice page will update when 9Pay confirms the bank transfer.</p>
              </main>
              <script>
                const payload = { source: "ninepay", type: "return", query: {{queryJson}} };
                if (window.opener && !window.opener.closed) {
                  window.opener.postMessage(payload, window.location.origin);
                  window.setTimeout(() => window.close(), 700);
                }
              </script>
            </body>
            </html>
            """;

        return Content(html, "text/html; charset=utf-8");
    }

    [AcceptVerbs("GET", "POST")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> NinePayIpn()
    {
        var (result, checksum, source, rawPayload) = await ReadNinePayIpnPayloadAsync();
        logger.LogInformation("9Pay IPN received by {Method} from {Source}. HasResult={HasResult}, HasChecksum={HasChecksum}",
            Request.Method,
            source,
            !string.IsNullOrWhiteSpace(result),
            !string.IsNullOrWhiteSpace(checksum));

        var decodedForLog = TryDecodeNinePayIpnResult(result, out var decodedLogJson, out _);
        var logInvoiceNo = decodedForLog ? GetNinePayIpnLogValue(decodedLogJson, "invoice_no") : string.Empty;
        var logPaymentNo = decodedForLog ? GetNinePayIpnLogValue(decodedLogJson, "payment_no") : string.Empty;
        var logProviderStatus = decodedForLog ? GetNinePayIpnLogValue(decodedLogJson, "status") : string.Empty;

        if (string.IsNullOrWhiteSpace(result) || string.IsNullOrWhiteSpace(checksum))
        {
            logger.LogWarning("9Pay IPN missing result or checksum.");
            await RecordNinePayRawIpnLogAsync(result, checksum, source, rawPayload, logInvoiceNo, logPaymentNo, logProviderStatus, "MissingPayload", "Missing result or checksum.");
            return BadRequest(new { success = false, message = "Missing result or checksum." });
        }

        var checksumKey = configuration["NinePay:ChecksumKey"];
        if (string.IsNullOrWhiteSpace(checksumKey))
        {
            logger.LogWarning("9Pay IPN received but NinePay:ChecksumKey is not configured.");
            await RecordNinePayRawIpnLogAsync(result, checksum, source, rawPayload, logInvoiceNo, logPaymentNo, logProviderStatus, "ConfigurationError", "Checksum key is not configured.");
            return BadRequest(new { success = false, message = "Checksum key is not configured." });
        }

        var checksumResult = FindNinePayChecksumResult(result, checksum, checksumKey);
        if (string.IsNullOrWhiteSpace(checksumResult))
        {
            logger.LogWarning("9Pay IPN checksum mismatch.");
            await RecordNinePayRawIpnLogAsync(result, checksum, source, rawPayload, logInvoiceNo, logPaymentNo, logProviderStatus, "ChecksumInvalid", "IPN checksum mismatch.");
            if (decodedForLog)
            {
                await paymentTransactionService.RecordNinePayIpnAttemptAsync(
                    result,
                    checksum,
                    decodedLogJson,
                    "ChecksumInvalid",
                    "IPN checksum mismatch.",
                    HttpContext.RequestAborted);
            }

            return BadRequest(new { success = false, message = "Invalid checksum." });
        }

        if (!TryDecodeNinePayIpnResult(checksumResult, out var decodedJson, out var decodedResult))
        {
            logger.LogWarning("9Pay IPN result is not valid Base64 JSON. Source={Source}", source);
            await RecordNinePayRawIpnLogAsync(result, checksum, source, rawPayload, logInvoiceNo, logPaymentNo, logProviderStatus, "InvalidPayload", "IPN result is not valid Base64 JSON.");
            return BadRequest(new { success = false, message = "Invalid result payload." });
        }

        logger.LogInformation("9Pay IPN verified: {Result}", decodedResult);
        var processResult = await paymentTransactionService.ProcessNinePayIpnAsync(checksumResult, checksum, decodedJson, HttpContext.RequestAborted);
        await RecordNinePayRawIpnLogAsync(
            checksumResult,
            checksum,
            source,
            rawPayload,
            GetNinePayIpnLogValue(decodedJson, "invoice_no"),
            GetNinePayIpnLogValue(decodedJson, "payment_no"),
            GetNinePayIpnLogValue(decodedJson, "status"),
            processResult.Success ? "Processed" : "ProcessFailed",
            processResult.Message);
        if (!processResult.Success)
        {
            return BadRequest(new
            {
                success = false,
                message = processResult.Message,
                invoice_no = processResult.InvoiceNumber,
                payment_no = processResult.PaymentNo
            });
        }

        return Ok(new
        {
            success = true,
            message = processResult.Message,
            invoice_no = processResult.InvoiceNumber,
            payment_no = processResult.PaymentNo
        });
    }

    [AcceptVerbs("GET", "POST")]
    [IgnoreAntiforgeryToken]
    [ActionName("NinPayIpn")]
    public Task<IActionResult> NinPayIpn()
    {
        return NinePayIpn();
    }

    private static bool TryDecodeNinePayIpnResult(string result, out JsonElement decodedJson, out string decodedResult)
    {
        decodedJson = default;
        decodedResult = string.Empty;
        foreach (var candidate in GetNinePayResultCandidates(result))
        {
            try
            {
                decodedResult = Encoding.UTF8.GetString(Convert.FromBase64String(candidate));
                using var document = JsonDocument.Parse(decodedResult);
                decodedJson = document.RootElement.Clone();
                return true;
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
            }
        }

        return false;
    }

    private static string FindNinePayChecksumResult(string result, string checksum, string checksumKey)
    {
        foreach (var candidate in GetNinePayResultCandidates(result))
        {
            var expectedChecksum = Sha256Uppercase(candidate + checksumKey);
            if (string.Equals(expectedChecksum, checksum, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> GetNinePayResultCandidates(string result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in ExpandNinePayResultCandidate(result))
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> ExpandNinePayResultCandidate(string result)
    {
        foreach (var value in ExpandNinePayResultValue(result))
        {
            yield return value;

            var base64Url = value.Replace('-', '+').Replace('_', '/');
            yield return PadBase64(base64Url);
        }
    }

    private static IEnumerable<string> ExpandNinePayResultValue(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            yield break;
        }

        var trimmed = result.Trim();
        yield return trimmed;

        if (trimmed.Contains(' ', StringComparison.Ordinal))
        {
            yield return trimmed.Replace(' ', '+');
        }

        var unescaped = Uri.UnescapeDataString(trimmed);
        yield return unescaped;

        if (unescaped.Contains(' ', StringComparison.Ordinal))
        {
            yield return unescaped.Replace(' ', '+');
        }
    }

    private static string PadBase64(string value)
    {
        var padding = value.Length % 4;
        return padding == 0 ? value : value.PadRight(value.Length + 4 - padding, '=');
    }

    private async Task RecordNinePayRawIpnLogAsync(
        string result,
        string checksum,
        string source,
        string rawPayload,
        string providerInvoiceNo,
        string paymentNo,
        string providerStatus,
        string processStatus,
        string processMessage)
    {
        await paymentTransactionService.RecordNinePayIpnRequestLogAsync(
            Request.Method,
            Request.Path.Value ?? string.Empty,
            source,
            result,
            checksum,
            rawPayload,
            providerInvoiceNo,
            paymentNo,
            providerStatus,
            processStatus,
            processMessage,
            HttpContext.RequestAborted);
    }

    private static string GetNinePayIpnLogValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.ToString();
    }

    private async Task<(string Result, string Checksum, string Source, string RawPayload)> ReadNinePayIpnPayloadAsync()
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(HttpContext.RequestAborted);
            var rawPayload = JsonSerializer.Serialize(form.ToDictionary(item => item.Key, item => item.Value.ToString()));
            return (form["result"].ToString(), form["checksum"].ToString(), "form", rawPayload);
        }

        var queryResult = Request.Query["result"].ToString();
        var queryChecksum = Request.Query["checksum"].ToString();
        if (!string.IsNullOrWhiteSpace(queryResult) || !string.IsNullOrWhiteSpace(queryChecksum))
        {
            var rawPayload = JsonSerializer.Serialize(Request.Query.ToDictionary(item => item.Key, item => item.Value.ToString()));
            return (queryResult, queryChecksum, "query", rawPayload);
        }

        if (Request.ContentLength.GetValueOrDefault() > 0 &&
            Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            var rawPayload = await reader.ReadToEndAsync(HttpContext.RequestAborted);
            using var json = JsonDocument.Parse(rawPayload);
            var result = json.RootElement.TryGetProperty("result", out var resultElement) ? resultElement.GetString() ?? string.Empty : string.Empty;
            var checksum = json.RootElement.TryGetProperty("checksum", out var checksumElement) ? checksumElement.GetString() ?? string.Empty : string.Empty;
            return (result, checksum, "json", rawPayload);
        }

        return (string.Empty, string.Empty, "empty", string.Empty);
    }

    private static string Sha256Uppercase(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToUpperInvariant();
    }
}
