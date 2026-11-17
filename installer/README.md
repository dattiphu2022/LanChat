# Bộ cài LanChat cho Windows 10 và 11 x64

1. Cài Inno Setup 6.3 trở lên trên máy build.
2. Publish dự án `LanChat.WinForms` từ Visual Studio hoặc chạy `dotnet publish src/LanChat.WinForms -c Release`. Bước publish tự biên dịch `LanChat.iss` từ thư mục publish của WinForms.
3. Lấy bộ cài tại đường dẫn Inno Setup in ra trong `artifacts/installer`.

Sau lần publish đầu tiên, bạn cũng có thể mở `LanChat.iss` và bấm **Compile** trong Inno Setup. Script tự dùng thư mục từ lần publish gần nhất có tạo bộ cài.

Nếu Inno Setup được cài ở vị trí tùy chỉnh, thêm `-p:InnoSetupCompiler="C:/duong-dan/ISCC.exe"` khi chạy `dotnet publish`. Có thể dùng `-p:BuildInstaller=false` để chỉ xuất thư mục WinForms.

Bộ cài chạy theo tài khoản hiện tại và dùng .NET Framework 4.8 có sẵn từ Windows 10 phiên bản 1903 (build 18362) và trên Windows 11. Hồ sơ và lịch sử nằm trong thư mục dữ liệu của LanChat.

## Phát hành công khai

```powershell
pwsh -NoProfile -File scripts/Publish-Windows.ps1 -PublicRelease
```

Script publish sang thư mục mới, kiểm tra tệp cấp mã và tài nguyên nhúng, rồi tạo `artifacts/installer/LanChat-Setup-Windows-x64.exe`. Khi phát hành phiên bản mới, đặt `CurrentApplicationVersion` trong `src/LanChat.Application/Contracts.cs` thành `X.Y.Z`, build lại và đính kèm chính tệp EXE này vào bản GitHub Release ổn định có tag `vX.Y.Z`. LanChat dùng kích thước và SHA-256 trong [GitHub Release Assets](https://docs.github.com/en/rest/releases/assets) để xác minh bộ cài trước khi mở.

Bộ cài dùng cùng AppId để nâng cấp tại chỗ và dùng lại hồ sơ, cấu hình mạng và lịch sử trên máy. Người dùng cài lần đầu chọn **Mạng chat → Thêm mã** hoặc **Tạo mạng**.

## Mã mạng mặc định nội bộ

- Cấp mã trước khi build lần đầu bằng `pwsh -NoProfile -File scripts/Set-DefaultNetwork.ps1 -CreatorMachine TEN-MAY`. Nếu bỏ tham số, máy build là máy khởi tạo cụm. Mở LanChat trên máy khởi tạo để cụm có thể bầu máy trung tâm và quản lý nhóm.
- Khi cần đổi mã, chạy lại lệnh với `-Rotate`, sau đó đóng gói lại và cập nhật các máy cùng đợt. Các lần build thông thường dùng lại mã hiện có.
- EXE nội bộ chứa thông tin tham gia mạng. Chỉ phân phối bản build này trong nội bộ và giữ bản sao tệp `src/LanChat.WinForms/Assets/DefaultNetwork.local.json` để tiếp tục build cùng mã. Git bỏ qua tệp này. Publish bộ cài nội bộ yêu cầu có tệp này để dùng cùng mã trên các máy.

## Khi Danh bạ chưa tìm thấy máy

- Trên hai máy, mở tab **Mạng mặc định**, rồi vào **Mạng chat → Chi tiết → Điều phối và mã mời**. So sánh **ID mạng** và **Giao thức**. Cài cùng bản giao thức v4 trên toàn bộ máy. Lần chạy đầu xóa lịch sử cũ trên từng máy theo [hướng dẫn nâng cấp](../README.md#nâng-cấp).
- Nếu hai máy cùng ID và giao thức, kiểm tra Windows Firewall và việc Wi-Fi có cô lập các thiết bị hay không. Quản trị viên có thể chạy `scripts/Enable-LanChatFirewall.ps1 -AppPath "C:/duong-dan/LanChat.WinForms.exe"` trên từng máy. Quy tắc chỉ cho phép EXE này nhận kết nối từ subnet nội bộ trên profile Domain hoặc Private. Mạng mặc định ưu tiên UDP 54545 và TCP 54546. Mạng riêng dùng cổng TCP theo mã mạng.
