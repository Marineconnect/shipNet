# Báo cáo cập nhật Dashboard KPI doanh thu

Ngày thực hiện: 23/08/2026

## Phạm vi đã thực hiện

- Thêm cụm KPI doanh thu vào trang Tổng quan/Dashboard, đặt trước phần Device Inventory.
- Thêm bộ lọc tháng/năm, mặc định theo tháng/năm hiện tại.
- Khi đổi tháng/năm, Dashboard gọi AJAX tới endpoint KPI, hiển thị trạng thái đang tải và lỗi nếu API thất bại.
- Ba chỉ số hiển thị:
  - Tổng doanh thu.
  - KIT kích hoạt.
  - Tổng hoa hồng.
- Các card KPI có thể click để chuyển sang màn hình lọc tương ứng:
  - Tổng doanh thu -> Billing & Invoice theo billing cycle.
  - KIT kích hoạt -> Billing Cycle theo tháng đang chọn.
  - Tổng hoa hồng -> Billing & Invoice theo billing cycle và chỉ số margin.
- Sửa thứ tự menu Thương mại/Commercial:
  - Quản lý gói cước / Pricing Management.
  - Chu kỳ tính cước / Billing Cycle.
  - Báo cáo doanh thu / Billing & Invoice.

## Data source và business rules

- Không tạo bảng mới, không thêm migration.
- Dashboard KPI đọc dữ liệu từ bảng hiện có:
  - `TblMonthlySubscription`.
  - `TblSubscriptionInvoice`.
- Chu kỳ doanh thu dùng `TblMonthlySubscription.UsageMonth`, không dùng ngày tạo invoice.
- Tổng doanh thu tính từ `TblSubscriptionInvoice.Amount` của invoice hợp lệ trong chu kỳ.
- Invoice bị loại khỏi doanh thu/hoa hồng/KIT đã có invoice nếu trạng thái là `void`, `cancelled`, hoặc `canceled`.
- KIT kích hoạt đếm distinct `DeviceId` trong subscription của chu kỳ, loại các subscription có trạng thái `void`, `cancelled`, `canceled`, `inactive`.
- KIT đã có invoice đếm distinct `DeviceId` có invoice hợp lệ trong chu kỳ.
- Không thấy field/table commission riêng trong schema hiện tại; chỉ số Tổng hoa hồng dùng cùng logic hiện hữu của Billing & Invoice: `MarginAmount`, fallback `SalePrice - BuyPrice`.
- Scope dữ liệu được áp dụng ở backend:
  - Admin thấy toàn bộ.
  - Tenant user bị giới hạn theo `TenantId`.
  - Ship admin/crew bị giới hạn thêm theo `DeviceId`.

## API/backend

- Thêm service:
  - `IDashboardKpiService`.
  - `DashboardKpiService`.
- Thêm endpoint:
  - `GET /Dashboard/Kpi?month={month}&year={year}`.
- Endpoint trả JSON gồm chu kỳ, doanh thu, KIT kích hoạt, KIT có invoice, hoa hồng và danh sách năm có dữ liệu.
- Đăng ký DI trong `Program.cs`.

## Giao diện

- Thêm panel KPI theo style dark navy của Shipnet Portal.
- Layout responsive:
  - Desktop: 3 card.
  - Tablet: 2 card + 1 card.
  - Mobile: 1 card mỗi dòng.
- Text mới có key i18n VI/EN trong Dashboard script.

## Kiểm tra và triển khai

- `dotnet build .\StarlinkDeviceManager.csproj -c Release --no-restore`: thành công.
- `dotnet test .\StarlinkDeviceManager.Tests\StarlinkDeviceManager.Tests.csproj -c Release --no-restore`: thành công, 60/60 tests passed.
- `dotnet publish .\StarlinkDeviceManager.csproj -c Release -o .\publish-iis`: thành công.
- Lưu ý: lệnh test mặc định Debug bị khóa do app local `StarlinkDeviceManager.exe` đang chạy, nên đã chạy test Release để tránh đụng process đang mở.

## Commit

- `70e3abc Add dashboard revenue KPI summary`
