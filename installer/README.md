# Bộ cài LanChat cho Windows 10 và 11 x64

1. Cài Inno Setup 6.3 trở lên trên máy build.
2. Publish dự án `LanChat.WinForms` từ Visual Studio hoặc chạy `dotnet publish src/LanChat.WinForms -c Release`. Bước publish tự biên dịch `LanChat.iss` từ thư mục publish của WinForms.
3. Lấy `LanChat-Setup-Windows-x64.exe` trong `artifacts/installer`.

Sau lần publish đầu tiên, bạn cũng có thể mở `LanChat.iss` và bấm **Compile** trong Inno Setup. Script tự dùng thư mục từ lần publish gần nhất có tạo bộ cài.

Nếu Inno Setup được cài ở vị trí tùy chỉnh, thêm `-p:InnoSetupCompiler="C:/duong-dan/ISCC.exe"` khi chạy `dotnet publish`. Có thể dùng `-p:BuildInstaller=false` để chỉ xuất thư mục WinForms.

Bộ cài chạy theo tài khoản hiện tại và dùng .NET Framework 4.8 có sẵn từ Windows 10 phiên bản 1903 (build 18362) và trên Windows 11. Hồ sơ và lịch sử nằm trong thư mục dữ liệu của LanChat.

## Mã mạng mặc định nội bộ

- Cấp mã trước khi build lần đầu bằng `pwsh -NoProfile -File scripts/Set-DefaultNetwork.ps1 -CreatorMachine TEN-MAY`. Nếu bỏ tham số, máy build là máy khởi tạo cụm. Mở LanChat trên máy khởi tạo để cụm có thể bầu máy trung tâm và quản lý nhóm.
- Khi cần đổi mã, chạy lại lệnh với `-Rotate`, sau đó đóng gói lại và cập nhật các máy cùng đợt. Các lần build thông thường dùng lại mã hiện có.
- EXE chứa thông tin tham gia mạng. Chỉ phân phối bản build nội bộ và giữ bản sao tệp `src/LanChat.WinForms/Assets/DefaultNetwork.local.json` để tiếp tục build cùng mã. Git bỏ qua tệp này. Publish bộ cài yêu cầu có tệp này để tránh phát hành nhầm bản tạo mạng mặc định riêng cho mỗi máy.

## Khi Danh bạ chưa tìm thấy máy

- Trên hai máy, mở tab **Mạng mặc định**, rồi vào **Mạng chat → Chi tiết → Điều phối và mã mời**. So sánh **ID mạng** và **Giao thức**. Các máy dùng cùng bộ cài nội bộ sẽ có cùng ID trên tab mặc định. Bản v1/v2 cần nâng cấp lên cùng bản v3 để tham gia mạng.
- Nếu hai máy cùng ID và giao thức, kiểm tra Windows Firewall và việc Wi-Fi có cô lập các thiết bị hay không. Quản trị viên có thể chạy `scripts/Enable-LanChatFirewall.ps1 -AppPath "C:/duong-dan/LanChat.WinForms.exe"` trên từng máy. Quy tắc chỉ cho phép EXE này nhận kết nối từ subnet nội bộ trên profile Domain hoặc Private. Mạng mặc định ưu tiên UDP 54545 và TCP 54546. Mạng riêng dùng cổng TCP theo mã mạng.
