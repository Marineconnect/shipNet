# ShipNet Device Activity Log and KVH Payment Resume Progress

## 1. Summary
- Added centralized `TblDeviceActivityLog` business timeline with constants, models, service, DI, SQL script, and Device Detail UI tab.
- Added post-payment KVH resume orchestration for 9Pay paid IPN and manual invoice Paid updates.
- Preserved existing payment commit/RabbitMQ/Telegram/audit behavior. KVH resume runs after invoice/payment DB commit and is safe-logged on failure.

## 2. Files changed
- Added:
  - `Models/DeviceActivityModels.cs`
  - `Services/IDeviceActivityLogService.cs`
  - `Services/DeviceActivityLogService.cs`
  - `Services/IKvhPaymentResumeService.cs`
  - `Services/KvhPaymentResumeService.cs`
  - `Database/Scripts/20260816_AddDeviceActivityLog.sql`
  - `docs/20260816-device-activity-kvh-resume-progress.md`
- Modified for this feature:
  - `Program.cs`
  - `Controllers/DashboardController.cs`
  - `Controllers/MonthlySubscriptionController.cs`
  - `Models/MonthlySubscriptionModels.cs`
  - `Services/IMonthlySubscriptionService.cs`
  - `Services/MonthlySubscriptionService.cs`
  - `Services/PaymentTransactionService.cs`
  - `Services/DeviceService.cs`
  - `Services/KvhSubscriptionService.cs`
  - `Services/KvhJobService.cs`
  - `Views/Dashboard/Index.cshtml`
  - `Views/MonthlySubscription/Details.cshtml`
  - `Views/KvhSolutions/Details.cshtml`
  - `wwwroot/css/site.css`

## 3. Database/schema changes
- New table: `[dbo].[TblDeviceActivityLog]`.
- Indexes:
  - `DeviceId, CreatedAtUtc DESC`
  - `DeviceId, Category, CreatedAtUtc DESC`
  - `DeviceId, Status, CreatedAtUtc DESC`
  - `CorrelationId`
  - `ReferenceType, ReferenceId`
- SQL script is at `Database/Scripts/20260816_AddDeviceActivityLog.sql`.
- SQL was not executed.

## 4. New endpoints
- `GET /Dashboard/DeviceActivityData`
- `GET /MonthlySubscription/KvhPaymentResumePrecheck`

## 5. New services/models
- `IDeviceActivityLogService` / `DeviceActivityLogService`
- `IKvhPaymentResumeService` / `KvhPaymentResumeService`
- `DeviceActivityLogEntry`, `DeviceActivityFilter`, `DeviceActivityPageResult`, `DeviceActivityItem`
- `KvhPaymentResumeRequest`, `KvhPaymentResumeResult`, `KvhPaymentResumePrecheckResult`

## 6. Payment -> KVH Resume flow
- `PaymentTransactionService.ProcessNinePayIpnAsync` still commits payment/invoice first.
- After commit and existing post-payment integrations, it writes `INVOICE_PAID` activity and calls `KvhPaymentResumeService`.
- Resume service syncs KVH, checks current status, skips if not paused, skips duplicate pending resume commands, submits existing `IKvhSubscriptionService.ResumeAsync` only when paused.
- KVH failure is logged as `SUBSCRIPTION_RESUME_FAILED` and does not fail the IPN payment result.

## 7. Manual Paid -> Resume confirmation flow
- `Views/MonthlySubscription/Details.cshtml` prechecks when invoice status changes from non-paid to paid.
- If KVH is paused, browser confirm asks whether to resume.
- Backend rechecks KVH in `KvhPaymentResumeService` after invoice commit when `ResumeKvh=true`.
- If resume fails, invoice remains paid and warning is shown via `TempData["SubscriptionWarning"]`.

## 8. Device Activity Log architecture
- New service writes sanitized business activity rows.
- Device detail modal has new Activity Log tab with category/status/date filters, pagination, and expandable detail rows.
- Added activity writes for billing cycle create/update, invoice create/update/paid, data opt-in/out, networking commands, plan add/update/remove, KVH subscription commands, and KVH worker completion.

## 9. Legacy log compatibility
- `DeviceActivityLogService.GetDeviceActivityAsync` includes safe legacy rows from:
  - `TblAudit`
  - `TblKvhCommand`
  - `TblDeviceDataOptInHistory`
- It only includes legacy rows tied to the requested `DeviceId` and avoids duplicates where matching new activity references exist.

## 10. Build/test result
- `dotnet build StarlinkDeviceManager.sln`: passed.
- `dotnet test StarlinkDeviceManager.Tests\StarlinkDeviceManager.Tests.csproj --no-build`: passed, 23/23.
- Build still reports 6 nullable warnings in existing `DeviceService.cs` areas.

## 11. Deployment notes
- Run `Database/Scripts/20260816_AddDeviceActivityLog.sql` manually before or during deployment.
- No new config keys required.
- IIS deployment: publish/deploy app normally after DB script is applied. Recycle the app pool so DI/service changes load.

## 12. Risks / manual verification
- KVH behavior requires real paused/active subscription states and should be verified in staging with KVH credentials.
- 9Pay duplicate IPN resume prevention depends on synced KVH state and pending `TblKvhCommand` rows.
- Existing repository had many dirty files and tracked build artifacts before this work; review git diff carefully before commit.
