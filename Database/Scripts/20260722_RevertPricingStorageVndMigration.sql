/*
    Reverts the accidental pricing-vnd-migration multiplication.

    Context:
      Some pricing rows were already stored as VND, but 20260722_ConvertPricingStorageToVnd.sql
      multiplied them by the USD -> VND rate. This script divides only rows that were touched
      by that migration marker:

        Updated_By = 'pricing-vnd-migration'

    Before running:
      1. Review the active USD -> VND rate selected below.
      2. If the selected rate is not the exact rate used by the bad migration, set @rateOverride.
      3. Backup the affected rows or run inside a manual transaction in SSMS first.

    Example preview:
      SELECT TOP 20 * FROM dbo.TblTenantPricing WHERE Updated_By = N'pricing-vnd-migration';
*/

DECLARE @rateOverride decimal(18,6) = NULL;
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
    THROW 50003, 'Missing USD -> VND rate. Set @rateOverride to the rate used by pricing-vnd-migration.', 1;
END;

BEGIN TRANSACTION;

IF OBJECT_ID(N'[dbo].[TblPricingPlan]', N'U') IS NOT NULL
BEGIN
    UPDATE [dbo].[TblPricingPlan]
    SET [ResellerPrice] = ROUND([ResellerPrice] / @rate, 0),
        [FinalPrice] = ROUND([FinalPrice] / @rate, 0),
        [ResellerOverChargePrice] = ROUND([ResellerOverChargePrice] / @rate, 0),
        [FinalOverChargePrice] = ROUND([FinalOverChargePrice] / @rate, 0),
        [Updated_Date] = GETDATE(),
        [Updated_By] = N'pricing-vnd-revert'
    WHERE [Updated_By] = N'pricing-vnd-migration';
END;

IF OBJECT_ID(N'[dbo].[TblTenantPricing]', N'U') IS NOT NULL
BEGIN
    UPDATE [dbo].[TblTenantPricing]
    SET [ResellerPrice] = ROUND([ResellerPrice] / @rate, 0),
        [FinalPrice] = ROUND([FinalPrice] / @rate, 0),
        [ResellerOverChargePrice] = ROUND([ResellerOverChargePrice] / @rate, 0),
        [FinalOverChargePrice] = ROUND([FinalOverChargePrice] / @rate, 0),
        [Updated_Date] = GETDATE(),
        [Updated_By] = N'pricing-vnd-revert'
    WHERE [Updated_By] = N'pricing-vnd-migration';
END;

IF OBJECT_ID(N'[dbo].[TblDevicePricing]', N'U') IS NOT NULL
BEGIN
    UPDATE [dbo].[TblDevicePricing]
    SET [ResellerPrice] = ROUND([ResellerPrice] / @rate, 0),
        [FinalPrice] = ROUND([FinalPrice] / @rate, 0),
        [ResellerOverChargePrice] = ROUND([ResellerOverChargePrice] / @rate, 0),
        [FinalOverChargePrice] = ROUND([FinalOverChargePrice] / @rate, 0),
        [Updated_Date] = GETDATE(),
        [Updated_By] = N'pricing-vnd-revert'
    WHERE [Updated_By] = N'pricing-vnd-migration';
END;

IF OBJECT_ID(N'[dbo].[TblSystemSetting]', N'U') IS NOT NULL
BEGIN
    UPDATE [dbo].[TblSystemSetting]
    SET [Description] = N'Pricing values are stored directly in VND. Accidental migration multiplication was reverted.',
        [Updated_Date] = GETDATE(),
        [Updated_By] = N'pricing-vnd-revert'
    WHERE [SettingCode] = N'pricing_storage_currency'
      AND UPPER(ISNULL([SettingValue], N'')) = N'VND';
END;

COMMIT TRANSACTION;

SELECT @rate AS [RevertRateUsed];
