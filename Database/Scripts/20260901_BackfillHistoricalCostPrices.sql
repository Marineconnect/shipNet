/*
    Backfill historical billing cost snapshots.

    Default mode is preview-only. Review the result sets first, then set:
        DECLARE @PreviewOnly bit = 0;

    Historical pricing limitation:
    This schema has TblAudit/TblAuditLog text audit tables, but no pricing version
    table or effective-dated pricing history that can reliably reconstruct the cost
    at Subscription.StartDate/UsageMonth. This script therefore uses current
    TblPricingPlan.CostPrice and TblPricingPlan.CostOverChargePrice as a one-time
    fallback and only fills rows where the existing cost snapshot is still 0.

    Rounding:
    SQL Server ROUND uses commercial half-away-from-zero rounding. Current billing
    values are non-negative, matching the C# Math.Round(..., 2,
    MidpointRounding.AwayFromZero) behavior used by MonthlySubscriptionService.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @PreviewOnly bit = 1;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'[dbo].[TblMonthlySubscription]', N'U') IS NULL
        THROW 51000, 'TblMonthlySubscription does not exist.', 1;
    IF OBJECT_ID(N'[dbo].[TblSubscriptionInvoice]', N'U') IS NULL
        THROW 51001, 'TblSubscriptionInvoice does not exist.', 1;
    IF OBJECT_ID(N'[dbo].[TblPricingPlan]', N'U') IS NULL
        THROW 51002, 'TblPricingPlan does not exist.', 1;

    IF COL_LENGTH(N'[dbo].[TblMonthlySubscription]', N'CostPrice') IS NULL
        THROW 51003, 'TblMonthlySubscription.CostPrice does not exist.', 1;
    IF COL_LENGTH(N'[dbo].[TblMonthlySubscription]', N'CostOverChargePrice') IS NULL
        THROW 51004, 'TblMonthlySubscription.CostOverChargePrice does not exist.', 1;
    IF COL_LENGTH(N'[dbo].[TblSubscriptionInvoice]', N'CostPrice') IS NULL
        THROW 51005, 'TblSubscriptionInvoice.CostPrice does not exist.', 1;
    IF COL_LENGTH(N'[dbo].[TblPricingPlan]', N'CostPrice') IS NULL
        THROW 51006, 'TblPricingPlan.CostPrice does not exist.', 1;
    IF COL_LENGTH(N'[dbo].[TblPricingPlan]', N'CostOverChargePrice') IS NULL
        THROW 51007, 'TblPricingPlan.CostOverChargePrice does not exist.', 1;

    SELECT
        @PreviewOnly AS [PreviewOnly],
        (SELECT COUNT(1) FROM [dbo].[TblMonthlySubscription] WHERE [CostPrice] = 0) AS [TotalSubscriptionsCostPriceZero],
        (SELECT COUNT(1) FROM [dbo].[TblMonthlySubscription] WHERE [CostOverChargePrice] = 0) AS [TotalSubscriptionsCostOverChargePriceZero],
        (SELECT COUNT(1) FROM [dbo].[TblSubscriptionInvoice] WHERE [CostPrice] = 0 AND UPPER([InvoiceType]) = N'SUBSCRIPTION') AS [TotalSubscriptionInvoicesCostPriceZero],
        (SELECT COUNT(1) FROM [dbo].[TblSubscriptionInvoice] WHERE [CostPrice] = 0 AND UPPER([InvoiceType]) = N'OVERCHARGE') AS [TotalOverchargeInvoicesCostPriceZero];

    WITH SubscriptionCostPreview AS
    (
        SELECT TOP (50)
            s.[ID] AS [SubscriptionId],
            CAST(NULL AS int) AS [InvoiceId],
            CAST(N'SUBSCRIPTION_COSTPRICE' AS nvarchar(50)) AS [InvoiceType],
            s.[PricingPlanId],
            p.[PlanName],
            s.[CostPrice] AS [CurrentCost],
            CASE
                WHEN CONVERT(date, s.[EndDate]) < CONVERT(date, s.[StartDate]) THEN CAST(0 AS decimal(18,2))
                WHEN CONVERT(date, s.[EndDate]) = EOMONTH(CONVERT(date, s.[StartDate]))
                     AND (DATEPART(day, CONVERT(date, s.[StartDate])) = 1
                          OR (DAY(EOMONTH(CONVERT(date, s.[StartDate]))) = 31
                              AND DATEPART(day, CONVERT(date, s.[StartDate])) = 2))
                    THEN ROUND(CASE WHEN p.[CostPrice] < 0 THEN 0 ELSE p.[CostPrice] END, 2)
                ELSE ROUND(
                    ROUND(CASE WHEN p.[CostPrice] < 0 THEN 0 ELSE p.[CostPrice] END, 2)
                    * (DATEDIFF(day, CONVERT(date, s.[StartDate]), CONVERT(date, s.[EndDate])) + 1) / CAST(30 AS decimal(18,2)),
                    2)
            END AS [NewCost],
            s.[StartDate],
            s.[EndDate],
            CAST(NULL AS decimal(18,2)) AS [DataGb]
        FROM [dbo].[TblMonthlySubscription] s
        INNER JOIN [dbo].[TblPricingPlan] p ON p.[ID] = s.[PricingPlanId]
        WHERE s.[CostPrice] = 0
          AND p.[CostPrice] > 0
        ORDER BY s.[ID]
    ),
    SubscriptionOverchargePreview AS
    (
        SELECT TOP (50)
            s.[ID] AS [SubscriptionId],
            CAST(NULL AS int) AS [InvoiceId],
            CAST(N'SUBSCRIPTION_OVERCHARGE_COST' AS nvarchar(50)) AS [InvoiceType],
            s.[PricingPlanId],
            p.[PlanName],
            s.[CostOverChargePrice] AS [CurrentCost],
            p.[CostOverChargePrice] AS [NewCost],
            s.[StartDate],
            s.[EndDate],
            CAST(NULL AS decimal(18,2)) AS [DataGb]
        FROM [dbo].[TblMonthlySubscription] s
        INNER JOIN [dbo].[TblPricingPlan] p ON p.[ID] = s.[PricingPlanId]
        WHERE s.[CostOverChargePrice] = 0
          AND p.[CostOverChargePrice] > 0
        ORDER BY s.[ID]
    ),
    InvoicePreview AS
    (
        SELECT TOP (50)
            s.[ID] AS [SubscriptionId],
            i.[ID] AS [InvoiceId],
            i.[InvoiceType],
            s.[PricingPlanId],
            s.[PlanName],
            i.[CostPrice] AS [CurrentCost],
            CASE
                WHEN UPPER(i.[InvoiceType]) = N'SUBSCRIPTION'
                    THEN s.[CostPrice]
                WHEN UPPER(i.[InvoiceType]) = N'OVERCHARGE'
                    THEN ROUND(i.[DataGb] * s.[CostOverChargePrice], 2)
                ELSE CAST(0 AS decimal(18,2))
            END AS [NewCost],
            s.[StartDate],
            s.[EndDate],
            i.[DataGb]
        FROM [dbo].[TblSubscriptionInvoice] i
        INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
        WHERE i.[CostPrice] = 0
          AND (
                (UPPER(i.[InvoiceType]) = N'SUBSCRIPTION' AND s.[CostPrice] > 0)
             OR (UPPER(i.[InvoiceType]) = N'OVERCHARGE' AND i.[DataGb] > 0 AND s.[CostOverChargePrice] > 0)
          )
        ORDER BY i.[ID]
    )
    SELECT TOP (50)
        [SubscriptionId],
        [InvoiceId],
        [InvoiceType],
        [PricingPlanId],
        [PlanName],
        [CurrentCost],
        [NewCost],
        [StartDate],
        [EndDate],
        [DataGb]
    FROM
    (
        SELECT * FROM SubscriptionCostPreview
        UNION ALL
        SELECT * FROM SubscriptionOverchargePreview
        UNION ALL
        SELECT * FROM InvoicePreview
    ) preview
    ORDER BY [SubscriptionId], [InvoiceId], [InvoiceType];

    SELECT
        UPPER([InvoiceType]) AS [InvoiceType],
        COUNT(1) AS [InvoiceCount],
        SUM(CASE WHEN [CostPrice] = 0 THEN 1 ELSE 0 END) AS [CostPriceZeroCount]
    FROM [dbo].[TblSubscriptionInvoice]
    GROUP BY UPPER([InvoiceType])
    ORDER BY UPPER([InvoiceType]);

    DECLARE
        @SubscriptionsCostPriceUpdated int = 0,
        @SubscriptionsCostOverChargeUpdated int = 0,
        @SubscriptionInvoicesUpdated int = 0,
        @OverchargeInvoicesUpdated int = 0;

    IF @PreviewOnly = 0
    BEGIN
        WITH CalculatedSubscriptionCost AS
        (
            SELECT
                s.[ID],
                CASE
                    WHEN CONVERT(date, s.[EndDate]) < CONVERT(date, s.[StartDate]) THEN CAST(0 AS decimal(18,2))
                    WHEN CONVERT(date, s.[EndDate]) = EOMONTH(CONVERT(date, s.[StartDate]))
                         AND (DATEPART(day, CONVERT(date, s.[StartDate])) = 1
                              OR (DAY(EOMONTH(CONVERT(date, s.[StartDate]))) = 31
                                  AND DATEPART(day, CONVERT(date, s.[StartDate])) = 2))
                        THEN ROUND(CASE WHEN p.[CostPrice] < 0 THEN 0 ELSE p.[CostPrice] END, 2)
                    ELSE ROUND(
                        ROUND(CASE WHEN p.[CostPrice] < 0 THEN 0 ELSE p.[CostPrice] END, 2)
                        * (DATEDIFF(day, CONVERT(date, s.[StartDate]), CONVERT(date, s.[EndDate])) + 1) / CAST(30 AS decimal(18,2)),
                        2)
                END AS [NewCostPrice]
            FROM [dbo].[TblMonthlySubscription] s
            INNER JOIN [dbo].[TblPricingPlan] p ON p.[ID] = s.[PricingPlanId]
            WHERE s.[CostPrice] = 0
              AND p.[CostPrice] > 0
        )
        UPDATE s
        SET s.[CostPrice] = c.[NewCostPrice]
        FROM [dbo].[TblMonthlySubscription] s
        INNER JOIN CalculatedSubscriptionCost c ON c.[ID] = s.[ID]
        WHERE s.[CostPrice] = 0
          AND c.[NewCostPrice] > 0;

        SET @SubscriptionsCostPriceUpdated = @@ROWCOUNT;

        UPDATE s
        SET s.[CostOverChargePrice] = p.[CostOverChargePrice]
        FROM [dbo].[TblMonthlySubscription] s
        INNER JOIN [dbo].[TblPricingPlan] p ON p.[ID] = s.[PricingPlanId]
        WHERE s.[CostOverChargePrice] = 0
          AND p.[CostOverChargePrice] > 0;

        SET @SubscriptionsCostOverChargeUpdated = @@ROWCOUNT;

        UPDATE i
        SET i.[CostPrice] = s.[CostPrice]
        FROM [dbo].[TblSubscriptionInvoice] i
        INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
        WHERE i.[CostPrice] = 0
          AND UPPER(i.[InvoiceType]) = N'SUBSCRIPTION'
          AND s.[CostPrice] > 0;

        SET @SubscriptionInvoicesUpdated = @@ROWCOUNT;

        UPDATE i
        SET i.[CostPrice] = ROUND(i.[DataGb] * s.[CostOverChargePrice], 2)
        FROM [dbo].[TblSubscriptionInvoice] i
        INNER JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
        WHERE i.[CostPrice] = 0
          AND UPPER(i.[InvoiceType]) = N'OVERCHARGE'
          AND i.[DataGb] > 0
          AND s.[CostOverChargePrice] > 0
          AND ROUND(i.[DataGb] * s.[CostOverChargePrice], 2) > 0;

        SET @OverchargeInvoicesUpdated = @@ROWCOUNT;
    END;

    SELECT
        @PreviewOnly AS [PreviewOnly],
        @SubscriptionsCostPriceUpdated AS [SubscriptionsUpdatedCostPrice],
        @SubscriptionsCostOverChargeUpdated AS [SubscriptionsUpdatedCostOverChargePrice],
        @SubscriptionInvoicesUpdated AS [SubscriptionInvoicesUpdated],
        @OverchargeInvoicesUpdated AS [OverchargeInvoicesUpdated],
        (SELECT COUNT(1) FROM [dbo].[TblMonthlySubscription] WHERE [CostPrice] = 0) AS [SubscriptionsStillCostPriceZero],
        (SELECT COUNT(1) FROM [dbo].[TblMonthlySubscription] WHERE [CostOverChargePrice] = 0) AS [SubscriptionsStillCostOverChargePriceZero],
        (SELECT COUNT(1) FROM [dbo].[TblSubscriptionInvoice] WHERE [CostPrice] = 0) AS [InvoicesStillCostPriceZero];

    SELECT
        reason.[RecordType],
        reason.[Reason],
        COUNT(1) AS [RecordsStillZero]
    FROM
    (
        SELECT
            N'SUBSCRIPTION_COSTPRICE' AS [RecordType],
            CASE
                WHEN p.[ID] IS NULL THEN N'PricingPlan missing'
                WHEN p.[CostPrice] = 0 THEN N'PricingPlan CostPrice = 0'
                WHEN CONVERT(date, s.[EndDate]) < CONVERT(date, s.[StartDate]) THEN N'Invalid subscription date range'
                WHEN DATEDIFF(day, CONVERT(date, s.[StartDate]), CONVERT(date, s.[EndDate])) + 1 <= 0 THEN N'Calculated subscription days <= 0'
                ELSE N'Calculated CostPrice = 0'
            END AS [Reason]
        FROM [dbo].[TblMonthlySubscription] s
        LEFT JOIN [dbo].[TblPricingPlan] p ON p.[ID] = s.[PricingPlanId]
        WHERE s.[CostPrice] = 0

        UNION ALL

        SELECT
            N'SUBSCRIPTION_OVERCHARGE_COST' AS [RecordType],
            CASE
                WHEN p.[ID] IS NULL THEN N'PricingPlan missing'
                WHEN p.[CostOverChargePrice] = 0 THEN N'PricingPlan CostOverChargePrice = 0'
                ELSE N'Calculated CostOverChargePrice = 0'
            END AS [Reason]
        FROM [dbo].[TblMonthlySubscription] s
        LEFT JOIN [dbo].[TblPricingPlan] p ON p.[ID] = s.[PricingPlanId]
        WHERE s.[CostOverChargePrice] = 0

        UNION ALL

        SELECT
            N'INVOICE' AS [RecordType],
            CASE
                WHEN s.[ID] IS NULL THEN N'Subscription missing'
                WHEN UPPER(i.[InvoiceType]) NOT IN (N'SUBSCRIPTION', N'OVERCHARGE') THEN N'Unknown InvoiceType'
                WHEN UPPER(i.[InvoiceType]) = N'SUBSCRIPTION' AND s.[CostPrice] = 0 THEN N'Subscription CostPrice = 0'
                WHEN UPPER(i.[InvoiceType]) = N'OVERCHARGE' AND i.[DataGb] = 0 THEN N'DataGb = 0'
                WHEN UPPER(i.[InvoiceType]) = N'OVERCHARGE' AND s.[CostOverChargePrice] = 0 THEN N'Subscription CostOverChargePrice = 0'
                WHEN UPPER(i.[InvoiceType]) = N'OVERCHARGE' AND ROUND(i.[DataGb] * s.[CostOverChargePrice], 2) = 0 THEN N'Calculated overcharge CostPrice = 0'
                ELSE N'No eligible cost source'
            END AS [Reason]
        FROM [dbo].[TblSubscriptionInvoice] i
        LEFT JOIN [dbo].[TblMonthlySubscription] s ON s.[ID] = i.[SubscriptionId]
        WHERE i.[CostPrice] = 0
    ) reason
    GROUP BY reason.[RecordType], reason.[Reason]
    ORDER BY reason.[RecordType], reason.[Reason];

    IF @PreviewOnly = 1
    BEGIN
        ROLLBACK;
        SELECT N'Preview completed. No data was updated because @PreviewOnly = 1.' AS [Message];
    END
    ELSE
    BEGIN
        COMMIT;
        SELECT N'Backfill committed.' AS [Message];
    END
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK;
    THROW;
END CATCH;
