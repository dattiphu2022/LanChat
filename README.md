# LanChat — Chat nội bộ P2P cho Windows 11

Đã triển khai Phase 1–5: chat trực tiếp/nhóm, tự bầu điều phối, lịch sử và sự kiện đồng bộ giữa các máy, làm việc khi mất kết nối, tệp P2P, tìm kiếm, TLS/HMAC và chẩn đoán. Ứng dụng chạy hoàn toàn trong LAN, giao diện WinForms native; mỗi máy có SQLite riêng.

## Mở bản chạy

Giải nén **`LanChat-Win11-x64.zip`** rồi mở **`LanChat.WinForms.exe`** trong thư mục vừa giải nén. Giữ các DLL bên cạnh EXE. Ứng dụng dùng .NET Framework 4.8/4.8.1 có sẵn trên Windows 11 và chạy với tài khoản thường.

1. Mở LanChat để vào thẳng **Mạng mặc định**. Sửa **Tên** và **Đơn vị** trong **Cài đặt**.
2. Vào **Mạng chat** để thêm mã, mỗi mã một dòng. Khi kết nối mạng riêng đầu tiên, LanChat hỏi có gỡ mạng mặc định hay giữ cả hai. Để mời máy khác vào một mạng, chọn **Chi tiết → Hiện mã mời tham gia**.
3. Dùng các tab để đổi mạng. **Quét tất cả** tìm máy đồng thời trên các mạng đã kết nối. Vào **Mạng chat** để thêm mã, đổi tên hoặc gỡ mạng. Khi nhập lại mã cũ, LanChat mở lại lịch sử đã lưu của mạng đó.
4. Vào **Danh bạ** hoặc tìm tên ở ô tìm hội thoại để chat. Tin được lưu trước khi gửi ra mạng.
5. Vào **Nhóm**, chọn thành viên và tạo nhóm. Chủ nhóm có thể đổi tên, thêm/xóa thành viên, chuyển quyền và xóa nhóm.
6. Kéo nhiều ảnh/tệp vào khung chat hoặc bấm **Đính kèm**; kiểm tra preview rồi bấm **Gửi**. Nội dung nhập cùng ảnh là **chú thích chung** cho các ảnh trong lần gửi đó. **Ảnh hiện trực tiếp trong tin nhắn**; bấm ảnh mở khung xem lớn có chú thích và dải ảnh của cuộc trò chuyện. Dùng **Trước/Sau**, phím **← →** hoặc bấm hình thu nhỏ để chuyển ảnh; cuộn chuột để zoom, kéo để di chuyển, **Vừa khung/100%** để đổi tỷ lệ và **Esc** để đóng.
   **Bấm tên tệp khác để mở bằng ứng dụng mặc định của Windows**. Nếu chưa có local, LanChat tự tải vào dữ liệu máy, kiểm tra SHA256 rồi mở. Tải lâu sẽ hiện tiến độ và nút Hủy. Trong khung xem ảnh có nút **Ứng dụng ngoài**; Trong bảng **Thông tin**, chọn **Tệp** để tìm tệp đã gửi.
7. Nhấp phải tin nhắn để trả lời, sao chép, chuyển tiếp, cảm xúc, sửa, thu hồi hoặc xóa phía tôi. Bấm **Tìm tin** trong hội thoại và nhấp đúp kết quả để mở đúng vị trí trong lịch sử.
8. Nhấp phải một hội thoại và chọn **Xóa hội thoại phía tôi** để bỏ lịch sử trên máy này. Tin mới sẽ đưa hội thoại trở lại danh sách. Tin đến khi đang ở hội thoại khác hoặc ở khay hệ thống có thể hiện thông báo Windows. Bấm thông báo để mở hội thoại hoặc dùng **Tắt / Bật thông báo** trong menu hội thoại.
9. Kéo thư mục từ Explorer vào ô soạn tin hoặc chọn **+ Đính kèm → Thêm thư mục**. Khi người nhận bấm tên thư mục, LanChat tải từng tệp và mở thư mục đã dựng lại trong Explorer. Mỗi thư mục có thể chứa tối đa 10.000 mục và 20 GB dữ liệu. Dùng bản LanChat mới ở hai đầu để mở được đính kèm dạng thư mục.

**Enter** gửi, **Shift+Enter** xuống dòng, **Ctrl+K** tìm hội thoại/người dùng và **Ctrl+F** tìm tin. Tin đã đồng bộ nghĩa là có ít nhất một bản sao tại peer khác; “đã nhận” và “đã đọc” dựa trên receipt của người tham gia. Ghim, im lặng và xóa phía tôi chỉ áp dụng trên máy này.

Nên có **ít nhất 3 máy đã tham gia ổn định** để chịu được một máy mất kết nối mà vẫn thay đổi nhóm được. Với 2 voter, cần cả hai cho quorum. Khi thiếu quorum, chat vẫn lưu local và đồng bộ qua các peer còn kết nối; các thay đổi cấu trúc nhóm bị từ chối.

Trong **Cài đặt**, bạn có thể sửa Tên, Đơn vị và bật **Khởi động cùng hệ thống** để mở LanChat khi đăng nhập Windows. LanChat dùng chữ cái làm ảnh đại diện và chỉ mở một tiến trình trên máy.

Nhấn **Thu nhỏ** hoặc **X** để đưa LanChat xuống khay hệ thống. Nhấp đúp biểu tượng để mở lại, hoặc bấm chuột phải và chọn **Thoát LanChat** để dừng ứng dụng.

## Mã mặc định cho nhiều máy

Bản phát hành nội bộ tự tham gia mạng mặc định được nhúng trong EXE. Khi cài bản có mã mới, LanChat tự chuyển mạng mặc định đang bật và tiếp tục lưu lịch sử theo từng mã. Nếu đã gỡ mạng mặc định, dùng **Khôi phục mạng mặc định** trên thanh công cụ của cửa sổ chính để bật lại. Xem [hướng dẫn bộ cài](installer/README.md) để cấp hoặc đổi mã cho bản build.

## Nâng cấp

Đóng LanChat và sao lưu toàn bộ thư mục dữ liệu trên từng máy. Thay bản chạy trên cả cụm trong cùng đợt vì kết nối phiên bản mới dùng giao thức 3. Mã mời v2, danh tính, chứng chỉ và lịch sử cũ vẫn dùng được. Mở lại bằng cùng tài khoản Windows để đọc cấu hình DPAPI.

Với dữ liệu Phase 1:

- Mỗi máy vẫn dùng thư mục dữ liệu cũ của chính nó; giữ nguyên UserId/NodeId và lịch sử.
- Cửa sổ **Nâng cấp mạng chat** xuất hiện với cấu hình cũ. Chọn **một máy duy nhất** tạo mã nâng cấp, áp dụng trên máy đó rồi nhập **cùng mã** cho các máy khác.
- Máy tạo mã cần trực tuyến trong giai đoạn thiết lập membership ban đầu. Sau khi có đủ voter, các máy có thể tự bầu điều phối mới.
- Migration tự thêm bảng sự kiện/outbox/cursor/control log và chỉ mục tìm kiếm; tin cũ được bổ sung vào event store một lần.
- Không ghép database của hai cluster và không chép database máy A thành dữ liệu cho máy B.

## Build, test và publish

PowerShell tại thư mục solution:

```powershell
# Chạy tại thư mục chứa LanChat.sln
dotnet restore LanChat.sln
dotnet build LanChat.sln --no-restore
dotnet test tests/LanChat.Tests --no-restore
dotnet run --project src/LanChat.WinForms --no-build
```

Project khởi động trong Visual Studio: **LanChat.WinForms**. Cần .NET SDK có trình biên dịch C# 13 và bộ reference assemblies .NET Framework 4.8 trên máy phát triển. Máy người dùng chạy bằng Framework có sẵn.

```powershell
pwsh -NoProfile -File scripts/Publish-Windows.ps1
```

Bản phát hành mở một cửa sổ. Dùng các tab để kết nối nhiều mạng từ cùng tiến trình.

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

LanChat phát discovery UDP có xác thực khi khởi động, đổi IP hoặc bấm **Quét lại**. TCP dự phòng bắt đầu sau 750 ms, ưu tiên địa chỉ đã biết và dò cổng LanChat trong subnet IPv4 cục bộ. Quét có thể hủy, chạy tối đa 64 kết nối đồng thời cho mỗi mạng. Các mạng dùng chung cổng UDP khám phá, có cổng TCP riêng được công bố qua UDP. Dò TCP dự phòng thử cổng mặc định, cổng tính từ mã mạng và các địa chỉ đã biết.

LanChat xác nhận trạng thái trực tuyến qua TCP/TLS và lưu các địa chỉ đã thấy của mỗi máy. Khi đường gửi tin đang dùng gặp lỗi, ứng dụng thử địa chỉ khác. Một chiều kết nối TCP còn hoạt động có thể đồng bộ tin nhắn theo cả hai chiều.

**Mạng chat → Chi tiết** cho phép ứng cử, đề cử và lưu lựa chọn **Cho phép máy này làm trung tâm**. Sau 5 giây vắng heartbeat, các máy đủ điều kiện bắt đầu kiểm tra phiếu. Khi trung tâm có đa số và đã commit trong nhiệm kỳ mới, ứng dụng đánh thức đồng bộ. Khi thiếu đa số hoặc mọi máy rút lui, các máy còn kết nối vẫn có thể trao đổi tin nhắn.

Nếu Windows Firewall hỏi, cho phép trên mạng nội bộ tin cậy. Từ thư mục mã nguồn, quản trị viên có thể chạy script sau trên từng máy. Quy tắc áp dụng cho EXE đã cài và các cổng TCP của LanChat, giới hạn ở subnet nội bộ trên profile Domain hoặc Private.

```powershell
.\scripts\Enable-LanChatFirewall.ps1 -AppPath 'C:\duong-dan\LanChat.WinForms.exe'
```

Khi bật nhiều mạng, xem cổng TCP thực tế trong **Mạng chat → Chi tiết → Kết nối** và thêm các cổng đó vào rule nếu môi trường yêu cầu lọc theo cổng. VLAN, Wi-Fi client isolation và policy broadcast cần kiểm tra tại nơi triển khai.

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
- Sửa/xóa file gốc sau khi gửi không thay đổi nội dung đã gửi. Nếu bản lưu trong ứng dụng bị hỏng/mất, LanChat thử phục hồi từ file nguồn hoặc bản mở local còn đúng SHA256, rồi tìm bản đúng trên máy khác. File nguồn chỉ được ghi nhớ bằng đường dẫn trong dữ liệu local, không gửi đường dẫn này qua mạng. Nếu không còn bản nguyên vẹn, cần bật máy từng gửi/tải hoặc gửi lại file thành tin nhắn mới; không thay SHA của tin nhắn cũ để chấp nhận nội dung đã sửa.
- Chưa có compaction/snapshot control log, retention hoặc benchmark cụm lớn. Bản hiện tại phù hợp đánh giá trong mạng nội bộ tin cậy trước khi nghiệm thu thực tế.
- Log cuốn trong `Data/Logs`, tối đa 14 tệp, 5 MB/tệp; không ghi secret hoặc nội dung chat.
- Sao lưu cả thư mục khi app đã đóng. DPAPI gắn với tài khoản Windows; không coi bản sao cluster.json là mã mời cho máy khác.

## Tài liệu

- [Kiến trúc và luồng dữ liệu](docs/ARCHITECTURE.md)
- [Kết quả kiểm chứng](docs/VERIFICATION.md)
- [Checklist nghiệm thu trên các PC](docs/ACCEPTANCE.md)
- [Phạm vi triển khai Phase 2–5](docs/PHASES_IMPLEMENTATION.md)
