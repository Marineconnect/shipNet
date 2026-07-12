using System.Data;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public class PaymentTransactionService(
    IConfiguration configuration,
    ICurrencyExchangeService currencyExchangeService,
    ISystemSettingsService systemSettingsService,
    IHttpClientFactory httpClientFactory,
    ITelegramNotificationService telegramNotificationService,
    IInvoiceRabbitMqPublisher invoiceRabbitMqPublisher,
    ILogger<PaymentTransactionService> logger) : IPaymentTransactionService
{
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Missing connection string: DefaultConnection");
    private const string DefaultCurrencySettingCode = "system_default_currency";
    private const string PaymentCurrency = "VND";

    private bool _schemaEnsured;

    public async Task<NinePayQrInfoViewModel> CreateNinePayQrInfoAsync(int invoiceId, string clientIp = "", string createdBy = "", CancellationToken cancellationToken = default)
    {
        var bankTransferInfo = await CreateNinePayBankTransferInfoAsync(invoiceId, clientIp, createdBy, cancellationToken);
        return new NinePayQrInfoViewModel
        {
            InvoiceId = bankTransferInfo.InvoiceId,
            InvoiceNumber = bankTransferInfo.InvoiceNumber,
            OrderTotalUsd = bankTransferInfo.OrderTotalUsd,
            ExchangeRateVndPerUsd = bankTransferInfo.ExchangeRateVndPerUsd,
            AmountVnd = bankTransferInfo.AmountVnd,
            TransactionFeeVnd = bankTransferInfo.TransactionFeeVnd,
            TotalToPayVnd = bankTransferInfo.TotalToPayVnd,
            Currency = bankTransferInfo.Currency,
            BankName = string.Empty,
            PaymentUrl = bankTransferInfo.PaymentUrl,
            QrImageUrl = string.Empty,
            PaymentDate = bankTransferInfo.PaymentDate
        };
    }

    public async Task<NinePayBankTransferInfoViewModel> CreateNinePayBankTransferInfoAsync(int invoiceId, string clientIp = "", string createdBy = "", CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        var invoice = await FindInvoiceByIdAsync(connection, transaction, invoiceId, cancellationToken)
            ?? throw new InvalidOperationException("Invoice was not found.");

        if (!string.Equals(invoice.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invoice '{invoice.InvoiceNumber}' is not pending and cannot be used to create QR.");
        }

        var paymentDate = DateTime.Today;
        var defaultCurrency = await GetSystemDefaultCurrencyAsync(cancellationToken);
        var conversion = await currencyExchangeService.ConvertAsync(new CurrencyConversionFormViewModel
        {
            Amount = invoice.Amount,
            FromCurrency = defaultCurrency,
            ToCurrency = PaymentCurrency,
            ConversionDate = paymentDate
        }, cancellationToken) ?? throw new InvalidOperationException($"Missing active {defaultCurrency} -> {PaymentCurrency} exchange rate for payment date.");

        var settings = await systemSettingsService.GetSettingsByCodesAsync(["ninepay_transaction_fee_vnd"], cancellationToken);
        var transactionFeeVnd = TryParseDecimal(settings.GetValueOrDefault("ninepay_transaction_fee_vnd"), 0);
        var amountVnd = Math.Round(conversion.ConvertedAmount, 0, MidpointRounding.AwayFromZero);
        var totalToPayVnd = amountVnd + transactionFeeVnd;
        var paymentMethod = configuration["NinePay:DirectBankTransfer:PaymentMethod"] ?? configuration["NinePay:PaymentMethod"] ?? "COLLECTION";
        var paymentUrl = BuildNinePayPaymentUrl(invoice.InvoiceNumber, totalToPayVnd, paymentMethod);
        var directPayment = await FindActiveQrSessionAsync(connection, transaction, invoice.InvoiceId, cancellationToken)
            ?? await TryCreateDirectBankTransferAsync(
                connection,
                transaction,
                invoice.InvoiceId,
                invoice.SubscriptionId,
                invoice.InvoiceNumber,
                totalToPayVnd,
                transactionFeeVnd,
                paymentMethod,
                clientIp,
                createdBy,
                cancellationToken);
        if (!string.IsNullOrWhiteSpace(directPayment.PaymentUrl))
        {
            paymentUrl = directPayment.PaymentUrl;
        }

        await UpsertPendingTransactionAsync(
            connection,
            transaction,
            invoice.InvoiceId,
            invoice.SubscriptionId,
            invoice.InvoiceNumber,
            invoice.Amount,
            conversion.Rate,
            amountVnd,
            transactionFeeVnd,
            totalToPayVnd,
            PaymentCurrency,
            paymentMethod,
            paymentUrl,
            directPayment.ProviderPaymentId,
            directPayment.ProviderStatus,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new NinePayBankTransferInfoViewModel
        {
            InvoiceId = invoice.InvoiceId,
            InvoiceNumber = invoice.InvoiceNumber,
            OrderTotalUsd = invoice.Amount,
            ExchangeRateVndPerUsd = conversion.Rate,
            AmountVnd = amountVnd,
            TransactionFeeVnd = transactionFeeVnd,
            TotalToPayVnd = totalToPayVnd,
            Currency = PaymentCurrency,
            PaymentDate = paymentDate,
            PaymentMethod = paymentMethod,
            PaymentUrl = paymentUrl,
            Instructions = directPayment.Banks.Count > 0
                ? "Select a bank below or use the first bank shown by default to complete payment in your banking app."
                : "Open 9Pay to choose a bank and complete the transfer/QR payment in the banking app.",
            ProviderPaymentId = directPayment.ProviderPaymentId,
            ProviderOrderRef = directPayment.ProviderOrderRef,
            QrStartedAt = directPayment.QrStartedAt,
            ExpiresAt = directPayment.ExpiresAt,
            QrStatus = directPayment.QrStatus,
            ReusedQr = directPayment.ReusedQr,
            Banks = directPayment.Banks,
            DebugLog = directPayment.DebugLog
        };
    }

    public async Task<NinePayBankTransferInfoViewModel> CreateNinePayBankTransferForSubscriptionsAsync(IReadOnlyList<int> subscriptionIds, string clientIp = "", string createdBy = "", CancellationToken cancellationToken = default)
    {
        var distinctSubscriptionIds = subscriptionIds.Where(id => id > 0).Distinct().ToList();
        if (distinctSubscriptionIds.Count == 0)
        {
            throw new InvalidOperationException("Select at least one subscription.");
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        var invoices = await FindPendingInvoicesBySubscriptionIdsAsync(connection, transaction, distinctSubscriptionIds, cancellationToken);
        if (invoices.Count == 0)
        {
            throw new InvalidOperationException("Selected subscriptions do not have pending invoices.");
        }
        if (invoices.Select(item => item.TenantId).Distinct().Count() > 1)
        {
            throw new InvalidOperationException("Kh\u00f4ng th\u1ec3 t\u1ea1o QR cho nhi\u1ec1u tenant kh\u00e1c nhau.");
        }

        var paymentDate = DateTime.Today;
        var defaultCurrency = await GetSystemDefaultCurrencyAsync(cancellationToken);
        var totalUsd = invoices.Sum(item => item.Amount);
        var conversion = await currencyExchangeService.ConvertAsync(new CurrencyConversionFormViewModel
        {
            Amount = totalUsd,
            FromCurrency = defaultCurrency,
            ToCurrency = PaymentCurrency,
            ConversionDate = paymentDate
        }, cancellationToken) ?? throw new InvalidOperationException($"Missing active {defaultCurrency} -> {PaymentCurrency} exchange rate for payment date.");

        var settings = await systemSettingsService.GetSettingsByCodesAsync(["ninepay_transaction_fee_vnd"], cancellationToken);
        var transactionFeeVnd = TryParseDecimal(settings.GetValueOrDefault("ninepay_transaction_fee_vnd"), 0);
        var amountVnd = Math.Round(conversion.ConvertedAmount, 0, MidpointRounding.AwayFromZero);
        var totalToPayVnd = amountVnd + transactionFeeVnd;
        var paymentMethod = configuration["NinePay:DirectBankTransfer:PaymentMethod"] ?? configuration["NinePay:PaymentMethod"] ?? "COLLECTION";
        var displayInvoiceNumber = string.Join(", ", invoices.Select(item => item.InvoiceNumber));
        var qrInvoices = invoices.Select(item => new QrInvoiceItem(
            item.InvoiceId,
            item.SubscriptionId,
            item.TenantId,
            item.InvoiceNumber,
            Math.Round(item.Amount * conversion.Rate, 0, MidpointRounding.AwayFromZero))).ToList();

        var directPayment = await FindActiveQrSessionForInvoicesAsync(
            connection,
            transaction,
            qrInvoices.Select(item => item.InvoiceId).ToList(),
            cancellationToken)
            ?? await TryCreateDirectBankTransferAsync(
            connection,
            transaction,
            invoices[0].InvoiceId,
            invoices[0].SubscriptionId,
            displayInvoiceNumber,
            totalToPayVnd,
            transactionFeeVnd,
            paymentMethod,
            clientIp,
            createdBy,
            cancellationToken,
            qrInvoices);

        foreach (var invoice in invoices)
        {
            await UpsertPendingTransactionAsync(
                connection,
                transaction,
                invoice.InvoiceId,
                invoice.SubscriptionId,
                invoice.InvoiceNumber,
                invoice.Amount,
                conversion.Rate,
                Math.Round(invoice.Amount * conversion.Rate, 0, MidpointRounding.AwayFromZero),
                0,
                Math.Round(invoice.Amount * conversion.Rate, 0, MidpointRounding.AwayFromZero),
                PaymentCurrency,
                paymentMethod,
                string.Empty,
                directPayment.ProviderPaymentId,
                directPayment.ProviderStatus,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new NinePayBankTransferInfoViewModel
        {
            InvoiceId = invoices[0].InvoiceId,
            InvoiceNumber = displayInvoiceNumber,
            OrderTotalUsd = totalUsd,
            ExchangeRateVndPerUsd = conversion.Rate,
            AmountVnd = amountVnd,
            TransactionFeeVnd = transactionFeeVnd,
            TotalToPayVnd = totalToPayVnd,
            Currency = PaymentCurrency,
            PaymentDate = paymentDate,
            PaymentMethod = paymentMethod,
            PaymentUrl = directPayment.PaymentUrl,
            Instructions = "Select a bank below and complete the grouped QR payment.",
            ProviderPaymentId = directPayment.ProviderPaymentId,
            ProviderOrderRef = directPayment.ProviderOrderRef,
            QrStartedAt = directPayment.QrStartedAt,
            ExpiresAt = directPayment.ExpiresAt,
            QrStatus = directPayment.QrStatus,
            ReusedQr = directPayment.ReusedQr,
            Banks = directPayment.Banks,
            DebugLog = directPayment.DebugLog
        };
    }

    public async Task<NinePayInvoicePaymentStatusViewModel?> GetNinePayInvoicePaymentStatusAsync(int invoiceId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        const string query = """
            SELECT TOP 1
                i.[ID],
                i.[InvoiceNumber],
                i.[Status] AS [InvoiceStatus],
                i.[PaidAmount],
                i.[ReceiptNumber],
                i.[CompletedAt],
                t.[ProviderPaymentNo],
                t.[ProviderStatus],
                t.[Status] AS [TransactionStatus],
                t.[AmountVnd],
                t.[Currency],
                t.[Method]
            FROM [dbo].[TblSubscriptionInvoice] i
            OUTER APPLY (
                SELECT TOP 1
                    [ProviderPaymentNo],
                    [ProviderStatus],
                    [Status],
                    [AmountVnd],
                    [Currency],
                    [Method]
                FROM [dbo].[TblPaymentTransaction]
                WHERE [Provider] = N'9Pay'
                  AND ([InvoiceId] = i.[ID] OR [InvoiceNumber] = i.[InvoiceNumber])
                ORDER BY [ID] DESC
            ) t
            WHERE i.[ID] = @invoiceId
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var status = new NinePayInvoicePaymentStatusViewModel
        {
            InvoiceId = ReadInt(reader, "ID"),
            InvoiceNumber = reader["InvoiceNumber"]?.ToString() ?? string.Empty,
            InvoiceStatus = reader["InvoiceStatus"]?.ToString() ?? string.Empty,
            PaidAmount = ReadDecimal(reader, "PaidAmount"),
            ReceiptNumber = reader["ReceiptNumber"]?.ToString() ?? string.Empty,
            CompletedAt = reader["CompletedAt"] == DBNull.Value ? null : Convert.ToDateTime(reader["CompletedAt"], CultureInfo.InvariantCulture),
            ProviderPaymentNo = reader["ProviderPaymentNo"]?.ToString() ?? string.Empty,
            ProviderStatus = reader["ProviderStatus"]?.ToString() ?? string.Empty,
            TransactionStatus = reader["TransactionStatus"]?.ToString() ?? string.Empty,
            AmountVnd = ReadDecimal(reader, "AmountVnd"),
            Currency = reader["Currency"]?.ToString() ?? "VND",
            Method = reader["Method"]?.ToString() ?? string.Empty
        };

        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return status;
    }

    public async Task<NinePayQrSessionIpnDetailViewModel?> GetNinePayQrSessionIpnDetailAsync(int qrSessionId, CancellationToken cancellationToken = default)
    {
        if (qrSessionId <= 0)
        {
            return null;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        const string query = """
            SELECT TOP 1
                q.[ID],
                q.[InvoiceNumber],
                q.[ProviderInvoiceNo],
                q.[Status] AS [QrStatus],
                q.[ProviderStatus],
                q.[ProviderPaymentNo],
                COALESCE(NULLIF(q.[IpnPaymentNo], N''), latestLog.[PaymentNo]) AS [IpnPaymentNo],
                COALESCE(q.[IpnReceivedAt], latestLog.[ReceivedAt]) AS [IpnReceivedAt],
                COALESCE(NULLIF(q.[IpnProcessStatus], N''), latestLog.[ProcessStatus]) AS [IpnProcessStatus],
                COALESCE(NULLIF(q.[IpnProcessMessage], N''), latestLog.[ProcessMessage]) AS [IpnProcessMessage],
                COALESCE(NULLIF(q.[IpnChecksum], N''), latestLog.[Checksum]) AS [IpnChecksum],
                COALESCE(NULLIF(q.[IpnResultBase64], N''), latestLog.[ResultBase64]) AS [IpnResultBase64],
                COALESCE(NULLIF(q.[IpnRawJson], N''), latestLog.[RawPayload]) AS [IpnRawJson],
                t.[Status] AS [LatestTransactionStatus],
                t.[ProviderStatus] AS [LatestTransactionProviderStatus],
                t.[FailureReason] AS [LatestTransactionFailureReason],
                t.[Updated_Date] AS [LatestTransactionAt]
            FROM [dbo].[TblNinePayQrSession] q
            OUTER APPLY (
                SELECT TOP 1
                    [ReceivedAt],
                    [PaymentNo],
                    [ProcessStatus],
                    [ProcessMessage],
                    [Checksum],
                    [ResultBase64],
                    [RawPayload]
                FROM [dbo].[TblNinePayIpnLog]
                WHERE [ProviderInvoiceNo] = q.[ProviderInvoiceNo]
                ORDER BY [ID] DESC
            ) latestLog
            OUTER APPLY (
                SELECT TOP 1
                    [Status],
                    [ProviderStatus],
                    [FailureReason],
                    [Updated_Date]
                FROM [dbo].[TblPaymentTransaction]
                WHERE [Provider] = N'9Pay'
                  AND (
                        [ProviderPaymentNo] = q.[ProviderPaymentNo]
                        OR [InvoiceNumber] = q.[ProviderInvoiceNo]
                        OR EXISTS (
                            SELECT 1
                            FROM [dbo].[TblNinePayQrSessionInvoice] qi
                            WHERE qi.[QrSessionId] = q.[ID]
                              AND qi.[InvoiceNumber] = [dbo].[TblPaymentTransaction].[InvoiceNumber]
                        )
                      )
                ORDER BY [ID] DESC
            ) t
            WHERE q.[ID] = @qrSessionId
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@qrSessionId", SqlDbType.Int).Value = qrSessionId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var detail = new NinePayQrSessionIpnDetailViewModel
        {
            QrSessionId = ReadInt(reader, "ID"),
            InvoiceNumber = reader["InvoiceNumber"]?.ToString() ?? string.Empty,
            ProviderInvoiceNo = reader["ProviderInvoiceNo"]?.ToString() ?? string.Empty,
            QrStatus = reader["QrStatus"]?.ToString() ?? string.Empty,
            ProviderStatus = reader["ProviderStatus"]?.ToString() ?? string.Empty,
            ProviderPaymentNo = reader["IpnPaymentNo"]?.ToString() ?? reader["ProviderPaymentNo"]?.ToString() ?? string.Empty,
            IpnReceivedAt = ReadDate(reader, "IpnReceivedAt"),
            IpnProcessStatus = reader["IpnProcessStatus"]?.ToString() ?? string.Empty,
            IpnProcessMessage = reader["IpnProcessMessage"]?.ToString() ?? string.Empty,
            IpnChecksum = reader["IpnChecksum"]?.ToString() ?? string.Empty,
            IpnResultBase64 = reader["IpnResultBase64"]?.ToString() ?? string.Empty,
            IpnRawJson = reader["IpnRawJson"]?.ToString() ?? string.Empty,
            LatestTransactionStatus = reader["LatestTransactionStatus"]?.ToString() ?? string.Empty,
            LatestTransactionProviderStatus = reader["LatestTransactionProviderStatus"]?.ToString() ?? string.Empty,
            LatestTransactionFailureReason = reader["LatestTransactionFailureReason"]?.ToString() ?? string.Empty,
            LatestTransactionAt = ReadDate(reader, "LatestTransactionAt")
        };

        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return detail;
    }

    public async Task RecordNinePayIpnAttemptAsync(
        string resultBase64,
        string checksum,
        JsonElement decodedResult,
        string processStatus,
        string processMessage,
        CancellationToken cancellationToken = default)
    {
        var providerInvoiceNumber = GetString(decodedResult, "invoice_no");
        if (string.IsNullOrWhiteSpace(providerInvoiceNumber))
        {
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        await UpdateQrSessionIpnAttemptAsync(
            connection,
            transaction,
            providerInvoiceNumber,
            GetString(decodedResult, "payment_no"),
            GetString(decodedResult, "status"),
            resultBase64,
            decodedResult.GetRawText(),
            checksum,
            processStatus,
            processMessage,
            DateTime.UtcNow,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordNinePayIpnRequestLogAsync(
        string method,
        string path,
        string source,
        string resultBase64,
        string checksum,
        string rawPayload,
        string providerInvoiceNo,
        string paymentNo,
        string providerStatus,
        string processStatus,
        string processMessage,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        const string query = """
            INSERT INTO [dbo].[TblNinePayIpnLog]
                ([ReceivedAt], [HttpMethod], [Path], [Source], [ProviderInvoiceNo], [PaymentNo], [ProviderStatus],
                 [ProcessStatus], [ProcessMessage], [ResultBase64], [Checksum], [RawPayload])
            VALUES
                (GETUTCDATE(), @method, @path, @source, @providerInvoiceNo, @paymentNo, @providerStatus,
                 @processStatus, @processMessage, @resultBase64, @checksum, @rawPayload);
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@method", SqlDbType.NVarChar, 10).Value = EmptyToDbNull(method);
        command.Parameters.Add("@path", SqlDbType.NVarChar, 300).Value = EmptyToDbNull(path);
        command.Parameters.Add("@source", SqlDbType.NVarChar, 50).Value = EmptyToDbNull(source);
        command.Parameters.Add("@providerInvoiceNo", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(providerInvoiceNo);
        command.Parameters.Add("@paymentNo", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(paymentNo);
        command.Parameters.Add("@providerStatus", SqlDbType.NVarChar, 50).Value = EmptyToDbNull(providerStatus);
        command.Parameters.Add("@processStatus", SqlDbType.NVarChar, 50).Value = EmptyToDbNull(processStatus);
        command.Parameters.Add("@processMessage", SqlDbType.NVarChar, 500).Value = EmptyToDbNull(TrimForLog(processMessage, 500));
        command.Parameters.Add("@resultBase64", SqlDbType.NVarChar, -1).Value = EmptyToDbNull(resultBase64);
        command.Parameters.Add("@checksum", SqlDbType.NVarChar, 200).Value = EmptyToDbNull(checksum);
        command.Parameters.Add("@rawPayload", SqlDbType.NVarChar, -1).Value = EmptyToDbNull(rawPayload);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<NinePaySampleTestResult> RunNinePaySampleCreateBankTransferAsync(CancellationToken cancellationToken = default)
    {
        const string merchantKey = "S9Efjs";
        const string merchantSecretKey = "P9V9PtZlkjOHQqG3bASPUVmjBvIlR116vUk";
        const string endPoint = "https://sand-payment.9pay.vn";

        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var invoiceNo = merchantKey + time;
        var url = $"{endPoint}/api/payments/create-bank-transfer";
        var returnUrl = configuration["NinePay:ReturnUrl"];
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            var applicationBaseUrl = (configuration["NinePay:ApplicationBaseUrl"] ?? "https://localhost:2008").TrimEnd('/');
            returnUrl = $"{applicationBaseUrl}/Payments/NinePayReturn";
        }

        var parameters = new Dictionary<string, object>
        {
            ["merchantKey"] = merchantKey,
            ["invoice_no"] = invoiceNo,
            ["lang"] = "vi",
            ["client_ip"] = "127.0.0.1",
            ["amount"] = 100000,
            ["currency"] = "VND",
            ["method"] = "COLLECTION",
            ["description"] = "ABC",
            ["return_url"] = returnUrl,
            ["expires_time"] = 100
        };

        var message = NinePaySampleMessageBuilder.Instance()
            .With(time, url, "POST")
            .WithParams(parameters)
            .Build();

        var hmac = new NinePaySampleHmacSignature();
        var signature = hmac.Sign(message, merchantSecretKey);

        var headers = new Dictionary<string, string>
        {
            ["Date"] = time,
            ["Authorization"] = $"Signature Algorithm=HS256,Credential={merchantKey},SignedHeaders=,Signature={signature}"
        };

        var result = await CallNinePaySampleApiAsync("POST", url, parameters, headers, cancellationToken);
        var requestPayload = new StringBuilder();
        requestPayload.AppendLine($"URL: {url}");
        requestPayload.AppendLine("METHOD: POST");
        requestPayload.AppendLine("MSG:");
        requestPayload.AppendLine(message);
        requestPayload.AppendLine();
        requestPayload.AppendLine("HEADERS:");
        foreach (var kv in headers)
        {
            requestPayload.AppendLine($"{kv.Key}: {kv.Value}");
        }

        requestPayload.AppendLine();
        requestPayload.AppendLine("FORM:");
        foreach (var kv in parameters)
        {
            requestPayload.AppendLine($"{kv.Key}: {kv.Value}");
        }

        var output = new StringBuilder();
        output.AppendLine("MSG:");
        output.AppendLine(message);
        output.AppendLine();
        output.AppendLine("HEADERS:");
        foreach (var kv in headers)
        {
            output.AppendLine($"{kv.Key}: {kv.Value}");
        }

        output.AppendLine();
        output.AppendLine("REQUEST FORM:");
        foreach (var kv in parameters)
        {
            output.AppendLine($"{kv.Key}: {kv.Value}");
        }

        output.AppendLine();
        output.AppendLine("RESULT:");
        output.AppendLine(result);

        return new NinePaySampleTestResult
        {
            Message = message,
            Url = url,
            Result = result,
            Output = output.ToString(),
            RequestPayload = requestPayload.ToString(),
            ResponsePayload = result
        };
    }

    public async Task<InvoiceRabbitMqPublishResult> SendInvoiceToRabbitMqAsync(
        int invoiceId,
        string transactionCode = "",
        DateTime? paymentTime = null,
        string operatorName = "",
        CancellationToken cancellationToken = default)
    {
        var result = new InvoiceRabbitMqPublishResult();
        void AddLog(string message)
        {
            result.Logs.Add($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | {message}");
        }

        if (!configuration.GetValue("InvoiceRabbitMq:SendInvoiceToRabitMQ", true))
        {
            result.Success = true;
            result.Message = "SendInvoiceToRabitMQ is disabled. Invoice message was not sent.";
            AddLog(result.Message);
            logger.LogInformation("Invoice RabbitMQ publish skipped because SendInvoiceToRabitMQ is disabled. InvoiceId={InvoiceId}.", invoiceId);
            return result;
        }

        AddLog($"STEP 1 - Load invoice payload context. InvoiceId={invoiceId}.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var schemaTransaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            await EnsureSchemaAsync(connection, schemaTransaction, cancellationToken);
            await schemaTransaction.CommitAsync(cancellationToken);
        }

        var context = await GetInvoicePdfPayloadContextAsync(connection, invoiceId, cancellationToken)
            ?? throw new InvalidOperationException($"Invoice {invoiceId} was not found.");

        var bank = ExtractNinePayBankInfo(context.ProviderResponseJson, context.BankAccountNo, context.TransferContent);
        var resolvedTransactionCode = FirstNotEmpty(transactionCode, context.IpnPaymentNo, context.ProviderPaymentNo, context.ReceiptNumber, $"INVOICE-{invoiceId}");
        var resolvedPaymentTime = paymentTime ?? context.CompletedAt ?? context.IpnReceivedAt ?? context.LatestTransactionAt ?? DateTime.UtcNow;
        var resolvedOperatorName = FirstNotEmpty(operatorName, context.UpdatedBy, context.CreatedBy, "system");
        var kitNumber = FirstNotEmpty(context.KitNumber, context.KitId);
        var amountVnd = context.InvoiceAmountVnd > 0
            ? context.InvoiceAmountVnd
            : context.TransactionAmountVnd;
        var generatedInvoiceCode = BuildShipNetInvoiceCode(context);
        var resolvedEmail = FirstNotEmpty(context.TenantEmail, configuration["InvoicePdf:CustomerEmail"], configuration["InvoicePdf:DefaultEmail"]);

        AddLog($"STEP 2 - Build invoice PDF JSON. Invoice={context.InvoiceNumber}; InvoiceCode={generatedInvoiceCode}; Email={FirstNotEmpty(resolvedEmail, "-")}; Company={InvoicePdfSetting("CompanyName", "MLTECH MARINE CONNECT PTE LTD")}; TransactionCode={resolvedTransactionCode}; KitNumber={kitNumber}; AmountVnd={amountVnd:#,##0.##}.");
        var payload = BuildInvoicePdfPayloadJson(context, bank, resolvedTransactionCode, resolvedPaymentTime, resolvedOperatorName, kitNumber, amountVnd);
        AddLog($"STEP 3 - Payload built. Size={Encoding.UTF8.GetByteCount(payload)} bytes.");

        var publishResult = await invoiceRabbitMqPublisher.PublishInvoiceAsync(new InvoiceRabbitMqPublishRequest
        {
            InvoiceJson = payload,
            Username = resolvedOperatorName
        }, cancellationToken);

        result.Success = publishResult.Success;
        result.Message = publishResult.Message;
        result.MessageId = publishResult.MessageId;
        result.Payload = payload;
        result.Logs.AddRange(publishResult.Logs);
        result.Logs.Add($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} | Payload: {payload}");

        if (publishResult.Success)
        {
            logger.LogInformation(
                "Invoice PDF RabbitMQ message sent. InvoiceId={InvoiceId}; InvoiceNumber={InvoiceNumber}; TransactionCode={TransactionCode}; MessageId={MessageId}.",
                invoiceId,
                context.InvoiceNumber,
                resolvedTransactionCode,
                publishResult.MessageId);
        }
        else
        {
            logger.LogError(
                "Invoice PDF RabbitMQ message failed. InvoiceId={InvoiceId}; InvoiceNumber={InvoiceNumber}; TransactionCode={TransactionCode}; Reason={Reason}.",
                invoiceId,
                context.InvoiceNumber,
                resolvedTransactionCode,
                publishResult.Message);
        }

        return result;
    }

    private async Task<InvoicePdfPayloadContext?> GetInvoicePdfPayloadContextAsync(SqlConnection connection, int invoiceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1
                i.[ID] AS [InvoiceId],
                i.[InvoiceNumber],
                YEAR(i.[CreatedAt]) AS [InvoiceYear],
                (
                    SELECT COUNT(1)
                    FROM [dbo].[TblSubscriptionInvoice] yearInv
                    WHERE yearInv.[CreatedAt] >= DATEFROMPARTS(YEAR(i.[CreatedAt]), 1, 1)
                      AND yearInv.[CreatedAt] < DATEFROMPARTS(YEAR(i.[CreatedAt]) + 1, 1, 1)
                      AND (
                          yearInv.[CreatedAt] < i.[CreatedAt]
                          OR (yearInv.[CreatedAt] = i.[CreatedAt] AND yearInv.[ID] <= i.[ID])
                      )
                ) AS [InvoiceSequenceInYear],
                i.[InvoiceType],
                i.[Description],
                i.[ReceiptNumber],
                i.[CompletedAt],
                i.[CreatedAt],
                i.[Created_By],
                i.[Updated_By],
                s.[ID] AS [SubscriptionId],
                s.[DeviceId],
                s.[TenantId],
                s.[TenantName],
                s.[VesselName],
                s.[KitId],
                s.[PlanName],
                s.[StartDate],
                s.[EndDate],
                d.[KITNumber],
                d.[DeviceCode],
                t.[Email] AS [TenantEmail],
                q.[ID] AS [QrSessionId],
                q.[ProviderInvoiceNo],
                q.[ProviderPaymentNo],
                q.[IpnPaymentNo],
                q.[IpnReceivedAt],
                q.[BankAccountNo],
                q.[TransferContent],
                q.[ProviderResponseJson],
                COALESCE(q.[InvoiceAmountVnd], q.[AmountVnd], tx.[AmountVnd], 0) AS [InvoiceAmountVnd],
                tx.[ProviderPaymentNo] AS [TransactionPaymentNo],
                tx.[AmountVnd] AS [TransactionAmountVnd],
                tx.[UpdatedAt] AS [LatestTransactionAt]
            FROM [dbo].[TblSubscriptionInvoice] i
            INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
            LEFT JOIN [dbo].[TblDevices] d ON d.[ID] = s.[DeviceId]
            LEFT JOIN [dbo].[TblTenant] t ON t.[ID] = s.[TenantId]
            OUTER APPLY (
                SELECT TOP 1
                    qs.[ID],
                    qs.[ProviderInvoiceNo],
                    qs.[ProviderPaymentNo],
                    qs.[IpnPaymentNo],
                    qs.[IpnReceivedAt],
                    qs.[BankAccountNo],
                    qs.[TransferContent],
                    qs.[ProviderResponseJson],
                    qs.[AmountVnd],
                    qi.[AmountVnd] AS [InvoiceAmountVnd],
                    qs.[Created_Date]
                FROM [dbo].[TblNinePayQrSession] qs
                LEFT JOIN [dbo].[TblNinePayQrSessionInvoice] qi ON qi.[QrSessionId] = qs.[ID] AND qi.[InvoiceId] = i.[ID]
                WHERE qs.[InvoiceId] = i.[ID]
                   OR qi.[InvoiceId] = i.[ID]
                ORDER BY qs.[Created_Date] DESC, qs.[ID] DESC
            ) q
            OUTER APPLY (
                SELECT TOP 1
                    pt.[ProviderPaymentNo],
                    pt.[AmountVnd],
                    COALESCE(pt.[CompletedAt], pt.[ProviderCreatedAt], pt.[Updated_Date], pt.[Created_Date]) AS [UpdatedAt]
                FROM [dbo].[TblPaymentTransaction] pt
                WHERE pt.[InvoiceId] = i.[ID]
                   OR pt.[InvoiceNumber] = i.[InvoiceNumber]
                ORDER BY COALESCE(pt.[CompletedAt], pt.[ProviderCreatedAt], pt.[Updated_Date], pt.[Created_Date]) DESC, pt.[ID] DESC
            ) tx
            WHERE i.[ID] = @invoiceId;
            """;

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InvoicePdfPayloadContext(
            ReadInt(reader, "InvoiceId"),
            ReadText(reader, "InvoiceNumber"),
            ReadInt(reader, "InvoiceYear"),
            ReadInt(reader, "InvoiceSequenceInYear"),
            ReadText(reader, "InvoiceType"),
            ReadText(reader, "Description"),
            ReadText(reader, "ReceiptNumber"),
            ReadDate(reader, "CompletedAt"),
            ReadDate(reader, "CreatedAt"),
            ReadText(reader, "Created_By"),
            ReadText(reader, "Updated_By"),
            ReadInt(reader, "SubscriptionId"),
            ReadInt(reader, "DeviceId"),
            ReadInt(reader, "TenantId"),
            ReadText(reader, "TenantName"),
            ReadText(reader, "VesselName"),
            ReadText(reader, "KitId"),
            ReadText(reader, "PlanName"),
            ReadDate(reader, "StartDate"),
            ReadDate(reader, "EndDate"),
            ReadText(reader, "KITNumber"),
            ReadText(reader, "DeviceCode"),
            ReadText(reader, "TenantEmail"),
            ReadInt(reader, "QrSessionId"),
            ReadText(reader, "ProviderInvoiceNo"),
            ReadText(reader, "ProviderPaymentNo"),
            ReadText(reader, "IpnPaymentNo"),
            ReadDate(reader, "IpnReceivedAt"),
            ReadText(reader, "BankAccountNo"),
            ReadText(reader, "TransferContent"),
            ReadText(reader, "ProviderResponseJson"),
            ReadDecimal(reader, "InvoiceAmountVnd"),
            ReadText(reader, "TransactionPaymentNo"),
            ReadDecimal(reader, "TransactionAmountVnd"),
            ReadDate(reader, "LatestTransactionAt"));
    }

    private string BuildInvoicePdfPayloadJson(
        InvoicePdfPayloadContext context,
        InvoicePdfBankInfo bank,
        string transactionCode,
        DateTime paymentTime,
        string operatorName,
        string kitNumber,
        decimal amountVnd)
    {
        var startDate = context.StartDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
        var endDate = context.EndDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
        var invoiceCode = BuildShipNetInvoiceCode(context);
        var email = FirstNotEmpty(context.TenantEmail, InvoicePdfSetting("CustomerEmail", string.Empty), InvoicePdfSetting("DefaultEmail", "admin@shipnet.vn"));
        var titlePeriod = string.IsNullOrWhiteSpace(startDate) || string.IsNullOrWhiteSpace(endDate)
            ? context.PlanName
            : $"{context.PlanName} ({startDate} - {endDate})";

        var payload = new
        {
            transactionCode,
            invoiceCode,
            source = "SHIPNET",
            paymentTime = FormatIsoUtc(paymentTime),
            operatorName,
            email = EmptyToNull(email),
            invoiceParams = new
            {
                LogoUrl = InvoicePdfSetting("LogoUrl", string.Empty),
                CompanyName = InvoicePdfSetting("CompanyName", "MLTECH MARINE CONNECT PTE LTD"),
                CompanyAddressLine1 = InvoicePdfSetting("CompanyAddressLine1", "Address: 18 Sin Ming Lane, #07-13, Midview City,"),
                CompanyAddressLine2 = InvoicePdfSetting("CompanyAddressLine2", "Singapore- 573960, Singapore"),
                CompanyEmail = InvoicePdfSetting("CompanyEmail", "Email: admin@marineconnect.sg"),
                ContactNote = InvoicePdfSetting("ContactNote", "If you have any questions regarding this invoice, please contact us."),
                PaymentTitle = InvoicePdfSetting("PaymentTitle", "PAYMENT INSTRUCTIONS:"),
                BankAccountNumber = FirstNotEmpty(bank.BankAccountNo, InvoicePdfSetting("BankAccountNumber", string.Empty)),
                BeneficiaryName = FirstNotEmpty(bank.BankAccountName, InvoicePdfSetting("BeneficiaryName", string.Empty)),
                BankName = FirstNotEmpty(bank.BankName, InvoicePdfSetting("BankName", string.Empty)),
                SwiftCode = FirstNotEmpty(bank.SwiftCode, InvoicePdfSetting("SwiftCode", string.Empty)),
                BankAddressLine1 = FirstNotEmpty(bank.BankAddressLine1, InvoicePdfSetting("BankAddressLine1", string.Empty)),
                BankAddressLine2 = FirstNotEmpty(bank.BankAddressLine2, InvoicePdfSetting("BankAddressLine2", string.Empty))
            },
            vessels = new[]
            {
                new
                {
                    vesselId = context.DeviceId > 0 ? context.DeviceId.ToString(CultureInfo.InvariantCulture) : context.SubscriptionId.ToString(CultureInfo.InvariantCulture),
                    vesselName = context.VesselName,
                    kit_id = kitNumber,
                    subscriptions = new[]
                    {
                        new
                        {
                            type = NormalizeInvoiceType(context.InvoiceType),
                            title = titlePeriod,
                            subTitles = new[]
                            {
                                $"Terminal ID: {FirstNotEmpty(context.KitId, context.DeviceCode)}",
                                $"KIT Code: {kitNumber}"
                            },
                            price = amountVnd,
                            start_time = startDate,
                            end_time = endDate,
                            kit_id = kitNumber
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    private static InvoicePdfBankInfo ExtractNinePayBankInfo(string providerResponseJson, string bankAccountNoFallback, string transferContentFallback)
    {
        if (string.IsNullOrWhiteSpace(providerResponseJson))
        {
            return new InvoicePdfBankInfo(bankAccountNoFallback, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, transferContentFallback);
        }

        try
        {
            using var json = JsonDocument.Parse(providerResponseJson);
            JsonElement banksElement = default;
            if ((!TryFindPropertyRecursive(json.RootElement, "list_bank_info", out banksElement) || banksElement.ValueKind != JsonValueKind.Array)
                && (!TryFindPropertyRecursive(json.RootElement, "banks", out banksElement) || banksElement.ValueKind != JsonValueKind.Array))
            {
                banksElement = default;
            }

            InvoicePdfBankInfo? firstBank = null;
            if (banksElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var bankElement in banksElement.EnumerateArray())
                {
                    var bank = new InvoicePdfBankInfo(
                        GetJsonStringAny(bankElement, "bank_account_no", "bankAccountNo", "account_no", "accountNo", "va_number", "vaNumber"),
                        GetJsonStringAny(bankElement, "bank_account_name", "bankAccountName", "account_name", "accountName", "beneficiary_name", "beneficiaryName"),
                        GetJsonStringAny(bankElement, "bank_name", "bankName", "name"),
                        GetJsonStringAny(bankElement, "swift_code", "swiftCode", "swift", "bank_swift_code", "bankSwiftCode"),
                        GetJsonStringAny(bankElement, "bank_address_line1", "bankAddressLine1", "bank_address_1", "address_line1", "addressLine1"),
                        GetJsonStringAny(bankElement, "bank_address_line2", "bankAddressLine2", "bank_address_2", "address_line2", "addressLine2"),
                        GetJsonStringAny(bankElement, "remark", "content", "description", "plaintext"));

                    firstBank ??= bank;
                    if (!string.IsNullOrWhiteSpace(bankAccountNoFallback) &&
                        string.Equals(bank.BankAccountNo, bankAccountNoFallback, StringComparison.OrdinalIgnoreCase))
                    {
                        return bank with
                        {
                            TransferContent = FirstNotEmpty(bank.TransferContent, transferContentFallback)
                        };
                    }
                }
            }

            if (firstBank is not null)
            {
                return firstBank with
                {
                    BankAccountNo = FirstNotEmpty(firstBank.BankAccountNo, bankAccountNoFallback),
                    TransferContent = FirstNotEmpty(firstBank.TransferContent, transferContentFallback)
                };
            }
        }
        catch
        {
        }

        return new InvoicePdfBankInfo(bankAccountNoFallback, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, transferContentFallback);
    }

    private static string NormalizeInvoiceType(string invoiceType)
    {
        return string.Equals(invoiceType, "SUBSCRIPTION", StringComparison.OrdinalIgnoreCase)
            ? "subscription"
            : invoiceType.ToLowerInvariant();
    }

    private string InvoicePdfSetting(string key, string fallback)
    {
        var value = configuration[$"InvoicePdf:{key}"];
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string BuildShipNetInvoiceCode(InvoicePdfPayloadContext context)
    {
        var year = context.InvoiceYear > 0
            ? context.InvoiceYear
            : (context.CreatedAt ?? DateTime.UtcNow).Year;
        var sequence = context.InvoiceSequenceInYear > 0
            ? context.InvoiceSequenceInYear
            : context.InvoiceId;
        return $"SHIPNET-INV-{year}-{sequence:00000}";
    }

    private static string FormatIsoUtc(DateTime value)
    {
        var utcValue = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime();
        return utcValue.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static string FirstNotEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public async Task<NinePayIpnProcessResult> ProcessNinePayIpnAsync(
        string resultBase64,
        string checksum,
        JsonElement decodedResult,
        CancellationToken cancellationToken = default)
    {
        var providerInvoiceNumber = GetString(decodedResult, "invoice_no");
        var paymentNo = GetString(decodedResult, "payment_no");
        var currency = GetString(decodedResult, "currency");
        var method = GetString(decodedResult, "method");
        var description = GetString(decodedResult, "description");
        var failureReason = GetString(decodedResult, "failure_reason");
        var providerStatus = GetString(decodedResult, "status");
        var amountVnd = GetDecimal(decodedResult, "amount");
        var providerCreatedAt = GetDate(decodedResult, "created_at");
        var rawJson = decodedResult.GetRawText();

        if (string.IsNullOrWhiteSpace(providerInvoiceNumber))
        {
            await SendUnmatchedNinePayIpnTelegramNotificationAsync(
                string.Empty,
                paymentNo,
                providerStatus,
                amountVnd,
                cancellationToken);

            return new NinePayIpnProcessResult
            {
                Success = false,
                Message = "IPN payload does not contain invoice_no."
            };
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);

        await UpdateQrSessionIpnAttemptAsync(
            connection,
            transaction,
            providerInvoiceNumber,
            paymentNo,
            providerStatus,
            resultBase64,
            rawJson,
            checksum,
            "Received",
            "IPN checksum is valid and payload was decoded.",
            DateTime.UtcNow,
            cancellationToken);

        var mappedInvoices = await FindInvoicesByQrProviderInvoiceAsync(connection, transaction, providerInvoiceNumber, cancellationToken);
        if (mappedInvoices.Count == 0)
        {
            var directInvoice = await FindInvoiceAsync(connection, transaction, providerInvoiceNumber, cancellationToken);
            if (directInvoice.HasValue)
            {
                mappedInvoices.Add((directInvoice.Value.InvoiceId, directInvoice.Value.SubscriptionId, providerInvoiceNumber, directInvoice.Value.InvoiceAmount));
            }
        }

        if (mappedInvoices.Count == 0)
        {
            await UpsertTransactionAsync(
                connection,
                transaction,
                null,
                null,
                providerInvoiceNumber,
                paymentNo,
                providerStatus,
                "unmatched",
                amountVnd,
                currency,
                method,
                description,
                failureReason,
                resultBase64,
                rawJson,
                checksum,
                providerCreatedAt,
                null,
                cancellationToken);

            await UpdateQrSessionIpnAttemptAsync(
                connection,
                transaction,
                providerInvoiceNumber,
                paymentNo,
                providerStatus,
                resultBase64,
                rawJson,
                checksum,
                "Unmatched",
                "Invoice was not found for IPN invoice_no.",
                DateTime.UtcNow,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            await SendUnmatchedNinePayIpnTelegramNotificationAsync(
                providerInvoiceNumber,
                paymentNo,
                providerStatus,
                amountVnd,
                cancellationToken);

            return new NinePayIpnProcessResult
            {
                Success = true,
                InvoiceNumber = providerInvoiceNumber,
                PaymentNo = paymentNo,
                Message = "Thanh toán không thông qua shipNet."
            };
        }

        var isPaidStatus = IsNinePayPaidStatus(providerStatus);
        DateTime? completedAt = isPaidStatus ? providerCreatedAt ?? DateTime.UtcNow : null;
        foreach (var invoice in mappedInvoices)
        {
            await UpsertTransactionAsync(
                connection,
                transaction,
                invoice.InvoiceId,
                invoice.SubscriptionId,
                invoice.InvoiceNumber,
                paymentNo,
                providerStatus,
                isPaidStatus ? "paid" : "pending",
                amountVnd,
                currency,
                method,
                description,
                failureReason,
                resultBase64,
                rawJson,
                checksum,
                providerCreatedAt,
                completedAt,
                cancellationToken);
        }

        if (isPaidStatus && completedAt.HasValue)
        {
            foreach (var invoice in mappedInvoices)
            {
                await MarkInvoicePaidAsync(connection, transaction, (invoice.InvoiceId, invoice.SubscriptionId, invoice.InvoiceAmount), paymentNo, completedAt.Value, cancellationToken);
                await RecalculateSubscriptionTotalsAsync(connection, transaction, invoice.SubscriptionId, cancellationToken);
                await InsertAuditAsync(connection, transaction, invoice.SubscriptionId, $"9Pay IPN paid invoice '{invoice.InvoiceNumber}' with payment_no '{paymentNo}'.", cancellationToken);
            }

            await MarkQrSessionPaidAsync(connection, transaction, providerInvoiceNumber, paymentNo, providerStatus, rawJson, completedAt.Value, cancellationToken);
        }
        else
        {
            await UpdateQrSessionIpnAttemptAsync(
                connection,
                transaction,
                providerInvoiceNumber,
                paymentNo,
                providerStatus,
                resultBase64,
                rawJson,
                checksum,
                "NonPaid",
                $"IPN received with non-paid status '{providerStatus}'.",
                DateTime.UtcNow,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var invoiceNumber = string.Join(", ", mappedInvoices.Select(item => item.InvoiceNumber));
        var processMessage = isPaidStatus ? "IPN processed and invoice marked paid." : $"IPN received with non-paid status '{providerStatus}'.";
        logger.LogInformation("9Pay IPN processed invoice {InvoiceNumber}, payment_no {PaymentNo}, status {Status}.", invoiceNumber, paymentNo, providerStatus);
        await SendNinePayIpnTelegramNotificationAsync(
            mappedInvoices.Select(item => item.SubscriptionId).Distinct().ToList(),
            providerInvoiceNumber,
            paymentNo,
            providerStatus,
            amountVnd,
            isPaidStatus ? "Paid" : "Not paid",
            processMessage,
            cancellationToken);

        if (isPaidStatus)
        {
            foreach (var invoice in mappedInvoices)
            {
                var publishResult = await SendInvoiceToRabbitMqAsync(
                    invoice.InvoiceId,
                    paymentNo,
                    completedAt,
                    "9Pay IPN",
                    cancellationToken);
                if (!publishResult.Success)
                {
                    logger.LogError(
                        "9Pay IPN was processed but invoice PDF RabbitMQ publish failed. InvoiceId={InvoiceId}; InvoiceNumber={InvoiceNumber}; PaymentNo={PaymentNo}; Reason={Reason}.",
                        invoice.InvoiceId,
                        invoice.InvoiceNumber,
                        paymentNo,
                        publishResult.Message);
                }
            }
        }

        return new NinePayIpnProcessResult
        {
            Success = true,
            InvoiceNumber = invoiceNumber,
            PaymentNo = paymentNo,
            Message = processMessage
        };
    }

    private async Task SendNinePayIpnTelegramNotificationAsync(
        IReadOnlyList<int>? subscriptionIds,
        string providerInvoiceNumber,
        string paymentNo,
        string providerStatus,
        decimal amountVnd,
        string paymentStatus,
        string processMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            var details = subscriptionIds is { Count: > 0 }
                ? await GetTelegramSubscriptionDetailsAsync(subscriptionIds, cancellationToken)
                : [];

            if (details.Count == 0)
            {
                var message = BuildNinePayIpnTelegramMessage(
                    null,
                    providerInvoiceNumber,
                    paymentNo,
                    providerStatus,
                    amountVnd,
                    paymentStatus,
                    processMessage);
                await telegramNotificationService.SendMessageAsync(message, cancellationToken);
                return;
            }

            foreach (var detail in details)
            {
                var message = BuildNinePayIpnTelegramMessage(
                    detail,
                    providerInvoiceNumber,
                    paymentNo,
                    providerStatus,
                    amountVnd,
                    paymentStatus,
                    processMessage);
                await telegramNotificationService.SendMessageAsync(message, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to send Telegram notification for 9Pay IPN {ProviderInvoiceNo}.", providerInvoiceNumber);
        }
    }

    private async Task SendUnmatchedNinePayIpnTelegramNotificationAsync(
        string providerInvoiceNumber,
        string paymentNo,
        string providerStatus,
        decimal amountVnd,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = BuildUnmatchedNinePayIpnTelegramMessage(providerInvoiceNumber, paymentNo, providerStatus, amountVnd);
            await telegramNotificationService.SendMessageAsync(message, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to send unmatched Telegram notification for 9Pay IPN {ProviderInvoiceNo}.", providerInvoiceNumber);
        }
    }

    private async Task<List<TelegramSubscriptionNotificationDetail>> GetTelegramSubscriptionDetailsAsync(
        IReadOnlyList<int> subscriptionIds,
        CancellationToken cancellationToken)
    {
        var distinctIds = subscriptionIds.Where(id => id > 0).Distinct().ToList();
        if (distinctIds.Count == 0)
        {
            return [];
        }

        var parameterNames = distinctIds.Select((_, index) => $"@id{index}").ToList();
        var query = $"""
            SELECT
                s.[ID],
                s.[TenantName],
                s.[DeviceId],
                s.[KitId],
                s.[PlanName],
                s.[StartDate],
                s.[EndDate],
                s.[TotalInvoiceAmount],
                d.[DeviceCode]
            FROM [dbo].[TblMonthlySubscription] s
            LEFT JOIN [dbo].[TblDevices] d ON d.[ID] = s.[DeviceId]
            WHERE s.[ID] IN ({string.Join(",", parameterNames)})
            ORDER BY s.[ID]
            """;

        var details = new List<TelegramSubscriptionNotificationDetail>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);
        for (var index = 0; index < distinctIds.Count; index++)
        {
            command.Parameters.Add(parameterNames[index], SqlDbType.Int).Value = distinctIds[index];
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            details.Add(new TelegramSubscriptionNotificationDetail(
                ReadInt(reader, "ID"),
                reader["TenantName"]?.ToString() ?? string.Empty,
                ReadInt(reader, "DeviceId"),
                reader["DeviceCode"]?.ToString() ?? string.Empty,
                reader["KitId"]?.ToString() ?? string.Empty,
                reader["PlanName"]?.ToString() ?? string.Empty,
                ReadDate(reader, "StartDate") ?? DateTime.MinValue,
                ReadDate(reader, "EndDate") ?? DateTime.MinValue,
                ReadDecimal(reader, "TotalInvoiceAmount")));
        }

        return details;
    }

    private string BuildNinePayIpnTelegramMessage(
        TelegramSubscriptionNotificationDetail? detail,
        string providerInvoiceNumber,
        string paymentNo,
        string providerStatus,
        decimal amountVnd,
        string paymentStatus,
        string processMessage)
    {
        var subscriptionUrl = detail is null ? "-" : BuildSubscriptionUrl(detail.SubscriptionId);
        var deviceCode = detail is null
            ? "-"
            : string.IsNullOrWhiteSpace(detail.DeviceCode) ? detail.DeviceId.ToString(CultureInfo.InvariantCulture) : detail.DeviceCode;
        var billingPeriod = detail is null || detail.StartDate == DateTime.MinValue || detail.EndDate == DateTime.MinValue
            ? "-"
            : $"{detail.StartDate:dd-MM-yyyy} to {detail.EndDate:dd-MM-yyyy}";
        var totalAmount = detail is null
            ? $"{amountVnd:#,##0} VND"
            : $"$ {detail.TotalInvoiceAmount:#,##0.##}";

        var lines = new[]
        {
            "<b>9Pay IPN payment notification</b>",
            $"Mã KIT: {Html(detail?.KitId)}",
            $"Mã thiết bị: {Html(deviceCode)}",
            $"Plan: {Html(detail?.PlanName)}",
            $"Tenant: {Html(detail?.TenantName)}",
            $"Billing Period: {Html(billingPeriod)}",
            $"Total amount: {Html(totalAmount)}",
            $"Url tới subscription: {Html(subscriptionUrl)}",
            $"Tình trạng thanh toán: {Html(paymentStatus)}",
            $"Provider invoice: {Html(providerInvoiceNumber)}",
            $"Payment no: {Html(paymentNo)}",
            $"Provider status: {Html(providerStatus)}",
            $"Message: {Html(processMessage)}"
        };

        return string.Join("\n", lines);
    }

    private static string BuildUnmatchedNinePayIpnTelegramMessage(
        string providerInvoiceNumber,
        string paymentNo,
        string providerStatus,
        decimal amountVnd)
    {
        var paymentStatus = IsNinePayPaidStatus(providerStatus) ? "Success" : "Failed";
        var lines = new[]
        {
            "<b>9Pay IPN payment notification</b>",
            $"Total amount: {Html($"{amountVnd:#,##0} VND")}",
            $"Tình trạng thanh toán: {Html(paymentStatus)}",
            $"Provider invoice: {Html(providerInvoiceNumber)}",
            $"Payment no: {Html(paymentNo)}",
            $"Provider status: {Html(providerStatus)}",
            "Message: Thanh toán không thông qua shipNet"
        };

        return string.Join("\n", lines);
    }

    private string BuildSubscriptionUrl(int subscriptionId)
    {
        var configuredBaseUrl = configuration["Telegram:SubscriptionBaseUrl"];
        if (string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            var applicationBaseUrl = (configuration["NinePay:ApplicationBaseUrl"] ?? "https://portal.shipnetsolution.com").TrimEnd('/');
            configuredBaseUrl = $"{applicationBaseUrl}/MonthlySubscription/Details";
        }

        return $"{configuredBaseUrl.TrimEnd('/')}/{subscriptionId}";
    }

    private static string Html(string? value)
    {
        return WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }

    private async Task EnsureSchemaAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        if (_schemaEnsured)
        {
            return;
        }

        const string query = """
            IF OBJECT_ID(N'[dbo].[TblPaymentTransaction]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblPaymentTransaction](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblPaymentTransaction] PRIMARY KEY,
                    [Provider] nvarchar(50) NOT NULL,
                    [InvoiceId] int NULL,
                    [SubscriptionId] int NULL,
                    [InvoiceNumber] nvarchar(100) NOT NULL,
                    [ProviderPaymentNo] nvarchar(100) NULL,
                    [ProviderStatus] nvarchar(50) NULL,
                    [Status] nvarchar(50) NOT NULL,
                    [OrderAmountUsd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_OrderAmountUsd] DEFAULT(0),
                    [ExchangeRateVndPerUsd] decimal(18,6) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_ExchangeRateVndPerUsd] DEFAULT(0),
                    [ConvertedAmountVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_ConvertedAmountVnd] DEFAULT(0),
                    [TransactionFeeVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_TransactionFeeVnd] DEFAULT(0),
                    [AmountVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_AmountVnd] DEFAULT(0),
                    [PaymentUrl] nvarchar(max) NULL,
                    [Currency] nvarchar(10) NULL,
                    [Method] nvarchar(50) NULL,
                    [Description] nvarchar(500) NULL,
                    [FailureReason] nvarchar(500) NULL,
                    [RawResultBase64] nvarchar(max) NULL,
                    [RawResultJson] nvarchar(max) NULL,
                    [RawChecksum] nvarchar(200) NULL,
                    [ChecksumValid] bit NOT NULL CONSTRAINT [DF_TblPaymentTransaction_ChecksumValid] DEFAULT(1),
                    [ProviderCreatedAt] datetime NULL,
                    [CompletedAt] datetime NULL,
                    [Created_Date] datetime NOT NULL CONSTRAINT [DF_TblPaymentTransaction_Created_Date] DEFAULT(GETDATE()),
                    [Updated_Date] datetime NOT NULL CONSTRAINT [DF_TblPaymentTransaction_Updated_Date] DEFAULT(GETDATE())
                );
            END;

            IF COL_LENGTH(N'[dbo].[TblPaymentTransaction]', N'OrderAmountUsd') IS NULL
                ALTER TABLE [dbo].[TblPaymentTransaction] ADD [OrderAmountUsd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_OrderAmountUsd_Alter] DEFAULT(0);
            IF COL_LENGTH(N'[dbo].[TblPaymentTransaction]', N'ExchangeRateVndPerUsd') IS NULL
                ALTER TABLE [dbo].[TblPaymentTransaction] ADD [ExchangeRateVndPerUsd] decimal(18,6) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_ExchangeRateVndPerUsd_Alter] DEFAULT(0);
            IF COL_LENGTH(N'[dbo].[TblPaymentTransaction]', N'ConvertedAmountVnd') IS NULL
                ALTER TABLE [dbo].[TblPaymentTransaction] ADD [ConvertedAmountVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_ConvertedAmountVnd_Alter] DEFAULT(0);
            IF COL_LENGTH(N'[dbo].[TblPaymentTransaction]', N'TransactionFeeVnd') IS NULL
                ALTER TABLE [dbo].[TblPaymentTransaction] ADD [TransactionFeeVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblPaymentTransaction_TransactionFeeVnd_Alter] DEFAULT(0);
            IF COL_LENGTH(N'[dbo].[TblPaymentTransaction]', N'PaymentUrl') IS NULL
                ALTER TABLE [dbo].[TblPaymentTransaction] ADD [PaymentUrl] nvarchar(max) NULL;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_TblPaymentTransaction_ProviderPaymentNo'
                  AND [object_id] = OBJECT_ID(N'[dbo].[TblPaymentTransaction]')
            )
            BEGIN
                CREATE INDEX [IX_TblPaymentTransaction_ProviderPaymentNo]
                ON [dbo].[TblPaymentTransaction]([Provider], [ProviderPaymentNo]);
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_TblPaymentTransaction_InvoiceNumber'
                  AND [object_id] = OBJECT_ID(N'[dbo].[TblPaymentTransaction]')
            )
            BEGIN
                CREATE INDEX [IX_TblPaymentTransaction_InvoiceNumber]
                ON [dbo].[TblPaymentTransaction]([Provider], [InvoiceNumber]);
            END;

            IF OBJECT_ID(N'[dbo].[TblNinePayIpnLog]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblNinePayIpnLog](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblNinePayIpnLog] PRIMARY KEY,
                    [ReceivedAt] datetime NOT NULL CONSTRAINT [DF_TblNinePayIpnLog_ReceivedAt] DEFAULT(GETUTCDATE()),
                    [HttpMethod] nvarchar(10) NULL,
                    [Path] nvarchar(300) NULL,
                    [Source] nvarchar(50) NULL,
                    [ProviderInvoiceNo] nvarchar(100) NULL,
                    [PaymentNo] nvarchar(100) NULL,
                    [ProviderStatus] nvarchar(50) NULL,
                    [ProcessStatus] nvarchar(50) NULL,
                    [ProcessMessage] nvarchar(500) NULL,
                    [ResultBase64] nvarchar(max) NULL,
                    [Checksum] nvarchar(200) NULL,
                    [RawPayload] nvarchar(max) NULL
                );
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_TblNinePayIpnLog_ProviderInvoiceNo'
                  AND [object_id] = OBJECT_ID(N'[dbo].[TblNinePayIpnLog]')
            )
            BEGIN
                CREATE INDEX [IX_TblNinePayIpnLog_ProviderInvoiceNo]
                ON [dbo].[TblNinePayIpnLog]([ProviderInvoiceNo], [ReceivedAt]);
            END;

            IF OBJECT_ID(N'[dbo].[TblNinePayQrSession]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblNinePayQrSession](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblNinePayQrSession] PRIMARY KEY,
                    [InvoiceId] int NOT NULL,
                    [SubscriptionId] int NOT NULL,
                    [InvoiceNumber] nvarchar(100) NOT NULL,
                    [ProviderInvoiceNo] nvarchar(100) NOT NULL,
                    [ProviderPaymentNo] nvarchar(100) NULL,
                    [ProviderStatus] nvarchar(50) NULL,
                    [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblNinePayQrSession_Status] DEFAULT(N'Pending'),
                    [AmountVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblNinePayQrSession_AmountVnd] DEFAULT(0),
                    [Currency] nvarchar(10) NULL,
                    [Method] nvarchar(50) NULL,
                    [Description] nvarchar(500) NULL,
                    [Channel] nvarchar(50) NULL,
                    [Created_By] nvarchar(100) NULL,
                    [TransferFeeVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblNinePayQrSession_TransferFeeVnd] DEFAULT(0),
                    [BankAccountNo] nvarchar(100) NULL,
                    [TransferContent] nvarchar(500) NULL,
                    [IpnPaymentNo] nvarchar(100) NULL,
                    [IpnReceivedAt] datetime NULL,
                    [IpnProcessStatus] nvarchar(50) NULL,
                    [IpnProcessMessage] nvarchar(500) NULL,
                    [IpnChecksum] nvarchar(200) NULL,
                    [IpnResultBase64] nvarchar(max) NULL,
                    [IpnRawJson] nvarchar(max) NULL,
                    [PaidAt] datetime NULL,
                    [QrStartedAt] datetime NOT NULL,
                    [QrExpiresAt] datetime NOT NULL,
                    [ProviderResponseJson] nvarchar(max) NULL,
                    [DebugLog] nvarchar(max) NULL,
                    [Created_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSession_Created_Date] DEFAULT(GETDATE()),
                    [Updated_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSession_Updated_Date] DEFAULT(GETDATE())
                );
            END;

            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'Channel') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [Channel] nvarchar(50) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'Created_By') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [Created_By] nvarchar(100) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'TransferFeeVnd') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [TransferFeeVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblNinePayQrSession_TransferFeeVnd_Existing] DEFAULT(0);
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'BankAccountNo') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [BankAccountNo] nvarchar(100) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'TransferContent') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [TransferContent] nvarchar(500) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnPaymentNo') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnPaymentNo] nvarchar(100) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnReceivedAt') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnReceivedAt] datetime NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnProcessStatus') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnProcessStatus] nvarchar(50) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnProcessMessage') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnProcessMessage] nvarchar(500) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnChecksum') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnChecksum] nvarchar(200) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnResultBase64') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnResultBase64] nvarchar(max) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'IpnRawJson') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [IpnRawJson] nvarchar(max) NULL;
            IF COL_LENGTH(N'[dbo].[TblNinePayQrSession]', N'PaidAt') IS NULL
                ALTER TABLE [dbo].[TblNinePayQrSession] ADD [PaidAt] datetime NULL;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_TblNinePayQrSession_Invoice_Active'
                  AND [object_id] = OBJECT_ID(N'[dbo].[TblNinePayQrSession]')
            )
            BEGIN
                CREATE INDEX [IX_TblNinePayQrSession_Invoice_Active]
                ON [dbo].[TblNinePayQrSession]([InvoiceId], [Status], [QrExpiresAt]);
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_TblNinePayQrSession_ProviderInvoiceNo'
                  AND [object_id] = OBJECT_ID(N'[dbo].[TblNinePayQrSession]')
            )
            BEGIN
                CREATE INDEX [IX_TblNinePayQrSession_ProviderInvoiceNo]
                ON [dbo].[TblNinePayQrSession]([ProviderInvoiceNo]);
            END;

            IF OBJECT_ID(N'[dbo].[TblNinePayQrSessionInvoice]', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[TblNinePayQrSessionInvoice](
                    [ID] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_TblNinePayQrSessionInvoice] PRIMARY KEY,
                    [QrSessionId] int NOT NULL,
                    [InvoiceId] int NOT NULL,
                    [SubscriptionId] int NOT NULL,
                    [InvoiceNumber] nvarchar(100) NOT NULL,
                    [AmountVnd] decimal(18,2) NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_AmountVnd] DEFAULT(0),
                    [Status] nvarchar(50) NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_Status] DEFAULT(N'Pending'),
                    [Created_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_Created_Date] DEFAULT(GETDATE()),
                    [Updated_Date] datetime NOT NULL CONSTRAINT [DF_TblNinePayQrSessionInvoice_Updated_Date] DEFAULT(GETDATE())
                );
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_TblNinePayQrSessionInvoice_QrSessionId'
                  AND [object_id] = OBJECT_ID(N'[dbo].[TblNinePayQrSessionInvoice]')
            )
            BEGIN
                CREATE INDEX [IX_TblNinePayQrSessionInvoice_QrSessionId]
                ON [dbo].[TblNinePayQrSessionInvoice]([QrSessionId]);
            END;

            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE [name] = N'IX_TblNinePayQrSessionInvoice_Invoice_Active'
                  AND [object_id] = OBJECT_ID(N'[dbo].[TblNinePayQrSessionInvoice]')
            )
            BEGIN
                CREATE INDEX [IX_TblNinePayQrSessionInvoice_Invoice_Active]
                ON [dbo].[TblNinePayQrSessionInvoice]([InvoiceId], [Status]);
            END;
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _schemaEnsured = true;
    }

    private static async Task<(int InvoiceId, int SubscriptionId, string InvoiceNumber, decimal Amount, string Status)?> FindInvoiceByIdAsync(SqlConnection connection, SqlTransaction transaction, int invoiceId, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 [ID], [SubscriptionId], [InvoiceNumber], [Amount], [Status]
            FROM [dbo].[TblSubscriptionInvoice]
            WHERE [ID] = @invoiceId
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            ReadInt(reader, "ID"),
            ReadInt(reader, "SubscriptionId"),
            reader["InvoiceNumber"]?.ToString() ?? string.Empty,
            ReadDecimal(reader, "Amount"),
            reader["Status"]?.ToString() ?? string.Empty);
    }

    private static async Task<List<QrInvoiceItem>> FindPendingInvoicesBySubscriptionIdsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<int> subscriptionIds,
        CancellationToken cancellationToken)
    {
        var parameterNames = subscriptionIds.Select((_, index) => $"@id{index}").ToList();
        var query = $"""
            SELECT i.[ID], i.[SubscriptionId], s.[TenantId], i.[InvoiceNumber], i.[Amount]
            FROM [dbo].[TblSubscriptionInvoice] i
            INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
            WHERE i.[SubscriptionId] IN ({string.Join(",", parameterNames)})
              AND LOWER(i.[Status]) = N'pending'
              AND i.[Amount] > ISNULL(i.[PaidAmount], 0)
            ORDER BY i.[SubscriptionId], i.[ID]
            """;

        var invoices = new List<QrInvoiceItem>();
        await using var command = new SqlCommand(query, connection, transaction);
        for (var index = 0; index < subscriptionIds.Count; index++)
        {
            command.Parameters.Add(parameterNames[index], SqlDbType.Int).Value = subscriptionIds[index];
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            invoices.Add(new QrInvoiceItem(
                ReadInt(reader, "ID"),
                ReadInt(reader, "SubscriptionId"),
                ReadInt(reader, "TenantId"),
                reader["InvoiceNumber"]?.ToString() ?? string.Empty,
                ReadDecimal(reader, "Amount")));
        }

        return invoices;
    }

    private static async Task UpsertPendingTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int invoiceId,
        int subscriptionId,
        string invoiceNumber,
        decimal orderAmountUsd,
        decimal exchangeRateVndPerUsd,
        decimal convertedAmountVnd,
        decimal transactionFeeVnd,
        decimal totalToPayVnd,
        string currency,
        string method,
        string paymentUrl,
        string providerPaymentNo,
        string providerStatus,
        CancellationToken cancellationToken)
    {
        const string query = """
            DECLARE @existingId int;

            SELECT TOP 1 @existingId = [ID]
            FROM [dbo].[TblPaymentTransaction]
            WHERE [Provider] = N'9Pay'
              AND [InvoiceNumber] = @invoiceNumber
              AND [Status] IN (N'pending', N'created')
            ORDER BY [ID] DESC;

            IF @existingId IS NULL
            BEGIN
                INSERT INTO [dbo].[TblPaymentTransaction]
                    ([Provider], [InvoiceId], [SubscriptionId], [InvoiceNumber], [ProviderPaymentNo], [ProviderStatus], [Status], [OrderAmountUsd],
                     [ExchangeRateVndPerUsd], [ConvertedAmountVnd], [TransactionFeeVnd], [AmountVnd], [Currency],
                     [Method], [Description], [PaymentUrl], [ChecksumValid], [Created_Date], [Updated_Date])
                VALUES
                    (N'9Pay', @invoiceId, @subscriptionId, @invoiceNumber, @providerPaymentNo, @providerStatus, N'pending', @orderAmountUsd,
                     @exchangeRateVndPerUsd, @convertedAmountVnd, @transactionFeeVnd, @totalToPayVnd, @currency,
                     @method, @description, @paymentUrl, 1, GETDATE(), GETDATE());
            END
            ELSE
            BEGIN
                UPDATE [dbo].[TblPaymentTransaction]
                SET [InvoiceId] = @invoiceId,
                    [SubscriptionId] = @subscriptionId,
                    [ProviderPaymentNo] = @providerPaymentNo,
                    [ProviderStatus] = @providerStatus,
                    [OrderAmountUsd] = @orderAmountUsd,
                    [ExchangeRateVndPerUsd] = @exchangeRateVndPerUsd,
                    [ConvertedAmountVnd] = @convertedAmountVnd,
                    [TransactionFeeVnd] = @transactionFeeVnd,
                    [AmountVnd] = @totalToPayVnd,
                    [Currency] = @currency,
                    [Method] = @method,
                    [Description] = @description,
                    [PaymentUrl] = @paymentUrl,
                    [Updated_Date] = GETDATE()
                WHERE [ID] = @existingId;
            END
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        command.Parameters.Add("@invoiceNumber", SqlDbType.NVarChar, 100).Value = invoiceNumber;
        command.Parameters.Add("@providerPaymentNo", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(providerPaymentNo);
        command.Parameters.Add("@providerStatus", SqlDbType.NVarChar, 50).Value = EmptyToDbNull(providerStatus);
        AddDecimal(command, "@orderAmountUsd", orderAmountUsd);
        AddDecimal(command, "@exchangeRateVndPerUsd", exchangeRateVndPerUsd, scale: 6);
        AddDecimal(command, "@convertedAmountVnd", convertedAmountVnd);
        AddDecimal(command, "@transactionFeeVnd", transactionFeeVnd);
        AddDecimal(command, "@totalToPayVnd", totalToPayVnd);
        command.Parameters.Add("@currency", SqlDbType.NVarChar, 10).Value = currency;
        command.Parameters.Add("@method", SqlDbType.NVarChar, 50).Value = method;
        command.Parameters.Add("@description", SqlDbType.NVarChar, 500).Value = $"Payment for {invoiceNumber}";
        command.Parameters.Add("@paymentUrl", SqlDbType.NVarChar, -1).Value = paymentUrl;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DirectNinePayBankTransferResult?> FindActiveQrSessionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int invoiceId,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1
                [ProviderInvoiceNo],
                [ProviderPaymentNo],
                [ProviderStatus],
                [QrStartedAt],
                [QrExpiresAt],
                [ProviderResponseJson],
                [DebugLog]
            FROM [dbo].[TblNinePayQrSession]
            WHERE (
                  [InvoiceId] = @invoiceId
                  OR EXISTS (
                      SELECT 1
                      FROM [dbo].[TblNinePayQrSessionInvoice] qi
                      WHERE qi.[QrSessionId] = [dbo].[TblNinePayQrSession].[ID]
                        AND qi.[InvoiceId] = @invoiceId
                        AND qi.[Status] = N'Pending'
                  )
              )
              AND [Status] = N'Pending'
              AND [QrExpiresAt] > GETUTCDATE()
            ORDER BY [ID] DESC
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var providerInvoiceNo = reader["ProviderInvoiceNo"]?.ToString() ?? string.Empty;
        var providerPaymentNo = reader["ProviderPaymentNo"]?.ToString() ?? string.Empty;
        var providerStatus = reader["ProviderStatus"]?.ToString() ?? string.Empty;
        var qrStartedAt = Convert.ToDateTime(reader["QrStartedAt"], CultureInfo.InvariantCulture);
        var qrExpiresAt = Convert.ToDateTime(reader["QrExpiresAt"], CultureInfo.InvariantCulture);
        var responseJson = reader["ProviderResponseJson"]?.ToString() ?? string.Empty;
        var debugLog = reader["DebugLog"]?.ToString() ?? string.Empty;

        await reader.DisposeAsync();

        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        using var json = JsonDocument.Parse(responseJson);
        var parsed = ParseDirectBankTransferResult(json.RootElement, 4320);
        return parsed with
        {
            ProviderOrderRef = string.IsNullOrWhiteSpace(parsed.ProviderOrderRef) ? providerInvoiceNo : parsed.ProviderOrderRef,
            ProviderPaymentId = string.IsNullOrWhiteSpace(parsed.ProviderPaymentId) ? providerPaymentNo : parsed.ProviderPaymentId,
            ProviderStatus = string.IsNullOrWhiteSpace(parsed.ProviderStatus) ? providerStatus : parsed.ProviderStatus,
            QrStartedAt = qrStartedAt,
            ExpiresAt = qrExpiresAt,
            QrStatus = "Pending",
            ReusedQr = true,
            ProviderResponseJson = responseJson,
            DebugLog = string.IsNullOrWhiteSpace(debugLog)
                ? $"Reused active 9Pay QR session. ProviderInvoiceNo={providerInvoiceNo}; ExpiresAt={qrExpiresAt:O}"
                : $"{debugLog}\nReused active 9Pay QR session. ProviderInvoiceNo={providerInvoiceNo}; ExpiresAt={qrExpiresAt:O}"
        };
    }

    private static async Task<DirectNinePayBankTransferResult?> FindActiveQrSessionForInvoicesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<int> invoiceIds,
        CancellationToken cancellationToken)
    {
        var distinctInvoiceIds = invoiceIds.Where(id => id > 0).Distinct().ToList();
        if (distinctInvoiceIds.Count == 0)
        {
            return null;
        }

        var invoiceParameters = distinctInvoiceIds.Select((_, index) => $"@invoiceId{index}").ToList();
        var query = $"""
            SELECT TOP 1
                q.[ProviderInvoiceNo],
                q.[ProviderPaymentNo],
                q.[ProviderStatus],
                q.[QrStartedAt],
                q.[QrExpiresAt],
                q.[ProviderResponseJson],
                q.[DebugLog]
            FROM [dbo].[TblNinePayQrSession] q
            WHERE q.[Status] = N'Pending'
              AND q.[QrExpiresAt] > GETUTCDATE()
              AND (
                  SELECT COUNT(DISTINCT qi.[InvoiceId])
                  FROM [dbo].[TblNinePayQrSessionInvoice] qi
                  WHERE qi.[QrSessionId] = q.[ID]
                    AND qi.[Status] = N'Pending'
              ) = @invoiceCount
              AND (
                  SELECT COUNT(DISTINCT qi.[InvoiceId])
                  FROM [dbo].[TblNinePayQrSessionInvoice] qi
                  WHERE qi.[QrSessionId] = q.[ID]
                    AND qi.[Status] = N'Pending'
                    AND qi.[InvoiceId] IN ({string.Join(", ", invoiceParameters)})
              ) = @invoiceCount
            ORDER BY q.[ID] DESC
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceCount", SqlDbType.Int).Value = distinctInvoiceIds.Count;
        for (var index = 0; index < distinctInvoiceIds.Count; index++)
        {
            command.Parameters.Add(invoiceParameters[index], SqlDbType.Int).Value = distinctInvoiceIds[index];
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var providerInvoiceNo = reader["ProviderInvoiceNo"]?.ToString() ?? string.Empty;
        var providerPaymentNo = reader["ProviderPaymentNo"]?.ToString() ?? string.Empty;
        var providerStatus = reader["ProviderStatus"]?.ToString() ?? string.Empty;
        var qrStartedAt = Convert.ToDateTime(reader["QrStartedAt"], CultureInfo.InvariantCulture);
        var qrExpiresAt = Convert.ToDateTime(reader["QrExpiresAt"], CultureInfo.InvariantCulture);
        var responseJson = reader["ProviderResponseJson"]?.ToString() ?? string.Empty;
        var debugLog = reader["DebugLog"]?.ToString() ?? string.Empty;

        await reader.DisposeAsync();

        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        using var json = JsonDocument.Parse(responseJson);
        var parsed = ParseDirectBankTransferResult(json.RootElement, 4320);
        return parsed with
        {
            ProviderOrderRef = string.IsNullOrWhiteSpace(parsed.ProviderOrderRef) ? providerInvoiceNo : parsed.ProviderOrderRef,
            ProviderPaymentId = string.IsNullOrWhiteSpace(parsed.ProviderPaymentId) ? providerPaymentNo : parsed.ProviderPaymentId,
            ProviderStatus = string.IsNullOrWhiteSpace(parsed.ProviderStatus) ? providerStatus : parsed.ProviderStatus,
            QrStartedAt = qrStartedAt,
            ExpiresAt = qrExpiresAt,
            QrStatus = "Pending",
            ReusedQr = true,
            ProviderResponseJson = responseJson,
            DebugLog = string.IsNullOrWhiteSpace(debugLog)
                ? $"Reused active grouped 9Pay QR session. ProviderInvoiceNo={providerInvoiceNo}; ExpiresAt={qrExpiresAt:O}"
                : $"{debugLog}\nReused active grouped 9Pay QR session. ProviderInvoiceNo={providerInvoiceNo}; ExpiresAt={qrExpiresAt:O}"
        };
    }

    private static async Task InsertQrSessionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int invoiceId,
        int subscriptionId,
        string invoiceNumber,
        DirectNinePayBankTransferResult result,
        decimal amountVnd,
        string currency,
        string method,
        string description,
        string createdBy,
        decimal transferFeeVnd,
        CancellationToken cancellationToken,
        IReadOnlyList<QrInvoiceItem>? qrInvoices = null)
    {
        var firstBank = result.Banks.FirstOrDefault();
        const string query = """
            INSERT INTO [dbo].[TblNinePayQrSession]
                ([InvoiceId], [SubscriptionId], [InvoiceNumber], [ProviderInvoiceNo], [ProviderPaymentNo], [ProviderStatus],
                 [Status], [AmountVnd], [Currency], [Method], [Description], [Channel], [Created_By], [TransferFeeVnd],
                 [BankAccountNo], [TransferContent], [QrStartedAt], [QrExpiresAt],
                 [ProviderResponseJson], [DebugLog], [Created_Date], [Updated_Date])
            OUTPUT INSERTED.[ID]
            VALUES
                (@invoiceId, @subscriptionId, @invoiceNumber, @providerInvoiceNo, @providerPaymentNo, @providerStatus,
                 N'Pending', @amountVnd, @currency, @method, @description, N'9pay', @createdBy, @transferFeeVnd,
                 @bankAccountNo, @transferContent, @qrStartedAt, @qrExpiresAt,
                 @providerResponseJson, @debugLog, GETDATE(), GETDATE());
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        command.Parameters.Add("@invoiceNumber", SqlDbType.NVarChar, 100).Value = invoiceNumber;
        command.Parameters.Add("@providerInvoiceNo", SqlDbType.NVarChar, 100).Value = result.ProviderOrderRef;
        command.Parameters.Add("@providerPaymentNo", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(result.ProviderPaymentId);
        command.Parameters.Add("@providerStatus", SqlDbType.NVarChar, 50).Value = EmptyToDbNull(result.ProviderStatus);
        AddDecimal(command, "@amountVnd", amountVnd);
        command.Parameters.Add("@currency", SqlDbType.NVarChar, 10).Value = currency;
        command.Parameters.Add("@method", SqlDbType.NVarChar, 50).Value = method;
        command.Parameters.Add("@description", SqlDbType.NVarChar, 500).Value = description;
        command.Parameters.Add("@createdBy", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(createdBy);
        AddDecimal(command, "@transferFeeVnd", transferFeeVnd);
        command.Parameters.Add("@bankAccountNo", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(firstBank?.BankAccountNo ?? string.Empty);
        command.Parameters.Add("@transferContent", SqlDbType.NVarChar, 500).Value = EmptyToDbNull(firstBank?.Remark ?? firstBank?.Content ?? firstBank?.Plaintext ?? description);
        command.Parameters.Add("@qrStartedAt", SqlDbType.DateTime).Value = result.QrStartedAt ?? DateTime.UtcNow;
        command.Parameters.Add("@qrExpiresAt", SqlDbType.DateTime).Value = result.ExpiresAt ?? DateTime.UtcNow.AddHours(72);
        command.Parameters.Add("@providerResponseJson", SqlDbType.NVarChar, -1).Value = EmptyToDbNull(result.ProviderResponseJson);
        command.Parameters.Add("@debugLog", SqlDbType.NVarChar, -1).Value = EmptyToDbNull(result.DebugLog);
        var qrSessionId = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        var mappings = qrInvoices is { Count: > 0 }
            ? qrInvoices
            : [new QrInvoiceItem(invoiceId, subscriptionId, 0, invoiceNumber, amountVnd)];

        foreach (var item in mappings)
        {
            await InsertQrSessionInvoiceAsync(connection, transaction, qrSessionId, item.InvoiceId, item.SubscriptionId, item.InvoiceNumber, item.Amount, cancellationToken);
        }
    }

    private static async Task InsertQrSessionInvoiceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int qrSessionId,
        int invoiceId,
        int subscriptionId,
        string invoiceNumber,
        decimal amountVnd,
        CancellationToken cancellationToken)
    {
        const string query = """
            IF NOT EXISTS (
                SELECT 1
                FROM [dbo].[TblNinePayQrSessionInvoice]
                WHERE [QrSessionId] = @qrSessionId
                  AND [InvoiceId] = @invoiceId
            )
            BEGIN
                INSERT INTO [dbo].[TblNinePayQrSessionInvoice]
                    ([QrSessionId], [InvoiceId], [SubscriptionId], [InvoiceNumber], [AmountVnd], [Status], [Created_Date], [Updated_Date])
                VALUES
                    (@qrSessionId, @invoiceId, @subscriptionId, @invoiceNumber, @amountVnd, N'Pending', GETDATE(), GETDATE());
            END
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@qrSessionId", SqlDbType.Int).Value = qrSessionId;
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoiceId;
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        command.Parameters.Add("@invoiceNumber", SqlDbType.NVarChar, 100).Value = invoiceNumber;
        AddDecimal(command, "@amountVnd", amountVnd);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(int InvoiceId, int SubscriptionId, decimal InvoiceAmount)?> FindInvoiceAsync(SqlConnection connection, SqlTransaction transaction, string invoiceNumber, CancellationToken cancellationToken)
    {
        const string query = """
            SELECT TOP 1 [ID], [SubscriptionId], [Amount]
            FROM [dbo].[TblSubscriptionInvoice]
            WHERE [InvoiceNumber] = @invoiceNumber
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceNumber", SqlDbType.NVarChar, 100).Value = invoiceNumber;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            ReadInt(reader, "ID"),
            ReadInt(reader, "SubscriptionId"),
            ReadDecimal(reader, "Amount"));
    }

    private static async Task<List<(int InvoiceId, int SubscriptionId, string InvoiceNumber, decimal InvoiceAmount)>> FindInvoicesByQrProviderInvoiceAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string providerInvoiceNo,
        CancellationToken cancellationToken)
    {
        const string query = """
            SELECT
                qi.[InvoiceId],
                qi.[SubscriptionId],
                qi.[InvoiceNumber],
                i.[Amount]
            FROM [dbo].[TblNinePayQrSession] q
            INNER JOIN [dbo].[TblNinePayQrSessionInvoice] qi ON qi.[QrSessionId] = q.[ID]
            INNER JOIN [dbo].[TblSubscriptionInvoice] i ON i.[ID] = qi.[InvoiceId]
            WHERE q.[ProviderInvoiceNo] = @providerInvoiceNo
            ORDER BY qi.[ID] ASC
            """;

        var invoices = new List<(int InvoiceId, int SubscriptionId, string InvoiceNumber, decimal InvoiceAmount)>();
        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@providerInvoiceNo", SqlDbType.NVarChar, 100).Value = providerInvoiceNo;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            invoices.Add((
                ReadInt(reader, "InvoiceId"),
                ReadInt(reader, "SubscriptionId"),
                reader["InvoiceNumber"]?.ToString() ?? string.Empty,
                ReadDecimal(reader, "Amount")));
        }

        return invoices;
    }

    private static async Task MarkQrSessionPaidAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string providerInvoiceNo,
        string providerPaymentNo,
        string providerStatus,
        string rawJson,
        DateTime completedAt,
        CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE [dbo].[TblNinePayQrSession]
            SET [Status] = N'Paid',
                [ProviderPaymentNo] = @providerPaymentNo,
                [ProviderStatus] = @providerStatus,
                [IpnPaymentNo] = @providerPaymentNo,
                [IpnReceivedAt] = COALESCE([IpnReceivedAt], @completedAt),
                [IpnProcessStatus] = N'Processed',
                [IpnProcessMessage] = N'IPN processed and QR session marked paid.',
                [IpnRawJson] = @rawJson,
                [PaidAt] = @completedAt,
                [Updated_Date] = @completedAt
            WHERE [ProviderInvoiceNo] = @providerInvoiceNo;

            UPDATE qi
            SET qi.[Status] = N'Paid',
                qi.[Updated_Date] = @completedAt
            FROM [dbo].[TblNinePayQrSessionInvoice] qi
            INNER JOIN [dbo].[TblNinePayQrSession] q ON q.[ID] = qi.[QrSessionId]
            WHERE q.[ProviderInvoiceNo] = @providerInvoiceNo;
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@providerInvoiceNo", SqlDbType.NVarChar, 100).Value = providerInvoiceNo;
        command.Parameters.Add("@providerPaymentNo", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(providerPaymentNo);
        command.Parameters.Add("@providerStatus", SqlDbType.NVarChar, 50).Value = EmptyToDbNull(providerStatus);
        command.Parameters.Add("@rawJson", SqlDbType.NVarChar, -1).Value = EmptyToDbNull(rawJson);
        command.Parameters.Add("@completedAt", SqlDbType.DateTime).Value = completedAt;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateQrSessionIpnAttemptAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string providerInvoiceNo,
        string providerPaymentNo,
        string providerStatus,
        string resultBase64,
        string rawJson,
        string checksum,
        string processStatus,
        string processMessage,
        DateTime receivedAt,
        CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE [dbo].[TblNinePayQrSession]
            SET [ProviderPaymentNo] = COALESCE(NULLIF(@providerPaymentNo, N''), [ProviderPaymentNo]),
                [ProviderStatus] = COALESCE(NULLIF(@providerStatus, N''), [ProviderStatus]),
                [IpnPaymentNo] = COALESCE(NULLIF(@providerPaymentNo, N''), [IpnPaymentNo]),
                [IpnReceivedAt] = @receivedAt,
                [IpnProcessStatus] = @processStatus,
                [IpnProcessMessage] = @processMessage,
                [IpnChecksum] = @checksum,
                [IpnResultBase64] = @resultBase64,
                [IpnRawJson] = @rawJson,
                [Updated_Date] = @receivedAt
            WHERE [ProviderInvoiceNo] = @providerInvoiceNo;
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@providerInvoiceNo", SqlDbType.NVarChar, 100).Value = providerInvoiceNo;
        command.Parameters.Add("@providerPaymentNo", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(providerPaymentNo);
        command.Parameters.Add("@providerStatus", SqlDbType.NVarChar, 50).Value = EmptyToDbNull(providerStatus);
        command.Parameters.Add("@resultBase64", SqlDbType.NVarChar, -1).Value = EmptyToDbNull(resultBase64);
        command.Parameters.Add("@rawJson", SqlDbType.NVarChar, -1).Value = EmptyToDbNull(rawJson);
        command.Parameters.Add("@checksum", SqlDbType.NVarChar, 200).Value = EmptyToDbNull(checksum);
        command.Parameters.Add("@processStatus", SqlDbType.NVarChar, 50).Value = EmptyToDbNull(processStatus);
        command.Parameters.Add("@processMessage", SqlDbType.NVarChar, 500).Value = EmptyToDbNull(TrimForLog(processMessage, 500));
        command.Parameters.Add("@receivedAt", SqlDbType.DateTime).Value = receivedAt;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int? invoiceId,
        int? subscriptionId,
        string invoiceNumber,
        string paymentNo,
        string providerStatus,
        string status,
        decimal amountVnd,
        string currency,
        string method,
        string description,
        string failureReason,
        string resultBase64,
        string rawJson,
        string checksum,
        DateTime? providerCreatedAt,
        DateTime? completedAt,
        CancellationToken cancellationToken)
    {
        const string query = """
            DECLARE @existingId int;

            SELECT TOP 1 @existingId = [ID]
            FROM [dbo].[TblPaymentTransaction]
            WHERE [Provider] = N'9Pay'
              AND (
                    (NULLIF(@paymentNo, N'') IS NOT NULL AND [ProviderPaymentNo] = @paymentNo)
                    OR (NULLIF(@paymentNo, N'') IS NULL AND [InvoiceNumber] = @invoiceNumber)
                  )
            ORDER BY [ID] DESC;

            IF @existingId IS NULL
            BEGIN
                INSERT INTO [dbo].[TblPaymentTransaction]
                    ([Provider], [InvoiceId], [SubscriptionId], [InvoiceNumber], [ProviderPaymentNo], [ProviderStatus], [Status],
                     [AmountVnd], [Currency], [Method], [Description], [FailureReason], [RawResultBase64], [RawResultJson],
                     [RawChecksum], [ChecksumValid], [ProviderCreatedAt], [CompletedAt], [Created_Date], [Updated_Date])
                VALUES
                    (N'9Pay', @invoiceId, @subscriptionId, @invoiceNumber, @paymentNo, @providerStatus, @status,
                     @amountVnd, @currency, @method, @description, @failureReason, @resultBase64, @rawJson,
                     @checksum, 1, @providerCreatedAt, @completedAt, GETDATE(), GETDATE());
            END
            ELSE
            BEGIN
                UPDATE [dbo].[TblPaymentTransaction]
                SET [InvoiceId] = @invoiceId,
                    [SubscriptionId] = @subscriptionId,
                    [InvoiceNumber] = @invoiceNumber,
                    [ProviderPaymentNo] = @paymentNo,
                    [ProviderStatus] = @providerStatus,
                    [Status] = @status,
                    [AmountVnd] = @amountVnd,
                    [Currency] = @currency,
                    [Method] = @method,
                    [Description] = @description,
                    [FailureReason] = @failureReason,
                    [RawResultBase64] = @resultBase64,
                    [RawResultJson] = @rawJson,
                    [RawChecksum] = @checksum,
                    [ChecksumValid] = 1,
                    [ProviderCreatedAt] = @providerCreatedAt,
                    [CompletedAt] = @completedAt,
                    [Updated_Date] = GETDATE()
                WHERE [ID] = @existingId;
            END
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = (object?)invoiceId ?? DBNull.Value;
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = (object?)subscriptionId ?? DBNull.Value;
        command.Parameters.Add("@invoiceNumber", SqlDbType.NVarChar, 100).Value = invoiceNumber;
        command.Parameters.Add("@paymentNo", SqlDbType.NVarChar, 100).Value = paymentNo;
        command.Parameters.Add("@providerStatus", SqlDbType.NVarChar, 50).Value = providerStatus;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = status;
        AddDecimal(command, "@amountVnd", amountVnd);
        command.Parameters.Add("@currency", SqlDbType.NVarChar, 10).Value = EmptyToDbNull(currency);
        command.Parameters.Add("@method", SqlDbType.NVarChar, 50).Value = EmptyToDbNull(method);
        command.Parameters.Add("@description", SqlDbType.NVarChar, 500).Value = EmptyToDbNull(description);
        command.Parameters.Add("@failureReason", SqlDbType.NVarChar, 500).Value = EmptyToDbNull(failureReason);
        command.Parameters.Add("@resultBase64", SqlDbType.NVarChar, -1).Value = resultBase64;
        command.Parameters.Add("@rawJson", SqlDbType.NVarChar, -1).Value = rawJson;
        command.Parameters.Add("@checksum", SqlDbType.NVarChar, 200).Value = checksum;
        command.Parameters.Add("@providerCreatedAt", SqlDbType.DateTime).Value = (object?)providerCreatedAt ?? DBNull.Value;
        command.Parameters.Add("@completedAt", SqlDbType.DateTime).Value = (object?)completedAt ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkInvoicePaidAsync(SqlConnection connection, SqlTransaction transaction, (int InvoiceId, int SubscriptionId, decimal InvoiceAmount) invoice, string paymentNo, DateTime completedAt, CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE [dbo].[TblSubscriptionInvoice]
            SET [PaidAmount] = [Amount],
                [ReceiptNumber] = @paymentNo,
                [CompletedAt] = @completedAt,
                [Status] = N'paid',
                [Updated_Date] = GETDATE(),
                [Updated_By] = N'9Pay IPN'
            WHERE [ID] = @invoiceId
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@invoiceId", SqlDbType.Int).Value = invoice.InvoiceId;
        command.Parameters.Add("@paymentNo", SqlDbType.NVarChar, 100).Value = EmptyToDbNull(paymentNo);
        command.Parameters.Add("@completedAt", SqlDbType.DateTime).Value = completedAt;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RecalculateSubscriptionTotalsAsync(SqlConnection connection, SqlTransaction transaction, int subscriptionId, CancellationToken cancellationToken)
    {
        const string query = """
            UPDATE s
            SET [TotalInvoiceAmount] = COALESCE(inv.[TotalInvoiceAmount], 0),
                [TotalPaid] = COALESCE(inv.[TotalPaid], 0),
                [Updated_Date] = GETDATE()
            FROM [dbo].[TblMonthlySubscription] s
            OUTER APPLY (
                SELECT SUM(i.[Amount]) AS [TotalInvoiceAmount],
                       SUM(i.[PaidAmount]) AS [TotalPaid]
                FROM [dbo].[TblSubscriptionInvoice] i
                WHERE i.[SubscriptionId] = s.[ID]
            ) inv
            WHERE s.[ID] = @subscriptionId
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditAsync(SqlConnection connection, SqlTransaction transaction, int subscriptionId, string detail, CancellationToken cancellationToken)
    {
        const string query = """
            IF OBJECT_ID(N'[dbo].[TblAuditLog]', N'U') IS NOT NULL
            BEGIN
                INSERT INTO [dbo].[TblAuditLog] ([DeviceId], [LogAction], [LogDetail], [Created_Date])
                VALUES (@subscriptionId, N'payment_9pay', @detail, GETDATE())
            END
            """;

        await using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.Add("@subscriptionId", SqlDbType.Int).Value = subscriptionId;
        command.Parameters.Add("@detail", SqlDbType.NVarChar, 1000).Value = detail;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ToString();
    }

    private static decimal GetDecimal(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    private static DateTime? GetDate(JsonElement element, string propertyName)
    {
        var value = GetString(element, propertyName);
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return date;
        }

        return null;
    }

    private static object EmptyToDbNull(string value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static int ReadInt(SqlDataReader reader, string columnName) => reader[columnName] == DBNull.Value ? 0 : reader[columnName] is int value ? value : Convert.ToInt32(reader[columnName], CultureInfo.InvariantCulture);

    private static decimal ReadDecimal(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value == DBNull.Value ? 0 : Convert.ToDecimal(value);
    }

    private static DateTime? ReadDate(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value == DBNull.Value ? null : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
    }

    private static string ReadText(SqlDataReader reader, string columnName)
    {
        var value = reader[columnName];
        return value == DBNull.Value ? string.Empty : value?.ToString() ?? string.Empty;
    }

    private string BuildNinePayPaymentUrl(string invoiceNumber, decimal totalToPayVnd, string method)
    {
        var baseUrl = (configuration["NinePay:BusinessBaseUrl"] ?? "https://sand-business.9pay.vn").TrimEnd('/');
        var path = configuration["NinePay:PaymentCreatePath"] ?? "/payments/create";
        var applicationBaseUrl = (configuration["NinePay:ApplicationBaseUrl"] ?? "https://localhost:2008").TrimEnd('/');
        var returnUrl = configuration["NinePay:ReturnUrl"];
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            returnUrl = $"{applicationBaseUrl}/Payments/NinePayReturn";
        }

        var ipnUrl = configuration["NinePay:IpnUrl"];
        if (string.IsNullOrWhiteSpace(ipnUrl))
        {
            ipnUrl = $"{applicationBaseUrl}/Payments/NinePayIpn";
        }

        var merchantKey = (configuration["NinePay:MerchantKey"] ?? string.Empty).Trim();
        var language = configuration["NinePay:Language"] ?? "vi";
        var query = string.Join("&", new[]
        {
            $"merchantKey={Uri.EscapeDataString(merchantKey)}",
            $"invoice_no={Uri.EscapeDataString(invoiceNumber)}",
            $"amount={Uri.EscapeDataString(totalToPayVnd.ToString("0", CultureInfo.InvariantCulture))}",
            "currency=VND",
            $"method={Uri.EscapeDataString(method)}",
            $"lang={Uri.EscapeDataString(language)}",
            $"description={Uri.EscapeDataString($"Payment for {invoiceNumber}")}",
            $"return_url={Uri.EscapeDataString(returnUrl)}",
            $"ipn_url={Uri.EscapeDataString(ipnUrl)}"
        });

        return $"{baseUrl}{path}?{query}";
    }

    private async Task<DirectNinePayBankTransferResult> TryCreateDirectBankTransferAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int invoiceId,
        int subscriptionId,
        string invoiceNumber,
        decimal totalToPayVnd,
        decimal transferFeeVnd,
        string method,
        string clientIp,
        string createdBy,
        CancellationToken cancellationToken,
        IReadOnlyList<QrInvoiceItem>? qrInvoices = null)
    {
        var section = configuration.GetSection("NinePay:DirectBankTransfer");
        if (!section.GetValue("Enabled", false))
        {
            return DirectNinePayBankTransferResult.Empty;
        }

        try
        {
            var debug = new StringBuilder();
            var baseUrl = (section["BaseUrl"] ?? "https://sand-payment.9pay.vn").TrimEnd('/');
            var path = section["CreatePath"] ?? "/api/payments/create-bank-transfer";
            var requestUrl = $"{baseUrl}/{path.TrimStart('/')}";
            var applicationBaseUrl = (configuration["NinePay:ApplicationBaseUrl"] ?? "https://localhost:2008").TrimEnd('/');
            var returnUrl = string.IsNullOrWhiteSpace(configuration["NinePay:ReturnUrl"])
                ? $"{applicationBaseUrl}/Payments/NinePayReturn"
                : configuration["NinePay:ReturnUrl"]!;
            var ipnUrl = string.IsNullOrWhiteSpace(configuration["NinePay:IpnUrl"])
                ? $"{applicationBaseUrl}/Payments/NinePayIpn"
                : configuration["NinePay:IpnUrl"]!;
            var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var configuredClientIp = section["DefaultClientIp"] ?? "127.0.0.1";
            var providerInvoiceNo = $"{(configuration["NinePay:MerchantKey"] ?? string.Empty).Trim()}{time}";
            var transferDescription = providerInvoiceNo;
            var body = new List<KeyValuePair<string, string>>
            {
                new("merchantKey", (configuration["NinePay:MerchantKey"] ?? string.Empty).Trim()),
                new("invoice_no", providerInvoiceNo),
                new("lang", configuration["NinePay:Language"] ?? "vi"),
                new("client_ip", configuredClientIp),
                new("amount", totalToPayVnd.ToString("0", CultureInfo.InvariantCulture)),
                new("currency", configuration["NinePay:Currency"] ?? "VND"),
                new("method", section["PaymentMethod"] ?? method),
                new("description", transferDescription),
                new("return_url", returnUrl),
                new("expires_time", "72")
            };

            if (section.GetValue("IncludeIpnUrlInCreateRequest", false))
            {
                body.Add(new("ipn_url", ipnUrl));
            }

            foreach (var extraField in section.GetSection("ExtraFields").GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(extraField.Key))
                {
                    body.Add(new(extraField.Key, extraField.Value ?? string.Empty));
                }
            }

            var merchantKey = (configuration["NinePay:MerchantKey"] ?? string.Empty).Trim();
            var merchantSecretKey = (configuration["NinePay:MerchantSecretKey"] ?? string.Empty).Trim();
            var checksumKey = (configuration["NinePay:ChecksumKey"] ?? string.Empty).Trim();

            debug.AppendLine($"9Pay create-bank-transfer debug {DateTime.UtcNow:O}");
            debug.AppendLine("Scope: create-bank-transfer only");
            debug.AppendLine($"ApiUrl: {requestUrl}");
            debug.AppendLine($"HttpMethod: {section["HttpMethod"] ?? "POST"}");
            debug.AppendLine($"MerchantKey: {merchantKey}");
            debug.AppendLine($"MerchantSecretKey: {MaskValue(merchantSecretKey)}");
            debug.AppendLine($"MerchantSecretKeyLength: {merchantSecretKey.Length}");
            debug.AppendLine($"MerchantSecretKeySha256: {Sha256Hex(merchantSecretKey)}");
            debug.AppendLine($"ChecksumKey: {MaskValue(checksumKey)}");
            debug.AppendLine($"ChecksumKeyLength: {checksumKey.Length}");
            debug.AppendLine($"ChecksumKeySha256: {Sha256Hex(checksumKey)}");
            debug.AppendLine($"OriginalClientIp: {clientIp}");
            debug.AppendLine($"ClientIp: {FindDebugBodyValue(body, "client_ip")}");
            debug.AppendLine($"AppInvoiceNumber: {invoiceNumber}");
            debug.AppendLine($"ProviderInvoiceNo: {providerInvoiceNo}");
            debug.AppendLine($"TransferDescription: {transferDescription}");
            debug.AppendLine($"ExpiresTime: {FindDebugBodyValue(body, "expires_time")}");
            debug.AppendLine($"IpnUrl: {FindDebugBodyValue(body, "ipn_url")}");
            debug.AppendLine("STEP 1 - Config and request values loaded.");
            debug.AppendLine("RequestBody:");
            foreach (var item in body)
            {
                debug.AppendLine($"  {item.Key}={MaskNinePayDebugValue(item.Key, item.Value)}");
            }

            var client = httpClientFactory.CreateClient();
            var httpMethod = HttpMethod.Post;
            var responseText = string.Empty;
            var responseStatusCode = 0;
            var responseReasonPhrase = string.Empty;
            var signatureAttempts = BuildNinePaySignatureAttempts(section);

            debug.AppendLine("STEP 2 - Calling create-bank-transfer.");
            for (var index = 0; index < signatureAttempts.Count; index++)
            {
                var attempt = signatureAttempts[index];
                using var request = new HttpRequestMessage(httpMethod, requestUrl);

                var signatureDebug = ApplyNinePayApiSignatureHeaders(request, body, requestUrl, time, attempt);
                debug.AppendLine($"SignatureAttempt {index + 1}/{signatureAttempts.Count}: {attempt.Name}");
                debug.AppendLine(signatureDebug);

                if (request.Method == HttpMethod.Get)
                {
                    var query = BuildFormUrlEncodedString(body);
                    request.RequestUri = new Uri($"{requestUrl}?{query}");
                }
                else
                {
                    ApplyNinePayRequestContent(request, body, attempt.ContentMode);
                }

                debug.AppendLine("OutgoingRequest:");
                debug.AppendLine($"  Uri: {request.RequestUri}");
                debug.AppendLine($"  Authorization: {ReadHeader(request, "Authorization")}");
                debug.AppendLine($"  Date: {ReadHeader(request, "Date")}");
                debug.AppendLine($"  ContentType: {request.Content?.Headers.ContentType?.ToString() ?? "(none)"}");
                debug.AppendLine($"  ContentPreview: {BuildNinePayContentDebug(body, attempt.ContentMode)}");

                using var response = await client.SendAsync(request, cancellationToken);
                responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                responseStatusCode = (int)response.StatusCode;
                responseReasonPhrase = response.ReasonPhrase ?? string.Empty;
                debug.AppendLine($"AttemptResponseStatus: {responseStatusCode} {responseReasonPhrase}");
                debug.AppendLine($"AttemptResponseBodyFull:");
                debug.AppendLine(responseText);
                if (response.IsSuccessStatusCode)
                {
                    debug.AppendLine($"SuccessfulSignatureAttempt: {attempt.Name}");
                    break;
                }
            }

            if (responseStatusCode < 200 || responseStatusCode >= 300)
            {
                throw new NinePayDebugException($"9Pay bank transfer API returned {responseStatusCode}: {TrimForLog(responseText, 500)}", debug.ToString());
            }

            using var json = JsonDocument.Parse(responseText);
            var directResult = ParseDirectBankTransferResult(json.RootElement, section.GetValue("ExpireMinutes", 4320));
            debug.AppendLine($"ParsedPaymentNo: {directResult.ProviderPaymentId}");
            debug.AppendLine($"ParsedStatus: {directResult.ProviderStatus}");
            debug.AppendLine($"ParsedBankCount: {directResult.Banks.Count}");
            if (directResult.Banks.FirstOrDefault() is { } firstBank)
            {
                debug.AppendLine($"FirstBank: {firstBank.BankCode} | {firstBank.BankName}");
                debug.AppendLine($"FirstBankAccountNo: {MaskValue(firstBank.BankAccountNo)}");
                debug.AppendLine($"FirstQrCodeUrl: {firstBank.QrCodeUrl}");
            }

            if (directResult.Banks.Count == 0)
            {
                var errorCode = FindStringRecursive(json.RootElement, "error_code", "errorCode");
                var failureReason = FindStringRecursive(json.RootElement, "failure_reason", "failureReason", "message");
                var providerError = string.Join(" - ", new[] { errorCode, failureReason }.Where(item => !string.IsNullOrWhiteSpace(item)));
                throw new NinePayDebugException(
                    string.IsNullOrWhiteSpace(providerError)
                        ? "9Pay bank transfer API response does not contain list_bank_info."
                        : $"9Pay bank transfer API returned no bank list: {providerError}",
                    debug.ToString());
            }

            var qrStartedAt = DateTime.UtcNow;
            var qrExpiresAt = directResult.ExpiresAt ?? qrStartedAt.AddMinutes(section.GetValue("ExpireMinutes", 4320));
            var resultWithDebug = directResult with
            {
                ProviderOrderRef = string.IsNullOrWhiteSpace(directResult.ProviderOrderRef) ? providerInvoiceNo : directResult.ProviderOrderRef,
                QrStartedAt = qrStartedAt,
                ExpiresAt = qrExpiresAt,
                QrStatus = "Pending",
                ProviderResponseJson = responseText,
                DebugLog = debug.ToString()
            };
            await InsertQrSessionAsync(
                connection,
                transaction,
                invoiceId,
                subscriptionId,
                invoiceNumber,
                resultWithDebug,
                totalToPayVnd,
                configuration["NinePay:Currency"] ?? "VND",
                method,
                invoiceNumber,
                createdBy,
                transferFeeVnd,
                cancellationToken,
                qrInvoices);
            logger.LogInformation("9Pay bank transfer created for invoice {InvoiceNumber}. PaymentNo={PaymentNo}, BankCount={BankCount}", invoiceNumber, resultWithDebug.ProviderPaymentId, resultWithDebug.Banks.Count);
            return resultWithDebug;
        }
        catch (Exception exception) when (section.GetValue("FailOpenToHostedPayment", true))
        {
            logger.LogWarning(exception, "Direct 9Pay bank transfer API failed. Falling back to hosted payment URL.");
            var debugLog = exception is NinePayDebugException debugException ? debugException.DebugLog : exception.ToString();
            return DirectNinePayBankTransferResult.Empty with { DebugLog = debugLog };
        }
    }

    private async Task RunNinePayBankListPreflightAsync(
        HttpClient client,
        IConfigurationSection section,
        string baseUrl,
        IReadOnlyList<NinePaySignatureAttempt> signatureAttempts,
        StringBuilder debug,
        CancellationToken cancellationToken)
    {
        var path = section["BankListPath"] ?? "/api/payments/inland/banks";
        var requestUrl = $"{baseUrl}/{path.TrimStart('/')}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var emptyBody = Array.Empty<KeyValuePair<string, string>>();

        debug.AppendLine("STEP 2 - Preflight signed GET bank list.");
        debug.AppendLine($"BankListUrl: {requestUrl}");
        debug.AppendLine($"BankListDate: {timestamp}");

        for (var index = 0; index < signatureAttempts.Count; index++)
        {
            var attempt = signatureAttempts[index];
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            var signatureDebug = ApplyNinePayApiSignatureHeaders(request, emptyBody, requestUrl, timestamp, attempt);
            debug.AppendLine($"BankListSignatureAttempt {index + 1}/{signatureAttempts.Count}: {attempt.Name}");
            debug.AppendLine(signatureDebug);

            using var response = await client.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            debug.AppendLine($"BankListResponseStatus: {(int)response.StatusCode} {response.ReasonPhrase}");
            debug.AppendLine($"BankListResponseBody: {TrimForLog(responseText, 2000)}");

            if (response.IsSuccessStatusCode)
            {
                debug.AppendLine($"BankListSuccessfulSignatureAttempt: {attempt.Name}");
                return;
            }
        }

        throw new NinePayDebugException("9Pay bank list preflight failed before creating QR. Check MerchantKey/MerchantSecretKey/signature header first.", debug.ToString());
    }

    private async Task ApplyDirectBankTransferLoginAuthAsync(
        HttpRequestMessage request,
        IConfigurationSection authSection,
        CancellationToken cancellationToken)
    {
        if (!authSection.GetValue("Enabled", false))
        {
            return;
        }

        var loginPath = authSection["LoginPath"];
        var username = authSection["Username"];
        var password = authSection["Password"];
        if (string.IsNullOrWhiteSpace(loginPath) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("NinePay direct bank transfer auth is enabled but LoginPath, Username, or Password is missing.");
        }

        var baseUrl = (authSection["BaseUrl"] ?? configuration["NinePay:BusinessBaseUrl"] ?? "https://sand-business.9pay.vn").TrimEnd('/');
        var loginUrl = $"{baseUrl}/{loginPath.TrimStart('/')}";
        var usernameField = authSection["UsernameField"] ?? "username";
        var passwordField = authSection["PasswordField"] ?? "password";
        var loginBody = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [usernameField] = username,
            [passwordField] = password
        };

        foreach (var extraField in authSection.GetSection("ExtraFields").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(extraField.Key))
            {
                loginBody[extraField.Key] = extraField.Value ?? string.Empty;
            }
        }

        using var loginRequest = new HttpRequestMessage(
            authSection["HttpMethod"]?.Equals("GET", StringComparison.OrdinalIgnoreCase) == true ? HttpMethod.Get : HttpMethod.Post,
            loginUrl);

        if (loginRequest.Method == HttpMethod.Get)
        {
            var query = string.Join("&", loginBody.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
            loginRequest.RequestUri = new Uri($"{loginUrl}?{query}");
        }
        else
        {
            loginRequest.Content = new StringContent(JsonSerializer.Serialize(loginBody), Encoding.UTF8, "application/json");
        }

        var client = httpClientFactory.CreateClient();
        using var loginResponse = await client.SendAsync(loginRequest, cancellationToken);
        var loginResponseText = await loginResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!loginResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"9Pay auth API returned {(int)loginResponse.StatusCode}: {TrimForLog(loginResponseText)}");
        }

        using var json = JsonDocument.Parse(loginResponseText);
        var tokenField = authSection["TokenField"] ?? "access_token";
        var token = FindStringRecursive(json.RootElement, tokenField, "access_token", "accessToken", "token", "jwt");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"9Pay auth API response does not contain token field '{tokenField}'.");
        }

        var headerName = authSection["AuthHeaderName"] ?? "Authorization";
        var authScheme = authSection["AuthScheme"] ?? "Bearer";
        var headerValue = string.IsNullOrWhiteSpace(authScheme) ? token : $"{authScheme} {token}";
        request.Headers.Remove(headerName);
        request.Headers.TryAddWithoutValidation(headerName, headerValue);
    }

    private string ApplyNinePayApiSignatureHeaders(
        HttpRequestMessage request,
        IReadOnlyList<KeyValuePair<string, string>> body,
        string requestUrl,
        string timestamp,
        NinePaySignatureAttempt attempt)
    {
        var merchantKey = (configuration["NinePay:MerchantKey"] ?? string.Empty).Trim();
        var merchantSecretKey = (configuration["NinePay:MerchantSecretKey"] ?? string.Empty).Trim();
        var checksumKey = (configuration["NinePay:ChecksumKey"] ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(merchantKey) || string.IsNullOrWhiteSpace(merchantSecretKey))
        {
            throw new InvalidOperationException("NinePay MerchantKey or MerchantSecretKey is not configured.");
        }

        var signatureBody = attempt.SortParameters
            ? body.OrderBy(item => item.Key, StringComparer.Ordinal).ToList()
            : body;
        var rawCanonicalizedResources = string.Join("&", signatureBody.Select(item => $"{item.Key}={item.Value}"));
        var canonicalizedResources = attempt.EncodeResources
            ? BuildFormUrlEncodedString(signatureBody)
            : rawCanonicalizedResources;
        var signatureUri = attempt.UsePathOnly ? new Uri(requestUrl).PathAndQuery : requestUrl;
        var rawSignature = $"{request.Method.Method}\n{signatureUri}\n{timestamp}\n{canonicalizedResources}";
        var signingKeyBytes = BuildNinePaySigningKeyBytes(attempt.SigningKeyMode, merchantSecretKey, checksumKey);
        using var hmac = new HMACSHA256(signingKeyBytes);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawSignature));
        var merchantSignature = BuildNinePayMerchantSignature(digest, attempt.DigestMode);
        var authorization = attempt.AuthorizationCommaSpaces
            ? $"Signature Algorithm=HS256, Credential={merchantKey}, SignedHeaders={attempt.SignedHeadersValue}, Signature={merchantSignature}"
            : $"Signature Algorithm=HS256,Credential={merchantKey},SignedHeaders={attempt.SignedHeadersValue},Signature={merchantSignature}";
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            authorization);
        request.Headers.TryAddWithoutValidation("Date", timestamp);

        return $"""
          ContentMode: {attempt.ContentMode}
          SignatureUriMode: {(attempt.UsePathOnly ? "path-and-query" : "full-url")}
          CanonicalizedResourcesMode: {(attempt.EncodeResources ? "form-url-encoded" : "raw")}
          CanonicalizedResourcesOrder: {(attempt.SortParameters ? "key-sorted" : "request-body")}
          DigestMode: {attempt.DigestMode}
          SigningKeyMode: {attempt.SigningKeyMode}
          SignedHeadersValue: {(string.IsNullOrWhiteSpace(attempt.SignedHeadersValue) ? "(empty)" : attempt.SignedHeadersValue)}
          RawCanonicalizedResources:
          {rawCanonicalizedResources}
          RawSignature:
          {rawSignature}
          ComputedSignature:
          {merchantSignature}
          AuthorizationFormat: {(attempt.AuthorizationCommaSpaces ? "comma-space" : "docs-nospace")}
          Authorization: {authorization.Replace(merchantSignature, MaskValue(merchantSignature), StringComparison.Ordinal)}
          Date: {timestamp}
          """;
    }

    private static IReadOnlyList<NinePaySignatureAttempt> BuildNinePaySignatureAttempts(IConfigurationSection section)
    {
        var configuredAttempts = section.GetSection("SignatureAttempts").Get<string[]>() ?? [];
        if (configuredAttempts.Length > 0)
        {
            return configuredAttempts
                .Select(ParseNinePaySignatureAttempt)
                .Where(attempt => attempt is not null)
                .Cast<NinePaySignatureAttempt>()
                .ToList();
        }

        return
        [
            new("ninepay-csharp-sample-full-url-encoded-form-secret-raw", EncodeResources: true, UsePathOnly: false, SortParameters: true, DigestMode: "raw-base64", ContentMode: "form", AuthorizationCommaSpaces: false, SigningKeyMode: "merchant-secret-raw", SignedHeadersValue: "")
        ];
    }

    private static NinePaySignatureAttempt? ParseNinePaySignatureAttempt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return new NinePaySignatureAttempt(
            normalized,
            EncodeResources: normalized.Contains("encoded", StringComparison.OrdinalIgnoreCase) || normalized.Contains("urlencoded", StringComparison.OrdinalIgnoreCase),
            UsePathOnly: normalized.Contains("path", StringComparison.OrdinalIgnoreCase),
            SortParameters: normalized.Contains("sort", StringComparison.OrdinalIgnoreCase),
            DigestMode: normalized.Contains("upper-hex", StringComparison.OrdinalIgnoreCase)
                ? "upper-hex-base64"
                : normalized.Contains("lower-hex", StringComparison.OrdinalIgnoreCase) || normalized.Contains("hex", StringComparison.OrdinalIgnoreCase)
                    ? "lower-hex-base64"
                    : "raw-base64",
            ContentMode: normalized.Contains("json", StringComparison.OrdinalIgnoreCase)
                ? "json"
                : normalized.Contains("multipart", StringComparison.OrdinalIgnoreCase) || normalized.Contains("postman", StringComparison.OrdinalIgnoreCase)
                    ? "multipart"
                    : "form",
            AuthorizationCommaSpaces: normalized.Contains("comma-space", StringComparison.OrdinalIgnoreCase),
            SigningKeyMode: normalized.Contains("checksum", StringComparison.OrdinalIgnoreCase)
                ? "checksum-key-raw"
                : normalized.Contains("base64", StringComparison.OrdinalIgnoreCase)
                    ? "merchant-secret-base64"
                    : "merchant-secret-raw",
            SignedHeadersValue: normalized.Contains("signedheaders-date", StringComparison.OrdinalIgnoreCase)
                ? "Date"
                : normalized.Contains("signedheaders-lower-date", StringComparison.OrdinalIgnoreCase)
                    ? "date"
                    : string.Empty);
    }

    private static byte[] BuildNinePaySigningKeyBytes(string signingKeyMode, string merchantSecretKey, string checksumKey)
    {
        if (signingKeyMode.Equals("checksum-key-raw", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8.GetBytes(checksumKey);
        }

        if (signingKeyMode.Equals("merchant-secret-base64", StringComparison.OrdinalIgnoreCase)
            && TryBase64Decode(merchantSecretKey, out var decodedSecret))
        {
            return decodedSecret;
        }

        return Encoding.UTF8.GetBytes(merchantSecretKey);
    }

    private static bool TryBase64Decode(string value, out byte[] decoded)
    {
        try
        {
            decoded = Convert.FromBase64String(value);
            return decoded.Length > 0;
        }
        catch (FormatException)
        {
            decoded = [];
            return false;
        }
    }

    private static void ApplyNinePayRequestContent(
        HttpRequestMessage request,
        IReadOnlyList<KeyValuePair<string, string>> body,
        string contentMode)
    {
        if (contentMode.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(body.ToDictionary(item => item.Key, item => BuildNinePayJsonValue(item.Key, item.Value), StringComparer.Ordinal));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return;
        }

        if (contentMode.Equals("multipart", StringComparison.OrdinalIgnoreCase))
        {
            var multipart = new MultipartFormDataContent();
            foreach (var item in body)
            {
                multipart.Add(new StringContent(item.Value, Encoding.UTF8), item.Key);
            }

            request.Content = multipart;
            return;
        }

        request.Content = new FormUrlEncodedContent(body);
    }

    private static string BuildNinePayContentDebug(
        IReadOnlyList<KeyValuePair<string, string>> body,
        string contentMode)
    {
        if (contentMode.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(body.ToDictionary(
                item => item.Key,
                item => BuildNinePayJsonValue(item.Key, MaskNinePayDebugValue(item.Key, item.Value)),
                StringComparer.Ordinal));
            return json;
        }

        if (contentMode.Equals("multipart", StringComparison.OrdinalIgnoreCase))
        {
            return string.Join("; ", body.Select(item => $"{item.Key}={MaskNinePayDebugValue(item.Key, item.Value)}"));
        }

        return BuildFormUrlEncodedString(body.Select(item => new KeyValuePair<string, string>(item.Key, MaskNinePayDebugValue(item.Key, item.Value))));
    }

    private static object BuildNinePayJsonValue(string key, string value)
    {
        if ((key.Equals("amount", StringComparison.OrdinalIgnoreCase)
                || key.Equals("time", StringComparison.OrdinalIgnoreCase)
                || key.Equals("expires_time", StringComparison.OrdinalIgnoreCase))
            && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return decimal.Truncate(number) == number ? decimal.ToInt64(number) : number;
        }

        return value;
    }

    private static string BuildNinePayMerchantSignature(byte[] digest, string digestMode)
    {
        return digestMode switch
        {
            "lower-hex-base64" => Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToHexString(digest).ToLowerInvariant())),
            "upper-hex-base64" => Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToHexString(digest).ToUpperInvariant())),
            _ => Convert.ToBase64String(digest)
        };
    }

    private static string BuildFormUrlEncodedString(IEnumerable<KeyValuePair<string, string>> values)
    {
        return string.Join("&", values.Select(item => $"{FormUrlEncode(item.Key)}={FormUrlEncode(item.Value)}"));
    }

    private static string FormUrlEncode(string value)
    {
        return Uri.EscapeDataString(value ?? string.Empty);
    }

    private static string FindDebugBodyValue(IEnumerable<KeyValuePair<string, string>> body, string key)
    {
        return body.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;
    }

    private static DirectNinePayBankTransferResult ParseDirectBankTransferResult(JsonElement root, int expireMinutes)
    {
        var providerPaymentId = FindStringRecursive(root, "providerPaymentId", "provider_payment_id", "payment_no", "paymentNo");
        var providerOrderRef = FindStringRecursive(root, "providerInvoiceNo", "provider_invoice_no", "invoice_no", "invoiceNo", "providerOrderRef", "provider_order_ref", "order_ref", "orderRef");
        var providerStatus = FindStringRecursive(root, "status");
        var description = FindStringRecursive(root, "description");
        var paymentUrl = FindStringRecursive(root, "payment_url", "paymentUrl", "checkout_url", "checkoutUrl", "url");
        var expiresAt = FindDateRecursive(root, "expires_at", "expired_at", "expire_at", "expiresAt", "payment_expired_at");
        var banks = new List<NinePayBankTransferBankViewModel>();

        if ((!TryFindPropertyRecursive(root, "list_bank_info", out var banksElement) || banksElement.ValueKind != JsonValueKind.Array)
            && (!TryFindPropertyRecursive(root, "banks", out banksElement) || banksElement.ValueKind != JsonValueKind.Array))
        {
            banksElement = default;
        }

        if (banksElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var bank in banksElement.EnumerateArray())
            {
                var remark = GetJsonStringAny(bank, "remark", "content", "description", "plaintext");
                if (string.IsNullOrWhiteSpace(remark))
                {
                    remark = description;
                }

                banks.Add(new NinePayBankTransferBankViewModel
                {
                    Logo = GetJsonStringAny(bank, "logo", "bank_logo", "logo_url"),
                    IsVa = GetJsonStringAny(bank, "is_va", "isVa"),
                    Remark = remark,
                    Content = GetJsonStringAny(bank, "content", "transfer_content"),
                    Keyword = GetJsonStringAny(bank, "keyword", "bank_keyword"),
                    BankCode = GetJsonStringAny(bank, "bank_code", "bankCode", "code"),
                    BankName = GetJsonStringAny(bank, "bank_name", "bankName", "name"),
                    Plaintext = GetJsonStringAny(bank, "plaintext", "qr_content", "qrContent"),
                    QrCodeUrl = GetJsonStringAny(bank, "qr_code_url", "qrCodeUrl", "qr_url", "qrUrl"),
                    BankAccountNo = GetJsonStringAny(bank, "bank_account_no", "bankAccountNo", "account_no", "accountNo", "va_number", "vaNumber"),
                    BankAccountName = GetJsonStringAny(bank, "bank_account_name", "bankAccountName", "account_name", "accountName")
                });
            }
        }

        if (!expiresAt.HasValue && banks.Count > 0 && expireMinutes > 0)
        {
            expiresAt = DateTime.UtcNow.AddMinutes(expireMinutes);
        }

        return new DirectNinePayBankTransferResult(providerPaymentId, providerOrderRef, providerStatus, paymentUrl, null, expiresAt, banks);
    }

    private static string FindStringRecursive(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryFindPropertyRecursive(element, propertyName, out var property))
            {
                return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.ToString();
            }
        }

        return string.Empty;
    }

    private static DateTime? FindDateRecursive(JsonElement element, params string[] propertyNames)
    {
        var value = FindStringRecursive(element, propertyNames);
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }

    private static bool TryFindPropertyRecursive(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in element.EnumerateObject())
            {
                if (item.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = item.Value;
                    return true;
                }

                if ((item.Value.ValueKind == JsonValueKind.Object || item.Value.ValueKind == JsonValueKind.Array)
                    && TryFindPropertyRecursive(item.Value, propertyName, out property))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindPropertyRecursive(item, propertyName, out property))
                {
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static string GetJsonString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.ToString();
    }

    private static string GetJsonStringAny(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = GetJsonString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string TrimForLog(string value, int maxLength = 500)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string MaskNinePayDebugValue(string key, string value)
    {
        if (key.Equals("merchantKey", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return key.Contains("key", StringComparison.OrdinalIgnoreCase)
            || key.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || key.Contains("signature", StringComparison.OrdinalIgnoreCase)
            ? MaskValue(value)
            : value;
    }

    private static string MaskValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        if (value.Length <= 8)
        {
            return $"{value[..Math.Min(2, value.Length)]}***";
        }

        return $"{value[..4]}***{value[^4..]}";
    }

    private static string Sha256Hex(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ReadHeader(HttpRequestMessage request, string name)
    {
        return request.Headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : "(missing)";
    }

    private static bool IsNinePayPaidStatus(string providerStatus)
    {
        return providerStatus.Equals("success", StringComparison.OrdinalIgnoreCase)
            || providerStatus.Equals("successful", StringComparison.OrdinalIgnoreCase)
            || providerStatus.Equals("5", StringComparison.OrdinalIgnoreCase)
            || providerStatus.Equals("paid", StringComparison.OrdinalIgnoreCase)
            || providerStatus.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || providerStatus.Equals("complete", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal TryParseDecimal(string? value, decimal fallback)
    {
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number) ? number : fallback;
    }

    private async Task<string> GetSystemDefaultCurrencyAsync(CancellationToken cancellationToken)
    {
        var settings = await systemSettingsService.GetSettingsByCodesAsync([DefaultCurrencySettingCode], cancellationToken);
        var configuredCurrency = FirstNotEmpty(
            settings.GetValueOrDefault(DefaultCurrencySettingCode),
            configuration["System:DefaultCurrency"],
            configuration["Billing:DefaultCurrency"],
            PaymentCurrency);

        configuredCurrency = configuredCurrency.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(configuredCurrency) ? PaymentCurrency : configuredCurrency;
    }

    private static void AddDecimal(SqlCommand command, string name, decimal value, byte scale = 2)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 18;
        parameter.Scale = scale;
        parameter.Value = Math.Round(value, scale, MidpointRounding.AwayFromZero);
    }

    private static async Task<string> CallNinePaySampleApiAsync(
        string method,
        string url,
        Dictionary<string, object> parameters,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();

        foreach (var kv in headers)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        HttpResponseMessage response;

        if (method == "POST")
        {
            var formData = parameters.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.ToString() ?? string.Empty);

            response = await client.PostAsync(url, new FormUrlEncodedContent(formData), cancellationToken);
        }
        else
        {
            if (parameters.Count > 0)
            {
                var qs = string.Join("&", parameters.Select(p =>
                    $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value?.ToString() ?? string.Empty)}"));
                url = $"{url}?{qs}";
            }

            response = await client.GetAsync(url, cancellationToken);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private sealed class NinePaySampleHmacSignature
    {
        public string Sign(string message, string key)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            var msgBytes = Encoding.UTF8.GetBytes(message);
            var hash = HMACSHA256.HashData(keyBytes, msgBytes);
            return Convert.ToBase64String(hash);
        }

        public bool Verify(string signature, string message, string key)
        {
            return Sign(message, key) == signature;
        }
    }

    private sealed class NinePaySampleMessageBuilder
    {
        private string _method = "GET";
        private string _uri = string.Empty;
        private string _date = string.Empty;
        private Dictionary<string, string> _headers = [];
        private Dictionary<string, object> _params = [];
        private string _body = string.Empty;

        public static NinePaySampleMessageBuilder Instance()
        {
            return new NinePaySampleMessageBuilder();
        }

        public NinePaySampleMessageBuilder With(
            string date,
            string uri,
            string method = "GET",
            Dictionary<string, string>? headers = null)
        {
            _date = date;
            _uri = uri;
            _method = method;
            _headers = headers ?? [];
            return this;
        }

        public NinePaySampleMessageBuilder WithBody(object body)
        {
            _body = body is string value ? value : JsonSerializer.Serialize(body);
            return this;
        }

        public NinePaySampleMessageBuilder WithParams(Dictionary<string, object> parameters)
        {
            _params = parameters;
            return this;
        }

        public string Build()
        {
            Validate();

            var canonicalHeaders = CanonicalHeaders();
            var canonicalPayload = _method == "POST" && !string.IsNullOrEmpty(_body)
                ? CanonicalBody()
                : CanonicalParams();

            var components = new List<string> { _method, _uri, _date };
            if (!string.IsNullOrEmpty(canonicalHeaders))
            {
                components.Add(canonicalHeaders);
            }

            if (!string.IsNullOrEmpty(canonicalPayload))
            {
                components.Add(canonicalPayload);
            }

            return string.Join("\n", components);
        }

        private void Validate()
        {
            if (string.IsNullOrEmpty(_uri) || string.IsNullOrEmpty(_date))
            {
                throw new InvalidOperationException("Please call With() before Build().");
            }
        }

        private string CanonicalHeaders()
        {
            if (_headers.Count == 0)
            {
                return string.Empty;
            }

            var sorted = new SortedDictionary<string, string>(_headers);
            return string.Join("&", sorted.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        }

        private string CanonicalParams()
        {
            if (_params.Count == 0)
            {
                return string.Empty;
            }

            var sorted = new SortedDictionary<string, object>(_params);
            return string.Join("&", sorted.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value?.ToString() ?? string.Empty)}"));
        }

        private string CanonicalBody()
        {
            var bytes = Encoding.UTF8.GetBytes(_body ?? string.Empty);
            var hash = SHA256.HashData(bytes);
            return Convert.ToBase64String(hash);
        }
    }

    private sealed record DirectNinePayBankTransferResult(
        string ProviderPaymentId,
        string ProviderOrderRef,
        string ProviderStatus,
        string PaymentUrl,
        DateTime? QrStartedAt,
        DateTime? ExpiresAt,
        List<NinePayBankTransferBankViewModel> Banks,
        string QrStatus = "",
        bool ReusedQr = false,
        string ProviderResponseJson = "",
        string DebugLog = "")
    {
        public static DirectNinePayBankTransferResult Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, null, null, [], string.Empty, false, string.Empty, string.Empty);
    }

    private sealed record QrInvoiceItem(int InvoiceId, int SubscriptionId, int TenantId, string InvoiceNumber, decimal Amount);

    private sealed record InvoicePdfPayloadContext(
        int InvoiceId,
        string InvoiceNumber,
        int InvoiceYear,
        int InvoiceSequenceInYear,
        string InvoiceType,
        string Description,
        string ReceiptNumber,
        DateTime? CompletedAt,
        DateTime? CreatedAt,
        string CreatedBy,
        string UpdatedBy,
        int SubscriptionId,
        int DeviceId,
        int TenantId,
        string TenantName,
        string VesselName,
        string KitId,
        string PlanName,
        DateTime? StartDate,
        DateTime? EndDate,
        string KitNumber,
        string DeviceCode,
        string TenantEmail,
        int QrSessionId,
        string ProviderInvoiceNo,
        string ProviderPaymentNo,
        string IpnPaymentNo,
        DateTime? IpnReceivedAt,
        string BankAccountNo,
        string TransferContent,
        string ProviderResponseJson,
        decimal InvoiceAmountVnd,
        string TransactionPaymentNo,
        decimal TransactionAmountVnd,
        DateTime? LatestTransactionAt);

    private sealed record InvoicePdfBankInfo(
        string BankAccountNo,
        string BankAccountName,
        string BankName,
        string SwiftCode,
        string BankAddressLine1,
        string BankAddressLine2,
        string TransferContent);

    private sealed record TelegramSubscriptionNotificationDetail(
        int SubscriptionId,
        string TenantName,
        int DeviceId,
        string DeviceCode,
        string KitId,
        string PlanName,
        DateTime StartDate,
        DateTime EndDate,
        decimal TotalInvoiceAmount);

    private sealed class NinePayDebugException(string message, string debugLog) : Exception(message)
    {
        public string DebugLog { get; } = debugLog;
    }

    private sealed record NinePaySignatureAttempt(
        string Name,
        bool EncodeResources,
        bool UsePathOnly,
        bool SortParameters,
        string DigestMode,
        string ContentMode,
        bool AuthorizationCommaSpaces,
        string SigningKeyMode,
        string SignedHeadersValue);
}
