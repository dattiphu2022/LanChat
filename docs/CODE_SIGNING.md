# Ký số phát triển LanChat trên Windows

Chứng thư của máy phát triển: **CN=LanChat Development**, Code Signing, RSA 3072-bit, SHA-256, khóa Exportable, thời hạn ba năm, trong **CurrentUser\My**. Chỉ sử dụng cho LanChat. Không thay kho Trusted Root, Trusted Publishers, Execution Policy hoặc cấu hình chống virus.

## Cấu hình build

Công cụ `tools/LanChat.Signing` tìm SignTool x64 trong Windows SDK, chọn đúng thumbprint của chứng thư LanChat còn hiệu lực, ký SHA-256 rồi kiểm tra chữ ký CMS và tính toàn vẹn PE qua WinVerifyTrust. Tệp bị sửa hoặc khác chứng thư bị từ chối. Chữ ký tự ký có kết quả Windows **0x800B0109 — untrusted root**; không coi đây là chứng thư được CA xác thực hoặc cam kết bỏ cảnh báo SmartScreen.

Tạo `CodeSigning.local.props` tại thư mục solution, sau khi đã sao lưu chứng thư. File này chỉ chứa thumbprint công khai và tùy chọn build, được Git bỏ qua:

```xml
<Project>
  <PropertyGroup>
    <LanChatSigningThumbprint>THUMBPRINT_CUA_CHUNG_THU_DA_SAO_LUU</LanChatSigningThumbprint>
    <LanChatDevelopmentSigning Condition="'$(LanChatDevelopmentSigning)' == '' and '$(LanChatPublicRelease)' != 'true'">true</LanChatDevelopmentSigning>
  </PropertyGroup>
</Project>
```

Debug/Release thông thường sẽ ký EXE sau build khi có cấu hình này. Máy CI không có chứng thư dùng `-p:LanChatDevelopmentSigning=false`. Không chép PFX vào repo, thư mục publish hoặc release asset. Không truyền mật khẩu qua tham số dòng lệnh hay biến môi trường.

## Bộ cài Inno Setup có chữ ký

Trước hết publish payload công khai, không nhúng mã mạng riêng:

```powershell
dotnet publish src/LanChat.WinForms/LanChat.WinForms.csproj -c Release -m:1 `
  -p:LanChatPublicRelease=true -p:LanChatDevelopmentSigning=true -p:BuildInstaller=false `
  -p:LanChatPublicBuildDirectory=artifacts/v1.2.7-release/public-build `
  -o artifacts/v1.2.7-release/publish
dotnet msbuild installer/BuildSignedInstaller.proj -v:minimal
```

MSBuild kiểm tra version và tài nguyên/tệp cấp mã, xác minh EXE đầu vào, gọi Inno Setup với SignTool cho bộ cài và phần gỡ cài đặt, rồi xác minh **LanChat-Setup-Windows-x64.exe** lần cuối. Chỉ tải asset đã ký/xác minh lên release. Dùng property MSBuild `-p:ReleaseDirectory=...` khi chọn vị trí khác.

`scripts/Publish-Windows.ps1 -PublicRelease -DevelopmentSign` cũng hỗ trợ quy trình này. Nếu phần mềm bảo vệ chặn script, giữ nguyên cơ chế bảo vệ và dùng lệnh build/MSBuild ở trên; không cần chạy lại script khởi tạo chứng thư. Ký số không phải lý do để tắt chống virus hoặc thêm ngoại lệ.

## Bản sao lưu đã tạo trên máy phát triển

- Desktop được lấy bằng **SHGetKnownFolderPath/FOLDERID_Desktop**, không ghép `%USERPROFILE%\Desktop`. Trên máy này kết quả là `C:\Users\Admin\OneDrive\Desktop\LanChat_CodeSigning_Backup\`.
- `LanChat_CodeSigning_Private.pfx`: chứa chứng thư và khóa riêng, được mã hóa **AES-256/SHA-256** bằng mật khẩu do người dùng nhập vào PasswordBox an toàn. Mật khẩu không được hiển thị, ghi log, lưu file hoặc chuyển cho Codex.
- `LanChat_CodeSigning_Public.cer`: chỉ chứa chứng thư công khai.
- `README.txt`: Subject, thumbprint, ngày tạo/hết hạn, mục đích và hướng dẫn khôi phục. Bản README trước khi cập nhật được giữ riêng.
- PFX đã được đọc lại bằng mật khẩu nhập, xác nhận có khóa riêng; ký/thẩm tra thử thách ngẫu nhiên trong bộ nhớ để chứng minh khóa khớp chứng thư. CER đã được đối chiếu thumbprint và không có khóa riêng. Không ghi đè PFX/CER cũ.

Chứng thư dùng cho v1.2.7: **9CC153E400BB35AAD399B6BA9956DC92EF0DC377**, hết hạn **19/11/2029**. Không có PFX, khóa riêng hoặc mật khẩu trong Git/release. Thư mục nằm trên OneDrive vì Desktop đã được chuyển hướng; trạng thái đồng bộ OneDrive không được giả định.

## Khôi phục trên máy Windows mới

1. Chép PFX an toàn sang máy mới và giữ mật khẩu riêng trong trình quản lý mật khẩu của bạn.
2. Mở `certmgr.msc` → **Personal/Chứng chỉ cá nhân → Certificates → All Tasks → Import**. Chọn PFX, tự nhập mật khẩu trong wizard và nhập vào **Current User → Personal/My**. Chọn cho phép xuất khóa nếu cần tiếp tục sao lưu.
3. Đối chiếu thumbprint ở trên và xác nhận Windows báo có private key. Không chọn Trusted Root hay Trusted Publishers.
4. Tạo `CodeSigning.local.props` với thumbprint đã khôi phục, build rồi chạy công cụ `LanChat.Signing.exe verify --thumbprint THUMBPRINT path.exe` để kiểm tra.

Hoặc nhập trực tiếp trong PowerShell bằng lệnh chuẩn của Windows, không lưu mật khẩu:

```powershell
$password = Read-Host 'Mật khẩu PFX' -AsSecureString
try {
  Import-PfxCertificate -FilePath '.\LanChat_CodeSigning_Private.pfx' `
    -CertStoreLocation Cert:\CurrentUser\My -Password $password -Exportable
} finally { $password.Dispose() }
```

Tài liệu: [SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool), [Export-PfxCertificate](https://learn.microsoft.com/en-us/powershell/module/pki/export-pfxcertificate), [Windows Known Folders](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shgetknownfolderpath).
