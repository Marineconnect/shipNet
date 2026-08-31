using System.Reflection;
using System.Text;
using StarlinkDeviceManager.Controllers;
using StarlinkDeviceManager.Models;
using StarlinkDeviceManager.Services;

public sealed class PricingCostAndBillingCsvTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void BillingCsvUsesUtf8BomAndKeepsVietnameseText()
    {
        var text = "Nguyễn Văn Đức,Công ty TNHH Phước Hải,Tàu Trường Sa";
        var bytes = BillingInvoiceReportService.EncodeCsvForExcel(text);

        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
        Assert.Equal(1, CountUtf8Bom(bytes));
        Assert.Equal(text, Encoding.UTF8.GetString(bytes[3..]));
    }

    [Theory]
    [InlineData("admin", "any-user", true)]
    [InlineData("tenant", "admin", true)]
    [InlineData("tenant", "tenant-user", false)]
    [InlineData("ship_admin", "ship-user", false)]
    [InlineData("crew", "crew-user", false)]
    public void BillingCsvCostPriceRoleGateMatchesAcceptedUsers(string userType, string username, bool expected)
    {
        var user = new AuthUserRecord { UserType = userType, Username = username };

        Assert.Equal(expected, InvokeCanViewCostPrice(user));
    }

    [Fact]
    public void BillingCsvCostPriceRoleGateRejectsAnonymousUsers()
    {
        Assert.False(InvokeCanViewCostPrice(null));
    }

    [Fact]
    public void BillingCsvCostPriceColumnIsControlledByAuthenticatedUserRole()
    {
        var controller = File.ReadAllText(Path.Combine(ProjectRoot, "Controllers", "BillingInvoiceController.cs"));
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "BillingInvoiceReportService.cs"));
        var contract = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "IBillingInvoiceReportService.cs"));

        Assert.Contains("CanViewCostPrice(currentUser)", controller);
        Assert.Contains("user.IsAdmin", controller);
        Assert.Contains("ManagedUserType.Admin", controller);
        Assert.Contains("user.Username?.Trim(), \"admin\"", controller);
        Assert.Contains("bool canViewCostPrice", contract);
        Assert.Contains("bool canViewCostPrice = false", service);
        Assert.Contains("if (canViewCostPrice)", service);
        Assert.Contains("header.Add(\"Cost Price\")", service);
        Assert.Contains("fields.Add(Csv(ReadDecimal(reader, \"CostPrice\")", service);
        Assert.Contains("LEFT JOIN [dbo].[TblTenant] t ON t.[ID] = s.[TenantId]", service);
        Assert.Contains("COALESCE(NULLIF(t.[TenantName], N''), NULLIF(s.[TenantName], N''), N'') AS [TenantName]", service);
    }

    [Fact]
    public void PricingPlanCostFieldsArePersistedAndExported()
    {
        var models = File.ReadAllText(Path.Combine(ProjectRoot, "Models", "PricingPlanModels.cs"));
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "PricingPlanService.cs"));
        var form = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pricing", "_PricingFormFields.cshtml"));
        var view = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Pricing", "Index.cshtml"));
        var migration = File.ReadAllText(Path.Combine(ProjectRoot, "Database", "Scripts", "20260901_AddPricingPlanCostPrices.sql"));
        var fullSchema = File.ReadAllText(Path.Combine(ProjectRoot, "Database", "Scripts", "ShipNet_FullSchema_Rebuild.sql"));

        Assert.Contains("public decimal CostPrice", models);
        Assert.Contains("public decimal CostOverChargePrice", models);
        Assert.Contains("[CostPrice]", service);
        Assert.Contains("[CostOverChargePrice]", service);
        Assert.Contains("@costPrice", service);
        Assert.Contains("@costOverChargePrice", service);
        Assert.Contains("CostPrice = ToPricingCurrency(model.CostPrice", service);
        Assert.Contains("CostOverChargePrice = ToPricingCurrency(model.CostOverChargePrice", service);
        Assert.Contains("CostPrice = ReadDecimal(reader, \"CostPrice\")", service);
        Assert.Contains("CostOverChargePrice = ReadDecimal(reader, \"CostOverChargePrice\")", service);
        Assert.Contains("CostPrice", migration);
        Assert.Contains("CostOverChargePrice", migration);
        Assert.Contains("CostPrice", fullSchema);
        Assert.Contains("CostOverChargePrice", fullSchema);
        Assert.Contains("name=\"@(prefix).CostPrice\"", form);
        Assert.Contains("name=\"@(prefix).CostOverChargePrice\"", form);
        Assert.Contains("data-money-input", form);
        Assert.Contains("KVH Cost Price", view);
        Assert.Contains("KVH Overcharge Cost", view);
    }

    [Fact]
    public void PricingPlanImportAcceptsNewCostColumnsAndLegacyFiles()
    {
        var csv = string.Join(Environment.NewLine, [
            "Tên gói,Mã gói,Dung lượng (GB),Giá Cost KVH,Đơn giá đại lý,Đơn giá bán ra,Giá Cost Overcharge KVH,Giá mua thêm đại lý,Giá mua thêm bán ra,Trạng thái",
            "Plan A,PLAN_A,50,4500000,5000000,6000000,50000,60000,70000,active"
        ]);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parsed = PricingPlanExcelTemplate.Parse(stream, "plans.csv");

        Assert.Empty(parsed.Errors);
        Assert.Single(parsed.Plans);
        Assert.Equal(4500000, parsed.Plans[0].CostPrice);
        Assert.Equal(50000, parsed.Plans[0].CostOverChargePrice);

        var legacyCsv = string.Join(Environment.NewLine, [
            "Tên gói,Mã gói,Dung lượng (GB),Đơn giá đại lý,Đơn giá bán ra,Giá mua thêm đại lý,Giá mua thêm bán ra,Trạng thái",
            "Plan B,PLAN_B,50,5000000,6000000,60000,70000,active"
        ]);

        using var legacyStream = new MemoryStream(Encoding.UTF8.GetBytes(legacyCsv));
        var legacyParsed = PricingPlanExcelTemplate.Parse(legacyStream, "plans.csv");

        Assert.Empty(legacyParsed.Errors);
        Assert.Single(legacyParsed.Plans);
        Assert.Equal(0, legacyParsed.Plans[0].CostPrice);
        Assert.Equal(0, legacyParsed.Plans[0].CostOverChargePrice);
    }

    [Fact]
    public void BillingInvoicesSnapshotCostPriceInsteadOfReadingCurrentPlanPrice()
    {
        var service = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "MonthlySubscriptionService.cs"));
        var billingService = File.ReadAllText(Path.Combine(ProjectRoot, "Services", "BillingInvoiceReportService.cs"));

        Assert.Contains("[CostPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_CostPrice]", service);
        Assert.Contains("[CostOverChargePrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblMonthlySubscription_CostOverChargePrice]", service);
        Assert.Contains("[CostPrice] decimal(18,2) NOT NULL CONSTRAINT [DF_TblSubscriptionInvoice_CostPrice]", service);
        Assert.Contains("CalculateSubscriptionPrice(context.CostPrice", service);
        Assert.Contains("Math.Round(dataGb * subscription.CostOverChargePrice", service);
        Assert.Contains("[DataGb], [CostPrice], [BuyPrice]", service);
        Assert.Contains("i.[DataGb], i.[CostPrice], i.[BuyPrice]", billingService);
        Assert.DoesNotContain("pp.[CostPrice]", billingService);
    }

    private static bool InvokeCanViewCostPrice(AuthUserRecord? user)
    {
        var method = typeof(BillingInvoiceController).GetMethod("CanViewCostPrice", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, [user])!;
    }

    private static int CountUtf8Bom(byte[] bytes)
    {
        var count = 0;
        for (var index = 0; index <= bytes.Length - 3; index++)
        {
            if (bytes[index] == 0xEF && bytes[index + 1] == 0xBB && bytes[index + 2] == 0xBF)
            {
                count++;
            }
        }

        return count;
    }
}
