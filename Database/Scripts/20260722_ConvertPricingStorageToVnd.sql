/*
    Converts pricing storage from USD-based values to VND-based values.

    Use when moving System:PricingCurrency from USD to VND.
    Safe guard: this script records a marker in TblSystemSetting and will not convert twice.

    Review the selected rate before running:
      SELECT TOP 1 * FROM dbo.TblCurrencyExchangeRate
      WHERE FromCurrency = N'USD' AND ToCurrency = N'VND' AND LOWER(Status) = N'active'
      ORDER BY EffectiveDate DESC, ID DESC;
*/

IF OBJECT_ID(N'[dbo].[TblSystemSetting]', N'U') IS NULL
BEGIN
    THROW 50001, 'TblSystemSetting does not exist. Stop migration.', 1;
END;

IF NOT EXISTS (
    SELECT 1
    FROM [dbo].[TblSystemSetting]
    WHERE [SettingCode] = N'pricing_storage_currency'
      AND UPPER(ISNULL([SettingValue], N'')) = N'VND'
)
BEGIN
    DECLARE @rate decimal(18,6);

    SELECT TOP 1 @rate = [Rate]
    FROM [dbo].[TblCurrencyExchangeRate]
    WHERE UPPER([FromCurrency]) = N'USD'
      AND UPPER([ToCurrency]) = N'VND'
      AND LOWER(ISNULL([Status], N'')) = N'active'
      AND CONVERT(date, [EffectiveDate]) <= CONVERT(date, GETDATE())
    ORDER BY [EffectiveDate] DESC, [ID] DESC;

    IF @rate IS NULL OR @rate <= 0
    BEGIN
        THROW 50002, 'Missing active USD -> VND exchange rate. Stop migration.', 1;
    END;

    BEGIN TRANSACTION;

    IF OBJECT_ID(N'[dbo].[TblPricingPlan]', N'U') IS NOT NULL
    BEGIN
        UPDATE [dbo].[TblPricingPlan]
        SET [ResellerPrice] = ROUND([ResellerPrice] * @rate, 0),
            [FinalPrice] = ROUND([FinalPrice] * @rate, 0),
            [ResellerOverChargePrice] = ROUND([ResellerOverChargePrice] * @rate, 0),
            [FinalOverChargePrice] = ROUND([FinalOverChargePrice] * @rate, 0),
            [Updated_Date] = GETDATE(),
            [Updated_By] = N'pricing-vnd-migration';
    END;

    IF OBJECT_ID(N'[dbo].[TblTenantPricing]', N'U') IS NOT NULL
    BEGIN
        UPDATE [dbo].[TblTenantPricing]
        SET [ResellerPrice] = ROUND([ResellerPrice] * @rate, 0),
            [FinalPrice] = ROUND([FinalPrice] * @rate, 0),
            [ResellerOverChargePrice] = ROUND([ResellerOverChargePrice] * @rate, 0),
            [FinalOverChargePrice] = ROUND([FinalOverChargePrice] * @rate, 0),
            [Updated_Date] = GETDATE(),
            [Updated_By] = N'pricing-vnd-migration';
    END;

    IF OBJECT_ID(N'[dbo].[TblDevicePricing]', N'U') IS NOT NULL
    BEGIN
        UPDATE [dbo].[TblDevicePricing]
        SET [ResellerPrice] = ROUND([ResellerPrice] * @rate, 0),
            [FinalPrice] = ROUND([FinalPrice] * @rate, 0),
            [ResellerOverChargePrice] = ROUND([ResellerOverChargePrice] * @rate, 0),
            [FinalOverChargePrice] = ROUND([FinalOverChargePrice] * @rate, 0),
            [Updated_Date] = GETDATE(),
            [Updated_By] = N'pricing-vnd-migration';
    END;

    INSERT INTO [dbo].[TblSystemSetting]
        ([Category], [SettingCode], [DisplayName], [SettingValue], [IsSecret], [Description], [DisplayOrder], [Created_Date], [Created_By], [Updated_Date], [Updated_By])
    VALUES
        (N'system', N'pricing_storage_currency', N'Pricing Storage Currency', N'VND', 0, N'Pricing values are stored directly in VND.', 99, GETDATE(), N'pricing-vnd-migration', GETDATE(), N'pricing-vnd-migration');

    COMMIT TRANSACTION;
END;
