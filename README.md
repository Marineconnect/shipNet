# Starlink Device Manager (.NET 8)

Dự án ASP.NET Core MVC (.NET 8) mô phỏng hệ thống quản lý thiết bị Starlink với giao diện tối màu theo style dashboard bạn cung cấp.

## Tính năng hiện có
- Trang đăng nhập cùng style với dashboard
- Cookie Authentication
- Dashboard quản lý thiết bị Starlink
- Khu vực thông tin hệ thống, điều khiển thiết bị, quản lý Wi-Fi
- Bản đồ và biểu đồ mô phỏng theo giao diện mẫu
- Responsive cơ bản

## Tài khoản demo
- Username: `admin`
- Password: `123456`

## Cách chạy
```bash
dotnet restore
dotnet run
```

Hoặc:
```bash
dotnet watch run
```

Sau đó mở trình duyệt:
```bash
http://localhost:5000
```
hoặc cổng mà ASP.NET Core hiển thị trong terminal.

## Cấu trúc chính
- `Controllers/AccountController.cs`: đăng nhập / đăng xuất
- `Controllers/DashboardController.cs`: trang dashboard
- `Models/LoginViewModel.cs`: model đăng nhập
- `Models/DeviceDashboardViewModel.cs`: dữ liệu dashboard
- `Views/Account/Login.cshtml`: giao diện login
- `Views/Dashboard/Index.cshtml`: giao diện quản lý thiết bị
- `wwwroot/css/site.css`: toàn bộ style

## Gợi ý mở rộng
- Kết nối DB SQL Server / PostgreSQL
- Tạo bảng thiết bị Starlink
- Kết nối API thật của Starlink / backend trung gian
- Phân quyền người dùng
- CRUD thiết bị, router config, lịch sử thao tác
- SignalR để cập nhật trạng thái realtime