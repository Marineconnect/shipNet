# Device Activity Log + KVH Payment Resume Audit Progress

Date: 2026-08-17

## Completed

- Restored Tenant Admin management permissions for device management, data opt-in/out, and monthly subscriptions.
- Added explicit activity actions for data opt-in/out failures, subscription pause failure, and cancel-schedule completion/failure.
- Added `ActorType`, `EventKey`, `OccurredAtUtc`, and `RecordedAtUtc` to the activity model and schema path.
- Kept `CreatedAtUtc` compatible by writing it from the recorded timestamp.
- Added idempotent activity writes: duplicate `EventKey` is treated as success and logged without breaking the business flow.
- Added safe migration SQL for fresh and existing databases in `Database/Scripts/20260816_AddDeviceActivityLog.sql`.
- Fixed manual invoice paid flow to use one operation correlation id across invoice update, invoice paid, and optional KVH resume.
- Changed KVH payment resume precheck from GET to POST with antiforgery.
- Updated the invoice UI so precheck failure asks whether to continue paid without resume or cancel.
- Fixed TempData success/warning overwrite in manual paid + resume flow.
- Removed raw provider/KVH payloads from business `DetailJson` activity entries touched by this audit.
- Hardened sanitizer to parse JSON recursively and redact sensitive property names.
- Changed 9Pay `INVOICE_PAID` activity to one deterministic event per device/invoice/payment number.
- Changed 9Pay payment activity old status to the actual invoice status before update.
- Added actor/source semantics for user, payment provider, and KVH worker activity entries.
- Changed KVH worker data opt-in/out completion to use requested `enabled` state, not success boolean, to decide opt-in vs opt-out.
- Changed KVH subscription completion semantics so `SUBSCRIPTION_RESUMED`, `SUBSCRIPTION_PAUSED`, and cancel-schedule completed are written only after verification success, post-command sync, and current-row state confirmation.
- Added focused regression tests for permissions, activity event key schema, data opt-in/out semantics, and POST precheck behavior.

## SQL

No SQL was executed automatically.

Run this script manually when ready:

`Database/Scripts/20260816_AddDeviceActivityLog.sql`

## Validation

- `dotnet build StarlinkDeviceManager.sln`: passed, with existing nullable warnings in `DeviceService`.
- `dotnet test StarlinkDeviceManager.Tests\StarlinkDeviceManager.Tests.csproj`: passed, 27/27 tests.

## Notes

- Activity logging remains fail-safe: logging failures and duplicate event keys do not block payment, invoice, or KVH command flows.
- The log remains append-only in the code paths touched here.
- Raw IPN/KVH payload storage remains in technical transaction/history tables where already designed; business activity `DetailJson` now uses whitelisted fields.
