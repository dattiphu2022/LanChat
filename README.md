# LanChat — Chat nội bộ P2P bằng .NET 9 WinForms

Đã triển khai Phase 1–5: chat trực tiếp/nhóm, tự bầu điều phối, lịch sử và sự kiện đồng bộ giữa các máy, làm việc khi mất kết nối, tệp P2P, tìm kiếm, TLS/HMAC và chẩn đoán. Ứng dụng chạy hoàn toàn trong LAN, giao diện WinForms native; mỗi máy có SQLite riêng.

## Mở bản chạy

Mở **`artifacts/LanChat-1.0/LanChat.WinForms.exe`**. Khi chuyển sang máy khác, sao chép **toàn bộ thư mục** `artifacts/LanChat-1.0`. Bản này cần **.NET 9 Desktop Runtime** trên Windows.

1. Máy đầu tiên nhập tên và chọn **Tạo mạng chat**.
2. Vào **Cài đặt → Mạng chat → Hiện mã mời tham gia**, chia sẻ mã cho người được phép tham gia.
3. Máy khác nhập tên, dán cùng mã và chọn **Tham gia**. Các máy cần cùng LAN và cổng UDP discovery.
4. Vào **Người dùng**, chọn tên để chat. Tin được lưu trước khi gửi ra mạng.
5. Vào **Nhóm**, chọn thành viên và tạo nhóm. Chủ nhóm có thể đổi tên/ảnh, thêm/xóa thành viên, chuyển quyền và xóa nhóm.
6. Kéo nhiều ảnh/tệp vào khung chat hoặc bấm **＋**; kiểm tra preview rồi bấm **Gửi**. Nội dung nhập cùng ảnh là **chú thích chung** cho các ảnh trong lần gửi đó. **Ảnh hiện trực tiếp trong tin nhắn**; bấm ảnh mở khung xem lớn có chú thích và dải ảnh của cuộc trò chuyện. Dùng **Trước/Sau**, phím **← →** hoặc bấm hình thu nhỏ để chuyển ảnh; cuộn chuột để zoom, kéo để di chuyển, **Vừa khung/100%** để đổi tỷ lệ và **Esc** để đóng.
   **Bấm tên tệp khác để mở bằng ứng dụng mặc định của Windows**. Nếu chưa có local, LanChat tự tải vào dữ liệu máy, kiểm tra SHA256 rồi mở. Tải lâu sẽ hiện tiến độ và nút Hủy. Trong khung xem ảnh có nút **Ứng dụng ngoài**; menu **File** vẫn có màn hình xem chi tiết và **Lưu thành…**.
7. Nhấp phải tin nhắn để trả lời, sao chép, chuyển tiếp, cảm xúc, sửa, thu hồi hoặc xóa phía tôi. Tìm nội dung tại **Tìm tin nhắn**; nhấp đúp kết quả để xem vị trí trong lịch sử.

**Enter** gửi, **Shift+Enter** xuống dòng. Tin đã đồng bộ nghĩa là có ít nhất một bản sao tại peer khác; “đã nhận” và “đã đọc” dựa trên receipt của người tham gia. Ghim, im lặng và xóa phía tôi chỉ áp dụng trên máy này.

Nên có **ít nhất 3 máy đã tham gia ổn định** để chịu được một máy mất kết nối mà vẫn thay đổi nhóm được. Với 2 voter, cần cả hai cho quorum. Khi thiếu quorum, chat vẫn lưu local và đồng bộ qua các peer còn kết nối; các thay đổi cấu trúc nhóm bị từ chối.

## Nâng cấp từ Phase 1

Đóng ứng dụng và sao lưu toàn bộ thư mục dữ liệu trên từng máy trước khi dùng bản mới.

- Mỗi máy vẫn dùng thư mục dữ liệu cũ của chính nó; giữ nguyên UserId/NodeId và lịch sử.
- Cửa sổ **Nâng cấp mạng chat** xuất hiện với cấu hình cũ. Chọn **một máy duy nhất** tạo mã nâng cấp, áp dụng trên máy đó rồi nhập **cùng mã** cho các máy khác.
- Máy tạo mã cần trực tuyến trong giai đoạn thiết lập membership ban đầu. Sau khi có đủ voter, các máy có thể tự bầu điều phối mới.
- Migration tự thêm bảng sự kiện/outbox/cursor/control log và chỉ mục tìm kiếm; tin cũ được bổ sung vào event store một lần.
- Protocol 2 không trao đổi với ứng dụng Phase 1. Nâng cấp tất cả máy trong cùng đợt.
- Không ghép database của hai cluster và không chép database máy A thành dữ liệu cho máy B.

## Build, test và publish

PowerShell tại thư mục solution:

```powershell
Set-Location 'C:\Users\Admin\source\repos\dattiphu2022\LANChatAppByMrD'
dotnet restore LanChat.sln
dotnet build LanChat.sln --no-restore
dotnet test tests/LanChat.Tests --no-restore
dotnet run --project src/LanChat.WinForms --no-build
```

Project khởi động trong Visual Studio: **LanChat.WinForms**. Môi trường kiểm tra: SDK 10.0.400, target .NET 9, Windows Desktop Runtime 9.0.19.

```powershell
dotnet publish src/LanChat.WinForms -c Release --no-restore --self-contained false -o artifacts/LanChat-1.0
```

Hai instance thử thủ công trên cùng máy, mỗi lệnh ở một cửa sổ:

```powershell
dotnet run --project src/LanChat.WinForms --no-build -- --data-dir .codex-temp/manual-A --port 54546
dotnet run --project src/LanChat.WinForms --no-build -- --data-dir .codex-temp/manual-B --port 54547
```

Dùng cùng mã mời, giữ nguyên thư mục dữ liệu khi mở lại. Cổng được lưu trong cấu hình; tham số cổng trên dòng lệnh có quyền ưu tiên. Một thư mục chỉ được mở bởi một instance.

Kiểm tra tự động **hai tiến trình WinForms thật**, tạo dữ liệu riêng, chụp giao diện và tự đóng:

```powershell
dotnet build LanChat.sln
pwsh -NoProfile -File scripts/Verify-TwoInstances.ps1 -UseBroadcast
# Hoặc dùng UDP loopback để kiểm tra độc lập với chính sách broadcast:
pwsh -NoProfile -File scripts/Verify-TwoInstances.ps1
```

Script cần PowerShell 7. Mỗi lần tạo thư mục `.codex-temp/two-instances-<thời điểm>`, không ghi vào dữ liệu thật. Dữ liệu mẫu gồm hai hồ sơ, chat, nhóm, ảnh và tệp. Muốn bắt đầu lại chỉ cần chạy lại script; không cần xóa/reset database đang dùng. Hook kiểm tra và các nút mô phỏng lỗi chỉ có trong Debug.

## Mạng

Mặc định **UDP 54545** discovery; **TCP/TLS 54546** cho sự kiện, điều phối và truyền tệp. Không cần IP server cố định. UDP công bố cổng TCP của peer; quét TCP fallback dò cổng service đang cấu hình.

Subnet tính từ IPv4 và mask thật; ưu tiên Ethernet/Wi-Fi đang hoạt động, lọc adapter ảo/VPN phổ biến. Quét có cancellation, 32 kết nối song song và timeout 600 ms; không phụ thuộc ping. Discovery chạy mỗi 5 giây, quét tự động mỗi 120 giây với mạng tối đa 4.096 host.

Nếu Windows Firewall hỏi, cho phép trên mạng nội bộ tin cậy. Quản trị viên có thể tạo rule trên từng máy; ứng dụng không tự sửa firewall:

```powershell
$lanChatExe = (Resolve-Path '.\artifacts\LanChat-1.0\LanChat.WinForms.exe').Path
New-NetFirewallRule -DisplayName 'LanChat TCP Private' -Direction Inbound -Program $lanChatExe -Protocol TCP -LocalPort 54546 -RemoteAddress LocalSubnet -Profile Private -Action Allow
New-NetFirewallRule -DisplayName 'LanChat UDP Private' -Direction Inbound -Program $lanChatExe -Protocol UDP -LocalPort 54545 -RemoteAddress LocalSubnet -Profile Private -Action Allow
```

Sửa cổng theo cấu hình. VLAN, Wi-Fi client isolation và policy broadcast cần kiểm tra tại nơi triển khai.

## Dữ liệu và giới hạn cần biết

- Dữ liệu mặc định: `%LOCALAPPDATA%\LanChat\Data`. SQLite WAL, event/projection/outbox cùng transaction; không dùng SQLite qua network share hoặc thư mục liên kết.
- Tất cả node trong cluster giữ lịch sử, kể cả hội thoại không hiển thị trên giao diện của người đó. **Cluster là một miền tin cậy chung; đây không phải mã hóa đầu cuối giữa riêng hai người.**
- TCP dùng TLS 1.2/1.3 với certificate pin từ mã mời; message/event có HMAC-SHA256. UDP discovery có HMAC nhưng metadata hiện diện không mã hóa.
- Mã mời chứa secret và chứng thư/khóa dùng chung của cluster. DPAPI CurrentUser bảo vệ chúng khi lưu local. Thành viên có mã mời được coi là tin cậy; không có chứng thực khóa riêng từng node hay chống thành viên ác ý giả danh. SQLite và tệp đính kèm chưa mã hóa khi lưu.
- Các máy nên đồng bộ giờ: envelope có cửa sổ thời gian 120 giây. HLC và sequence giải quyết thứ tự/đồng bộ; không thay thế kiểm tra giờ của xác thực.
- Không tự loại voter offline vì việc đó làm sai quorum. Nếu mất vĩnh viễn đa số voter, cần phục hồi máy/dữ liệu đã có; chưa có công cụ quản trị loại voter hoặc khôi phục cưỡng bức.
- Binary chỉ ở máy đã gửi/tải. Không còn nguồn trực tuyến thì vẫn giữ metadata, báo chưa tải được. Tối đa 20 ảnh/tệp mỗi tin, 20 GB/tệp; streaming, progress, hủy, SHA256, tệp tạm và đổi tên nguyên tử.
- Ảnh JPEG/PNG/GIF/BMP/TIFF đang nằm trong vùng chat hiển thị sẽ tự tải về cache local để tạo hình thu nhỏ (tối đa 20 MB/ảnh, hai tác vụ ảnh đồng thời). Ảnh lớn hơn cần bấm để tải/xem. Khung xem lớn hỗ trợ tối đa 100 MB và 40 megapixel; ảnh vượt giới hạn có thể mở bằng **Ứng dụng ngoài**. GIF/TIFF chỉ hiển thị khung/trang đầu. Ảnh đã tải có thể xem lại khi máy gửi offline. Khung xem dùng ảnh gốc đã gửi; sửa chú thích bằng thao tác sửa tin nhắn.
- Tệp mở bằng ứng dụng ngoài giữ tên/đuôi gốc trong `Data/Attachments/Open/<AttachmentId>/`. Lần bấm sau dùng lại bản local này, kể cả khi nguồn offline. Chỉnh sửa bằng Word/Excel chỉ thay đổi bản mở local, không tự gửi lại hoặc thay đổi blob gốc mà các peer tải xuống. Muốn chia sẻ nội dung đã sửa, gửi lại tệp đó.
- Chưa có compaction/snapshot control log, retention hoặc benchmark cụm lớn. Bản hiện tại phù hợp đánh giá trong mạng nội bộ tin cậy trước khi nghiệm thu thực tế.
- Log cuốn trong `Data/Logs`, tối đa 14 tệp, 5 MB/tệp; không ghi secret hoặc nội dung chat.
- Sao lưu cả thư mục khi app đã đóng. DPAPI gắn với tài khoản Windows; không coi bản sao cluster.json là mã mời cho máy khác.

## Tài liệu

- [Kiến trúc và luồng dữ liệu](docs/ARCHITECTURE.md)
- [Kết quả kiểm chứng](docs/VERIFICATION.md)
- [Checklist nghiệm thu trên các PC](docs/ACCEPTANCE.md)
- [Phạm vi triển khai Phase 2–5](docs/PHASES_IMPLEMENTATION.md)
