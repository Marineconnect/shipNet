IF COL_LENGTH(N'[dbo].[TblPricingPlan]', N'CostPrice') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblPricingPlan]
    ADD [CostPrice] DECIMAL(18,2) NOT NULL
        CONSTRAINT [DF_TblPricingPlan_CostPrice] DEFAULT(0) WITH VALUES;
END;
GO

IF COL_LENGTH(N'[dbo].[TblPricingPlan]', N'CostOverChargePrice') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblPricingPlan]
    ADD [CostOverChargePrice] DECIMAL(18,2) NOT NULL
        CONSTRAINT [DF_TblPricingPlan_CostOverChargePrice] DEFAULT(0) WITH VALUES;
END;
GO

IF COL_LENGTH(N'[dbo].[TblMonthlySubscription]', N'CostPrice') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblMonthlySubscription]
    ADD [CostPrice] DECIMAL(18,2) NOT NULL
        CONSTRAINT [DF_TblMonthlySubscription_CostPrice] DEFAULT(0) WITH VALUES;
END;
GO

IF COL_LENGTH(N'[dbo].[TblMonthlySubscription]', N'CostOverChargePrice') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblMonthlySubscription]
    ADD [CostOverChargePrice] DECIMAL(18,2) NOT NULL
        CONSTRAINT [DF_TblMonthlySubscription_CostOverChargePrice] DEFAULT(0) WITH VALUES;
END;
GO

IF COL_LENGTH(N'[dbo].[TblSubscriptionInvoice]', N'CostPrice') IS NULL
BEGIN
    ALTER TABLE [dbo].[TblSubscriptionInvoice]
    ADD [CostPrice] DECIMAL(18,2) NOT NULL
        CONSTRAINT [DF_TblSubscriptionInvoice_CostPrice] DEFAULT(0) WITH VALUES;
END;
GO
