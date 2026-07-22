/*
    Fixes pricing rows that were accidentally divided by the USD -> VND rate while VND mode was active.

    Symptom:
      User enters 6,000,000 VND, app stores about 49,950 because an old System:PricingCurrency=USD
      override was still applied.

    This script only touches recent rows updated by normal users/admins where prices look like
    accidentally-divided VND values. Adjust @updatedSince or @rateOverride before running if needed.
*/

DECLARE @rateOverride decimal(18,6) = NULL;
DECLARE @updatedSince datetime = DATEADD(day, -2, GETDATE());
DECLARE @rate decimal(18,6);

SELECT TOP 1 @rate = [Rate]
FROM [dbo].[TblCurrencyExchangeRate]
WHERE UPPER([FromCurrency]) = N'USD'
  AND UPPER([ToCurrency]) = N'VND'
  AND LOWER(ISNULL([Status], N'')) = N'active'
  AND CONVERT(date, [EffectiveDate]) <= CONVERT(date, GETDATE())
ORDER BY [EffectiveDate] DESC, [ID] DESC;

SET @rate = COALESCE(@rateOverride, @rate);

IF @rate IS NULL OR @rate <= 0
BEGIN
    THROW 50004, 'Missing USD -> VND rate. Set @rateOverride to the rate that produced the divided prices.', 1;
END;

BEGIN TRANSACTION;

IF OBJECT_ID(N'[dbo].[TblPricingPlan]', N'U') IS NOT NULL
BEGIN
    UPDATE [dbo].[TblPricingPlan]
    SET [ResellerPrice] = CASE WHEN [ResellerPrice] > 0 AND [ResellerPrice] < 100000 THEN ROUND([ResellerPrice] * @rate, 0) ELSE [ResellerPrice] END,
        [FinalPrice] = CASE WHEN [FinalPrice] > 0 AND [FinalPrice] < 100000 THEN ROUND([FinalPrice] * @rate, 0) ELSE [FinalPrice] END,
        [ResellerOverChargePrice] = CASE WHEN [ResellerOverChargePrice] > 0 AND [ResellerOverChargePrice] < 100000 THEN ROUND([ResellerOverChargePrice] * @rate, 0) ELSE [ResellerOverChargePrice] END,
        [FinalOverChargePrice] = CASE WHEN [FinalOverChargePrice] > 0 AND [FinalOverChargePrice] < 100000 THEN ROUND([FinalOverChargePrice] * @rate, 0) ELSE [FinalOverChargePrice] END,
        [Updated_Date] = GETDATE(),
        [Updated_By] = N'pricing-vnd-divide-fix'
    WHERE [Updated_Date] >= @updatedSince
      AND ISNULL([Updated_By], N'') NOT IN (N'pricing-vnd-migration')
      AND (
          ([ResellerPrice] > 0 AND [ResellerPrice] < 100000)
          OR ([FinalPrice] > 0 AND [FinalPrice] < 100000)
          OR ([ResellerOverChargePrice] > 0 AND [ResellerOverChargePrice] < 100000)
          OR ([FinalOverChargePrice] > 0 AND [FinalOverChargePrice] < 100000)
      );
END;

IF OBJECT_ID(N'[dbo].[TblTenantPricing]', N'U') IS NOT NULL
BEGIN
    UPDATE [dbo].[TblTenantPricing]
    SET [ResellerPrice] = CASE WHEN [ResellerPrice] > 0 AND [ResellerPrice] < 100000 THEN ROUND([ResellerPrice] * @rate, 0) ELSE [ResellerPrice] END,
        [FinalPrice] = CASE WHEN [FinalPrice] > 0 AND [FinalPrice] < 100000 THEN ROUND([FinalPrice] * @rate, 0) ELSE [FinalPrice] END,
        [ResellerOverChargePrice] = CASE WHEN [ResellerOverChargePrice] > 0 AND [ResellerOverChargePrice] < 100000 THEN ROUND([ResellerOverChargePrice] * @rate, 0) ELSE [ResellerOverChargePrice] END,
        [FinalOverChargePrice] = CASE WHEN [FinalOverChargePrice] > 0 AND [FinalOverChargePrice] < 100000 THEN ROUND([FinalOverChargePrice] * @rate, 0) ELSE [FinalOverChargePrice] END,
        [Updated_Date] = GETDATE(),
        [Updated_By] = N'pricing-vnd-divide-fix'
    WHERE [Updated_Date] >= @updatedSince
      AND ISNULL([Updated_By], N'') NOT IN (N'pricing-vnd-migration')
      AND (
          ([ResellerPrice] > 0 AND [ResellerPrice] < 100000)
          OR ([FinalPrice] > 0 AND [FinalPrice] < 100000)
          OR ([ResellerOverChargePrice] > 0 AND [ResellerOverChargePrice] < 100000)
          OR ([FinalOverChargePrice] > 0 AND [FinalOverChargePrice] < 100000)
      );
END;

IF OBJECT_ID(N'[dbo].[TblDevicePricing]', N'U') IS NOT NULL
BEGIN
    UPDATE [dbo].[TblDevicePricing]
    SET [ResellerPrice] = CASE WHEN [ResellerPrice] > 0 AND [ResellerPrice] < 100000 THEN ROUND([ResellerPrice] * @rate, 0) ELSE [ResellerPrice] END,
        [FinalPrice] = CASE WHEN [FinalPrice] > 0 AND [FinalPrice] < 100000 THEN ROUND([FinalPrice] * @rate, 0) ELSE [FinalPrice] END,
        [ResellerOverChargePrice] = CASE WHEN [ResellerOverChargePrice] > 0 AND [ResellerOverChargePrice] < 100000 THEN ROUND([ResellerOverChargePrice] * @rate, 0) ELSE [ResellerOverChargePrice] END,
        [FinalOverChargePrice] = CASE WHEN [FinalOverChargePrice] > 0 AND [FinalOverChargePrice] < 100000 THEN ROUND([FinalOverChargePrice] * @rate, 0) ELSE [FinalOverChargePrice] END,
        [Updated_Date] = GETDATE(),
        [Updated_By] = N'pricing-vnd-divide-fix'
    WHERE [Updated_Date] >= @updatedSince
      AND ISNULL([Updated_By], N'') NOT IN (N'pricing-vnd-migration')
      AND (
          ([ResellerPrice] > 0 AND [ResellerPrice] < 100000)
          OR ([FinalPrice] > 0 AND [FinalPrice] < 100000)
          OR ([ResellerOverChargePrice] > 0 AND [ResellerOverChargePrice] < 100000)
          OR ([FinalOverChargePrice] > 0 AND [FinalOverChargePrice] < 100000)
      );
END;

COMMIT TRANSACTION;

SELECT @rate AS [FixRateUsed], @updatedSince AS [UpdatedSince];
