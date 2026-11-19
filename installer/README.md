# Bộ cài LanChat cho Windows 10 và 11 x64

Cài Inno Setup 6.3 trở lên trên máy build. Bộ cài chạy theo tài khoản hiện tại và dùng .NET Framework 4.8 trên Windows 10 từ phiên bản 1903 (build 18362) và Windows 11.

## Phát hành công khai từ Visual Studio

1. Đặt `CurrentApplicationVersion` trong `src/LanChat.Application/Contracts.cs` thành phiên bản sẽ phát hành, chẳng hạn `1.2.1`.
2. Mở `LanChat.sln`. Trong **Solution Explorer**, nhấp phải project **LanChat.WinForms → Publish**.
3. Chọn profile **PublicRelease** và bấm **Publish**. Chờ thông báo `Public publish ready` trong **Output**.
4. Mở [LanChat.Public.iss](LanChat.Public.iss) trong Inno Setup Compiler. Chọn **Build → Compile** để tạo `artifacts/public-release/installer/LanChat-Setup-Windows-x64.exe`.
5. Push commit đã dùng để build và gắn tag trùng phiên bản, chẳng hạn `v1.2.1`. Tạo release ổn định từ tag đó, đính kèm EXE với nguyên tên và chọn **Set as latest release**. [GitHub Releases](https://github.com/dattiphu2022/LanChat/releases)

Profile **PublicRelease** kiểm tra tài nguyên và tệp cấp mã mạng trước khi chuẩn bị bộ cài. `LanChat.Public.iss` lấy phiên bản và đường dẫn ứng dụng từ lần publish công khai thành công gần nhất. Với bản cập nhật, tăng phiên bản trong code rồi lặp lại Publish, Compile và tải EXE lên một tag mới.

## Phát hành công khai bằng lệnh

```powershell
pwsh -NoProfile -File scripts/Publish-Windows.ps1 -PublicRelease
```

Script publish sang thư mục mới, kiểm tra tệp cấp mã và tài nguyên nhúng, rồi tạo `artifacts/installer/LanChat-Setup-Windows-x64.exe`. Khi phát hành phiên bản mới, đặt `CurrentApplicationVersion` trong `src/LanChat.Application/Contracts.cs` thành `X.Y.Z`, build lại và đính kèm chính tệp EXE này vào bản GitHub Release ổn định có tag `vX.Y.Z`. LanChat dùng kích thước và SHA-256 trong [GitHub Release Assets](https://docs.github.com/en/rest/releases/assets) để xác minh bộ cài trước khi mở.

Bộ cài dùng cùng AppId để nâng cấp tại chỗ. Kiểm tra hồ sơ, cấu hình mạng và lịch sử khi thử nâng cấp từ bản trước bằng cùng tài khoản Windows. Người dùng cài lần đầu chọn **Mạng chat → Thêm mã** hoặc **Tạo mạng**. [Hướng dẫn sử dụng](../docs/HUONG_DAN_SU_DUNG.md)

## Ký số phát triển trước khi phát hành

Từ v1.2.7, build trên máy phát triển có thể tự ký EXE bằng SignTool khi cấu hình `CodeSigning.local.props` trỏ tới thumbprint của chứng thư LanChat đã sao lưu. Dùng `dotnet msbuild installer/BuildSignedInstaller.proj` sau khi publish payload công khai để ký/xác minh bộ cài Inno Setup và phần gỡ cài đặt. Không tải bộ cài chưa ký lên release. Đây là chứng thư tự ký dành cho phát triển; không phải chứng thư CA và không tự cài Trusted Root. Xem [hướng dẫn ký và khôi phục PFX](../docs/CODE_SIGNING.md).

## Đóng gói nội bộ

Publish dự án `LanChat.WinForms` bằng **FolderProfile** hoặc chạy `dotnet publish src/LanChat.WinForms -c Release` để biên dịch `LanChat.iss` và tạo bộ cài trong `artifacts/installer`. File `LanChat.iss` dùng thư mục từ lần publish nội bộ có tạo bộ cài gần nhất khi bạn mở và bấm **Compile** trong Inno Setup.

Nếu Inno Setup ở vị trí tùy chỉnh, dùng `-p:InnoSetupCompiler="C:/duong-dan/ISCC.exe"` khi chạy `dotnet publish`. Dùng `-p:BuildInstaller=false` để chỉ xuất thư mục WinForms.

## Mã mạng mặc định nội bộ

- Cấp mã trước khi build lần đầu bằng `pwsh -NoProfile -File scripts/Set-DefaultNetwork.ps1 -CreatorMachine TEN-MAY`. Nếu bỏ tham số, máy build là máy khởi tạo cụm. Mở LanChat trên máy khởi tạo để cụm có thể bầu máy trung tâm và quản lý nhóm.
- Khi cần đổi mã, chạy lại lệnh với `-Rotate`, sau đó đóng gói lại và cập nhật các máy cùng đợt. Các lần build thông thường dùng lại mã hiện có.
- EXE nội bộ chứa thông tin tham gia mạng. Chỉ phân phối bản build này trong nội bộ và giữ bản sao tệp `src/LanChat.WinForms/Assets/DefaultNetwork.local.json` để tiếp tục build cùng mã. Git bỏ qua tệp này. Publish bộ cài nội bộ yêu cầu có tệp này để dùng cùng mã trên các máy.

## Khi Danh bạ chưa tìm thấy máy

- Trên hai máy, chọn tab đang sử dụng rồi vào **Mạng chat → Chi tiết → Điều phối và mã mời**. So sánh **ID mạng** và **Giao thức**. Các máy dùng cùng mã và cùng giao thức v4. Khi chuyển từ giao thức cũ, xem chính sách dữ liệu trong [hướng dẫn nâng cấp](../README.md#nâng-cấp).
- Nếu hai máy cùng ID và giao thức, kiểm tra Windows Firewall và việc Wi-Fi có cô lập các thiết bị hay không. Quản trị viên có thể chạy `scripts/Enable-LanChatFirewall.ps1 -AppPath "C:/duong-dan/LanChat.WinForms.exe"` trên từng máy. Quy tắc chỉ cho phép EXE này nhận kết nối từ subnet nội bộ trên profile Domain hoặc Private. Mạng mặc định ưu tiên UDP 54545 và TCP 54546. Mạng riêng dùng cổng TCP theo mã mạng.
