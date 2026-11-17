# LanChat v1.2.1 — Chat nội bộ cho Windows

LanChat hỗ trợ chat riêng, chat nhóm và truyền ảnh, tệp, thư mục giữa các máy Windows trên mạng LAN. Mỗi máy lưu dữ liệu trong SQLite. Tin nhắn và tệp được mã hóa đầu cuối. Kiểm tra và tải bản cập nhật từ GitHub cần Internet.

## Tải và bắt đầu sử dụng

1. Tải **LanChat-Setup-Windows-x64.exe** từ [GitHub Releases](https://github.com/dattiphu2022/LanChat/releases/latest) và chạy bộ cài. Hỗ trợ Windows 10 từ phiên bản 1903 và Windows 11 x64 với .NET Framework 4.8.
2. Mở **Cài đặt** để nhập **Tên** và **Đơn vị**. Danh bạ hiển thị đơn vị trước tên.
3. Mở **Mạng chat → Thêm mã** nếu đã có mã. Chọn **Tạo mạng** nếu bạn muốn bắt đầu mạng mới. Để mời máy khác, mở **Chi tiết → Hiện mã mời tham gia** và gửi mã cho người được phép tham gia.
4. Chọn người dùng trong **Danh bạ** hoặc bấm **Quét tất cả** để tìm lại máy. Dùng các tab để đổi mạng.

Mở **Giới thiệu → Hướng dẫn sử dụng** để đọc hướng dẫn ngay trong ứng dụng, kể cả khi đang ngoại tuyến. Tài liệu này cũng có trên GitHub. [Hướng dẫn sử dụng](docs/HUONG_DAN_SU_DUNG.md)

## Nhắn tin, gửi ảnh và tệp

- **Enter** gửi tin, **Shift+Enter** xuống dòng, **Ctrl+K** tìm hội thoại và **Ctrl+F** tìm tin. Bấm khung trích dẫn để đến tin gốc, rồi chọn **Quay về tin nhắn mới** để trở lại cuối hội thoại.
- Dùng **Dán ảnh**, **Chụp vùng** hoặc kéo tệp vào ô soạn tin. LanChat tạm ẩn khi chụp vùng và hiện lại khi chụp xong hoặc nhấn **Esc**.
- Nội dung nhập cùng ảnh là chú thích chung cho lần gửi. Bấm ảnh để mở khung xem lớn. Dùng **Trước/Sau** hoặc phím mũi tên để chuyển ảnh, cuộn chuột để phóng to và **Esc** để đóng.
- Dùng **Đính kèm → Thêm thư mục** để chọn thư mục từ Windows. Mỗi thư mục có thể chứa tối đa 10.000 mục và 20 GB dữ liệu. Bấm tên tệp để tải, kiểm tra SHA-256 và mở bằng ứng dụng mặc định.

Tin đã đồng bộ nghĩa là ít nhất một máy khác có bản sao. Trạng thái **Đã nhận** và **Đã đọc** dựa trên xác nhận của người tham gia. Ghim, tắt thông báo và xóa phía tôi áp dụng trên máy này.

Mạng có ba voter đang hoạt động ổn định chịu được một máy mất kết nối mà vẫn thay đổi cấu trúc nhóm. Khi thiếu số máy điều phối cần thiết, tin nhắn vẫn được lưu và có thể đồng bộ giữa các máy còn kết nối.

Trong **Cài đặt**, bật **Khởi động cùng hệ thống** để mở LanChat khi đăng nhập Windows. Nhấn **Thu nhỏ** hoặc **X** để đưa LanChat xuống khay hệ thống. Nhấp đúp biểu tượng để mở lại, hoặc bấm chuột phải và chọn **Thoát LanChat** để dừng ứng dụng.

## Mã mặc định cho nhiều máy

Bản phát hành nội bộ tự tham gia mạng mặc định được nhúng trong EXE. Khi cài bản có mã mới, LanChat tự chuyển mạng mặc định đang bật và tiếp tục lưu lịch sử theo từng mã. Nếu đã gỡ mạng mặc định, dùng **Khôi phục mạng mặc định** trên thanh công cụ của cửa sổ chính để bật lại. Xem [hướng dẫn bộ cài](installer/README.md) để cấp hoặc đổi mã cho bản build.

## Nâng cấp

Với bản đã có trình cập nhật, mở **Giới thiệu → Kiểm tra cập nhật**. Khi có bản mới hơn, đóng Giới thiệu và chọn **Tải và cập nhật**. Có thể cài thủ công EXE từ GitHub Releases bằng cùng tài khoản Windows. Để thoát trước khi cài thủ công, dùng **Thoát LanChat** ở khay hệ thống.

Khi nâng từ giao thức cũ lên **giao thức v4** lần đầu, migration xóa lịch sử tin nhắn cũ, chỉ mục tìm kiếm và các tin đang chờ gửi. Sao lưu khi LanChat đã thoát nếu cần lưu dữ liệu cũ để tra cứu. Bảo vệ bản sao vì có thể chứa dữ liệu dạng rõ.

Ứng dụng giữ mã mời v2, danh tính cục bộ, danh bạ và cấu hình từng mạng. Nhóm đang hoạt động được tạo lại từ danh sách thành viên trên máy chủ nhóm khi máy đó có kết nối đến máy điều phối. Những người nhận cần chạy bản mới ít nhất một lần để LanChat ghi nhận khóa trước khi gửi tin cho họ.

Nếu cửa sổ **Nâng cấp mạng chat** xuất hiện với cấu hình Phase 1, chọn một máy tạo mã nâng cấp và nhập cùng mã đó trên các máy còn lại. Dùng thư mục dữ liệu riêng của từng máy. Máy tạo mã cần trực tuyến để thiết lập điều phối ban đầu.

## Build, test và publish

PowerShell tại thư mục solution:

```powershell
# Chạy tại thư mục chứa LanChat.sln
dotnet restore LanChat.sln
dotnet build LanChat.sln --no-restore
dotnet test tests/LanChat.Tests --no-restore
dotnet run --project src/LanChat.WinForms --no-build
```

Project khởi động trong Visual Studio là **LanChat.WinForms**. Máy build cần .NET SDK hỗ trợ C# 13 và reference assemblies .NET Framework 4.8.

Để làm bộ cài công khai, chọn **LanChat.WinForms → Publish → PublicRelease**, rồi mở `installer/LanChat.Public.iss` và chọn **Build → Compile** trong Inno Setup. Tệp `LanChat-Setup-Windows-x64.exe` nằm trong `artifacts/public-release/installer`. [Hướng dẫn đóng gói và phát hành](installer/README.md)

```powershell
pwsh -NoProfile -File scripts/Publish-Windows.ps1 -PublicRelease
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
- Nội dung tin riêng, tin nhóm, sửa tin, cảm xúc, xác nhận đọc và khóa tệp được mã hóa đầu cuối. Máy ngoài cuộc có thể chuyển tiếp bản mã. Tệp, ảnh và thư mục được mã hóa trước khi truyền, rồi giải mã trên thiết bị người nhận.
- Mỗi thiết bị giữ khóa riêng được bảo vệ bằng DPAPI CurrentUser. LanChat ghim khóa công khai lần đầu gặp và từ chối khóa thay thế cho cùng định danh. Đối chiếu dấu vân tay với người liên hệ qua kênh tin cậy ở **Mạng chat → Chi tiết → Khóa mã hóa** để xác minh lần kết nối đầu.
- TLS và HMAC bảo vệ giao thức mạng chat. Máy trong mạng vẫn thấy thông tin danh bạ, hiện diện, tên nhóm, thành viên, thời gian và định danh dùng để đồng bộ. Tin đã giải mã, chỉ mục tìm kiếm và tệp đã mở lưu ở dạng rõ tại thiết bị được cấp quyền. Việc lộ khóa riêng có thể ảnh hưởng đến bản mã đã lưu trước đó.
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

- [Hướng dẫn sử dụng](docs/HUONG_DAN_SU_DUNG.md)
- [Đóng gói và phát hành](installer/README.md)
- [Kiến trúc và luồng dữ liệu](docs/ARCHITECTURE.md)
- [Kết quả kiểm chứng](docs/VERIFICATION.md)
- [Checklist nghiệm thu trên các PC](docs/ACCEPTANCE.md)
- [Phạm vi triển khai Phase 2–5](docs/PHASES_IMPLEMENTATION.md)
