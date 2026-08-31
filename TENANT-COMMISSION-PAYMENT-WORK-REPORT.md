# Tenant Commission Payment Work Report

## Scope Completed

- Added Tenant Commission Payment ledger module for ShipNet.
- Added list, filter, create, eligible billing-cycle search, and detail flows.
- Added admin-only create permission and tenant-scoped read access.
- Updated Billing & Invoice and Dashboard commission calculations to separate gross, paid, and remaining commission.

## Database Changes

- Added `Database/Scripts/20260831_AddTenantCommissionPayments.sql`.
- Added `TblTenantCommissionPayment` as payment header ledger.
- Added `TblTenantCommissionPaymentItem` as billing-cycle detail ledger.
- Added unique index `UX_TenantCommissionPaymentItem_SubscriptionId` so one billing cycle can be paid once.
- Added check constraints for positive amount, valid period, valid source mode, and positive item commission snapshot.
- Updated `Database/Scripts/ShipNet_FullSchema_Rebuild.sql` to include the new ledger tables.

## Business Rules

- Invoice margin fields are not modified.
- No `IsCommissionPaid` field was added to invoice.
- Commission payment records are append-only in this version: create, list, detail only.
- Manual payments reduce remaining commission by header `Amount`.
- Billing-cycle payments reduce remaining commission by header `Amount` and store detail snapshots.
- Billing cycles already linked in `TblTenantCommissionPaymentItem` are not eligible again.

## Commission Formulas

Gross commission:

```text
SUM(COALESCE(NULLIF(TblSubscriptionInvoice.MarginAmount, 0), TblSubscriptionInvoice.SalePrice - TblSubscriptionInvoice.BuyPrice))
```

Paid commission:

```text
SUM(TblTenantCommissionPayment.Amount)
```

Remaining commission:

```text
MAX(0, GrossCommission - PaidCommission)
```

## Eligible Billing-Cycle Rules

A billing cycle is eligible only when:

- It belongs to the selected tenant.
- It does not exist in `TblTenantCommissionPaymentItem`.
- It has at least one valid invoice.
- Valid invoices exclude `void`, `cancelled`, `canceled`, and `refunded`.
- Every valid invoice in the billing cycle is paid by current system logic:

```text
LOWER(Status) = 'paid'
OR
Amount > 0 AND PaidAmount >= Amount
```

Backend recalculates the commission amount from DB before insert and does not trust frontend amount.

## Security / Permissions

- Tenant users can view only their own tenant payment history and balances.
- Admin/system users can view all tenants and create commission payments.
- ShipAdmin/Crew are tenant-scoped and are not allowed to create commission payments.
- View-only users are not allowed to create commission payments.
- Backend validates selected subscription tenant against payment tenant.

## Files Created

- `Controllers/TenantCommissionPaymentController.cs`
- `Database/Scripts/20260831_AddTenantCommissionPayments.sql`
- `Models/TenantCommissionPaymentModels.cs`
- `Services/ITenantCommissionPaymentService.cs`
- `Services/TenantCommissionPaymentService.cs`
- `Views/TenantCommissionPayment/Index.cshtml`
- `StarlinkDeviceManager.Tests/TenantCommissionPaymentTests.cs`
- `TENANT-COMMISSION-PAYMENT-WORK-REPORT.md`

## Files Updated

- `Program.cs`
- `Models/BillingInvoiceModels.cs`
- `Services/BillingInvoiceReportService.cs`
- `Services/DashboardKpiService.cs`
- `Views/BillingInvoice/Index.cshtml`
- `Views/Shared/_PortalNav.cshtml`
- `wwwroot/css/site.css`
- `Database/Scripts/ShipNet_FullSchema_Rebuild.sql`

## Build Result

```text
dotnet build ./StarlinkDeviceManager.csproj -c Release
Build succeeded.
Warnings: existing nullable warnings in DeviceService.cs.
Errors: 0.
```

## Test Result

```text
dotnet test ./StarlinkDeviceManager.Tests/StarlinkDeviceManager.Tests.csproj
Passed: 69.
Failed: 0.
Skipped: 0.
```

## Migration Instructions

Run this script on the target ShipNet SQL Server database before deploying the application:

```text
Database/Scripts/20260831_AddTenantCommissionPayments.sql
```

For a brand-new database rebuild, run:

```text
Database/Scripts/ShipNet_FullSchema_Rebuild.sql
```

Then deploy the application build containing the new controller, service, models, view, and CSS.

The migration script was executed successfully against a temporary LocalDB validation database.
