# Billing & Invoice Work Report

Date: 2026-08-22
Project: ShipNet Portal - StarlinkDeviceManager

## Scope Completed

- Implemented the Billing & Invoice reporting module as a read-only portal screen.
- Reused existing invoice and subscription data. No new Billing table was added.
- Added server-side filtering, search, sorting, paging, KPI summary, and CSV export.
- Added role-aware backend scoping so tenant, ship admin, and crew users only see allowed data.
- Added the Billing & Invoice entry to the portal navigation.
- Kept the page styling aligned with the existing ShipNet dark dashboard theme.

## Data Source Mapping

- Invoice source: `TblSubscriptionInvoice`.
- Subscription source: `TblMonthlySubscription`.
- Device/KIT fallback source: `TblDevices`.
- Payment transaction source: latest related row from `TblPaymentTransaction`.
- 9Pay QR source: latest related row from `TblNinePayQrSession` and `TblNinePayQrSessionInvoice`.
- KIT fallback order: `TblDevices.KITNumber`, then `TblMonthlySubscription.KitId`, then `TblDevices.KITID`.

## KPI Formulas

- Total Invoice Amount: sum of invoice `Amount`.
- Paid Amount: sum of invoice `PaidAmount`.
- Pending Amount: positive balance of `Amount - PaidAmount - RefundAmount`.
- Total Margin: invoice `MarginAmount`, with fallback to `SalePrice - BuyPrice` where margin is empty/zero.
- Paid Invoices: invoice status `paid` or paid amount greater than or equal to amount.
- Pending Invoices: non-void, non-refunded invoices with outstanding amount.

## Filters And Search

Implemented filters:

- Invoice created date range.
- Billing cycle.
- Tenant.
- Vessel/device.
- KIT number.
- Pricing plan.
- Invoice type.
- Invoice status.
- Payment status.
- Invoice number.
- Keyword search across invoice, KIT, tenant, vessel, device, and plan.

Sorting is server-side and supports created date, invoice number, billing cycle, tenant, vessel, invoice amount, paid amount, margin, and status.

## Permission Handling

- The module requires authentication.
- Tenant users are scoped by tenant.
- Ship admin and crew users are scoped by tenant and device.
- System-level users can view all data.
- The module is read-only and does not create, update, or delete invoice data.

## Files Created

- `Controllers/BillingInvoiceController.cs`
- `Models/BillingInvoiceModels.cs`
- `Services/IBillingInvoiceReportService.cs`
- `Services/BillingInvoiceReportService.cs`
- `Views/BillingInvoice/Index.cshtml`
- `BILLING-INVOICE-WORK-REPORT.md`

## Files Updated

- `Program.cs`
- `Views/Shared/_PortalNav.cshtml`
- `wwwroot/css/site.css`
- `StarlinkDeviceManager.Tests/DeviceActivityAuditHardeningTests.cs`

Related KVH auto-resume setting work was also completed in the same delivery:

- `Services/SystemSettingsService.cs`
- `Controllers/SystemSettingsController.cs`
- `Models/SystemSettingsModels.cs`
- `Views/SystemSettings/Index.cshtml`
- `Services/KvhPaymentResumeService.cs`

## Validation

- `dotnet build .\StarlinkDeviceManager.csproj -c Release --no-restore`
  - Result: succeeded.
  - Note: existing nullable warnings remain in `Services/DeviceService.cs`.
- `dotnet test .\StarlinkDeviceManager.Tests\StarlinkDeviceManager.Tests.csproj --no-restore`
  - Result: passed.
  - Total: 59 passed, 0 failed.

## Deployment

- Published IIS build output to `publish-iis`.
- Command used: `dotnet publish .\StarlinkDeviceManager.csproj -c Release -o .\publish-iis`.

## Notes

- No database migration is required for the Billing & Invoice module.
- The page depends on existing transaction and 9Pay tables already used by the portal.
- CSV export uses the same backend filters and sort order as the screen.
