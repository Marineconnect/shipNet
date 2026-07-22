/*
    Backfill payment integration log and subscription paid status for subscription 1011.
    Safe to run more than once.
*/

DECLARE @SubscriptionId int = 1011;

UPDATE s
SET [Status] = N'paid',
    [TotalInvoiceAmount] = COALESCE(inv.[TotalInvoiceAmount], 0),
    [TotalPaid] = COALESCE(inv.[TotalPaid], 0),
    [Updated_Date] = GETDATE(),
    [Updated_By] = N'payment-backfill'
FROM [dbo].[TblMonthlySubscription] s
OUTER APPLY (
    SELECT SUM(i.[Amount]) AS [TotalInvoiceAmount],
           SUM(i.[PaidAmount]) AS [TotalPaid],
           SUM(CASE
               WHEN LOWER(ISNULL(i.[Status], N'')) NOT IN (N'paid', N'refunded', N'void')
                AND ISNULL(i.[PaidAmount], 0) < ISNULL(i.[Amount], 0)
               THEN 1
               ELSE 0
           END) AS [UnpaidInvoiceCount]
    FROM [dbo].[TblSubscriptionInvoice] i
    WHERE i.[SubscriptionId] = s.[ID]
) inv
WHERE s.[ID] = @SubscriptionId
  AND COALESCE(inv.[TotalInvoiceAmount], 0) > 0
  AND COALESCE(inv.[TotalPaid], 0) >= COALESCE(inv.[TotalInvoiceAmount], 0)
  AND COALESCE(inv.[UnpaidInvoiceCount], 0) = 0;

INSERT INTO [dbo].[TblInvoiceIntegrationLog]
    ([InvoiceId], [InvoiceCode], [TransactionCode], [EventType], [Direction], [Status], [SourceSystem], [TargetSystem],
     [CorrelationId], [PayloadJson], [ErrorMessage], [StartedAtUtc], [CompletedAtUtc], [DurationMs], [CreatedAtUtc], [CreatedBy])
SELECT TOP 1
    i.[ID],
    i.[InvoiceNumber],
    COALESCE(NULLIF(q.[IpnPaymentNo], N''), NULLIF(t.[ProviderPaymentNo], N''), NULLIF(i.[ReceiptNumber], N'')),
    N'NinePayPaymentReceived',
    N'Inbound',
    N'Paid',
    N'9Pay',
    N'ShipNet',
    COALESCE(NULLIF(q.[IpnPaymentNo], N''), NULLIF(t.[ProviderPaymentNo], N''), NULLIF(i.[ReceiptNumber], N''), q.[ProviderInvoiceNo]),
    COALESCE(NULLIF(q.[IpnRawJson], N''), NULLIF(t.[RawResultJson], N'')),
    CONCAT(N'ProviderInvoiceNo=', COALESCE(q.[ProviderInvoiceNo], N''), N'; ProviderStatus=', COALESCE(q.[ProviderStatus], t.[ProviderStatus], N''), N'; AmountVnd=', COALESCE(CONVERT(nvarchar(50), q.[AmountVnd]), CONVERT(nvarchar(50), t.[AmountVnd]), N'')),
    COALESCE(q.[PaidAt], t.[CompletedAt], i.[CompletedAt], SYSUTCDATETIME()),
    SYSUTCDATETIME(),
    0,
    SYSUTCDATETIME(),
    N'payment-backfill'
FROM [dbo].[TblSubscriptionInvoice] i
LEFT JOIN [dbo].[TblNinePayQrSessionInvoice] qi ON qi.[InvoiceId] = i.[ID]
LEFT JOIN [dbo].[TblNinePayQrSession] q ON q.[ID] = qi.[QrSessionId]
LEFT JOIN [dbo].[TblPaymentTransaction] t ON t.[InvoiceId] = i.[ID]
WHERE i.[SubscriptionId] = @SubscriptionId
  AND LOWER(ISNULL(i.[Status], N'')) = N'paid'
  AND NOT EXISTS (
      SELECT 1
      FROM [dbo].[TblInvoiceIntegrationLog] log
      WHERE log.[InvoiceId] = i.[ID]
        AND log.[EventType] = N'NinePayPaymentReceived'
  )
ORDER BY i.[ID] DESC;

SELECT
    s.[ID] AS [SubscriptionId],
    s.[Status] AS [SubscriptionStatus],
    s.[TotalInvoiceAmount],
    s.[TotalPaid],
    i.[ID] AS [InvoiceId],
    i.[InvoiceNumber],
    i.[Status] AS [InvoiceStatus],
    i.[PaidAmount],
    log.[ID] AS [PaymentLogId],
    log.[EventType],
    log.[Status] AS [LogStatus]
FROM [dbo].[TblMonthlySubscription] s
INNER JOIN [dbo].[TblSubscriptionInvoice] i ON i.[SubscriptionId] = s.[ID]
LEFT JOIN [dbo].[TblInvoiceIntegrationLog] log ON log.[InvoiceId] = i.[ID] AND log.[EventType] = N'NinePayPaymentReceived'
WHERE s.[ID] = @SubscriptionId
ORDER BY i.[ID], log.[ID];
