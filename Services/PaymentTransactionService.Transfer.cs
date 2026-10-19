using System.Data;
using Microsoft.Data.SqlClient;
using StarlinkDeviceManager.Models;

namespace StarlinkDeviceManager.Services;

public partial class PaymentTransactionService
{
    private static async Task<TransferInvoiceCandidate> ReadTransferCandidateAsync(SqlConnection connection, SqlTransaction transaction, string code, int? tenantId, int? deviceId, CancellationToken ct)
    {
        const string sql = """
            SELECT i.ID, i.SubscriptionId, i.InvoiceNumber, i.Amount,
                CASE WHEN i.Status <> N'pending' OR i.PaidAmount <> 0 OR i.RefundAmount <> 0
                  OR NULLIF(i.ReceiptNumber, N'') IS NOT NULL
                  OR EXISTS (SELECT 1 FROM dbo.TblPaymentTransaction WITH (UPDLOCK,HOLDLOCK) WHERE InvoiceId = i.ID OR InvoiceNumber = i.InvoiceNumber)
                  OR EXISTS (SELECT 1 FROM dbo.TblPaymentTransfer WITH (UPDLOCK,HOLDLOCK) WHERE TargetInvoiceId = i.ID)
                THEN 1 ELSE 0 END AS Unavailable
            FROM dbo.TblSubscriptionInvoice i WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.TblMonthlySubscription s WITH (UPDLOCK,HOLDLOCK) ON s.ID = i.SubscriptionId
            WHERE i.InvoiceNumber = @code AND (@tenant IS NULL OR s.TenantId = @tenant)
              AND (@device IS NULL OR s.DeviceId = @device) AND s.Status <> N'terminated'
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@code", code);
        command.Parameters.Add("@tenant", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.Add("@device", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException($"Invoice {code} không tồn tại hoặc không thuộc phạm vi được phép.");
        if (reader.GetInt32(4) != 0) throw new InvalidOperationException($"Invoice {code} đã mapping transaction hoặc không còn ở trạng thái chờ thanh toán.");
        var result = new TransferInvoiceCandidate(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetDecimal(3));
        if (result.Amount <= 0) throw new InvalidOperationException($"Invoice {code} không có số tiền hợp lệ.");
        if (await reader.ReadAsync(ct)) throw new InvalidOperationException($"Mã invoice {code} bị trùng trong hệ thống.");
        return result;
    }

    public async Task<TransferInvoiceCandidate> ValidateTransferInvoiceAsync(string code, int? tenantId, int? deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 100) throw new InvalidOperationException("Vui lòng nhập mã invoice hợp lệ.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        var result = await ReadTransferCandidateAsync(connection, transaction, code.Trim(), tenantId, deviceId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task TransferTransactionAsync(int sourceInvoiceId, IReadOnlyList<string> codes, string reason, string actor, int? tenantId, int? deviceId, CancellationToken cancellationToken = default)
    {
        reason = reason.Trim();
        if (reason.Length is < 1 or > 1000) throw new InvalidOperationException("Bắt buộc nhập lý do chuyển (tối đa 1000 ký tự).");
        if (codes.Count is < 1 or > 100 || codes.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 100) || codes.Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != codes.Count)
            throw new InvalidOperationException("Danh sách phải có 1–100 mã invoice hợp lệ, không trùng nhau.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await EnsureSchemaAsync(connection, transaction, cancellationToken);
        const string sourceSql = """
            SELECT i.InvoiceNumber, i.SubscriptionId, i.PaidAmount, pt.ID, pt.ProviderPaymentNo
            FROM dbo.TblSubscriptionInvoice i WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.TblMonthlySubscription s WITH (UPDLOCK,HOLDLOCK) ON s.ID = i.SubscriptionId
            CROSS APPLY (SELECT TOP 1 p.* FROM dbo.TblPaymentTransaction p WITH (UPDLOCK,HOLDLOCK)
                WHERE (p.InvoiceId = i.ID OR p.InvoiceNumber = i.InvoiceNumber OR p.ProviderPaymentNo = i.ReceiptNumber
                    OR EXISTS (SELECT 1 FROM dbo.TblPaymentTransfer tr WHERE tr.TargetInvoiceId = i.ID AND tr.TransactionId = p.ID))
                  AND p.Provider = N'9Pay' AND p.Status = N'paid' AND NULLIF(p.ProviderPaymentNo,N'') IS NOT NULL
                ORDER BY p.ID DESC) pt
            WHERE i.ID = @id AND i.Status = N'paid' AND i.PaidAmount > 0 AND i.RefundAmount = 0
              AND (@tenant IS NULL OR s.TenantId = @tenant) AND (@device IS NULL OR s.DeviceId = @device)
              AND NOT EXISTS (SELECT 1 FROM dbo.TblPaymentTransfer WITH (UPDLOCK,HOLDLOCK) WHERE SourceInvoiceId = i.ID)
            """;
        string sourceCode, paymentNo; int sourceSubscription, transactionId; decimal paid;
        await using (var command = new SqlCommand(sourceSql, connection, transaction))
        {
            command.Parameters.AddWithValue("@id", sourceInvoiceId);
            command.Parameters.Add("@tenant", SqlDbType.Int).Value = (object?)tenantId ?? DBNull.Value;
            command.Parameters.Add("@device", SqlDbType.Int).Value = (object?)deviceId ?? DBNull.Value;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Invoice nguồn phải đã thanh toán qua 9Pay, chưa hoàn tiền và chưa chuyển transaction.");
            sourceCode = reader.GetString(0); sourceSubscription = reader.GetInt32(1); paid = reader.GetDecimal(2); transactionId = reader.GetInt32(3); paymentNo = reader.GetString(4);
        }
        var targets = new List<TransferInvoiceCandidate>();
        foreach (var code in codes.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            var target = await ReadTransferCandidateAsync(connection, transaction, code.Trim(), tenantId, deviceId, cancellationToken);
            if (target.SubscriptionId == sourceSubscription) throw new InvalidOperationException("Invoice nhận phải thuộc billing khác billing nguồn sẽ bị hủy.");
            targets.Add(target);
        }
        if (targets.Sum(t => t.Amount) != paid) throw new InvalidOperationException($"Tổng invoice nhận phải bằng số tiền chuyển {paid:N0} VND.");
        foreach (var target in targets)
        {
            const string sql = """
                INSERT dbo.TblPaymentTransfer (SourceInvoiceId,TargetInvoiceId,TransactionId,PaymentNo,SourceCode,TargetCode,Amount,Reason,CreatedBy)
                VALUES (@source,@target,@tx,@payment,@sourceCode,@targetCode,@amount,@reason,@actor);
                UPDATE dbo.TblSubscriptionInvoice SET PaidAmount = Amount, Status = N'paid', ReceiptNumber = @payment,
                    CompletedAt = GETUTCDATE(), Updated_Date = GETDATE(), Updated_By = @actor WHERE ID = @target;
                """;
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@source", sourceInvoiceId); command.Parameters.AddWithValue("@target", target.Id);
            command.Parameters.AddWithValue("@tx", transactionId); command.Parameters.AddWithValue("@payment", paymentNo);
            command.Parameters.AddWithValue("@sourceCode", sourceCode); command.Parameters.AddWithValue("@targetCode", target.InvoiceNumber);
            command.Parameters.AddWithValue("@amount", target.Amount); command.Parameters.AddWithValue("@reason", reason); command.Parameters.AddWithValue("@actor", actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var command = new SqlCommand("UPDATE dbo.TblSubscriptionInvoice SET PaidAmount = 0, Status = N'void', Updated_Date = GETDATE(), Updated_By = @actor WHERE ID = @id; UPDATE dbo.TblMonthlySubscription SET Status = N'terminated', Updated_Date = GETDATE(), Updated_By = @actor WHERE ID = @subscription;", connection, transaction))
        {
            command.Parameters.AddWithValue("@id", sourceInvoiceId); command.Parameters.AddWithValue("@subscription", sourceSubscription); command.Parameters.AddWithValue("@actor", actor);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var id in targets.Select(t => t.SubscriptionId).Append(sourceSubscription).Distinct())
            await RecalculateSubscriptionTotalsAsync(connection, transaction, id, cancellationToken);
        // Preserve the explicit cancellation even when other invoices on the source are paid.
        await using (var command = new SqlCommand("UPDATE dbo.TblMonthlySubscription SET Status = N'terminated' WHERE ID = @id", connection, transaction))
        {
            command.Parameters.AddWithValue("@id", sourceSubscription);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var audit = $"9Pay transaction {paymentNo}: {sourceCode} -> {string.Join(", ", targets.Select(t => t.InvoiceNumber))}. Reason: {reason}. Actor: {actor}.";
        foreach (var id in targets.Select(t => t.SubscriptionId).Append(sourceSubscription).Distinct())
            await InsertAuditAsync(connection, transaction, id, audit, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
