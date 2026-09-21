# LanChat — Chat nội bộ P2P bằng .NET 9 WinForms

Phase 1 đã triển khai: mỗi instance có SQLite và UserId/NodeId riêng, tự tìm peer trong cùng cluster, chat trực tiếp qua TCP, lưu tin trước khi gửi và tự thử lại khi người nhận kết nối lại. Không có server cố định, không dùng giao diện web, không cần Internet khi chạy.

## Mở bản đã build

Trên máy Windows đã có **.NET 9 Desktop Runtime**, mở:

```text
artifacts\Phase1\LanChat.WinForms.exe
```

Khi chuyển sang máy khác, sao chép **toàn bộ thư mục** `artifacts\Phase1`, không chỉ tệp EXE. Đây là bản framework-dependent, chưa kèm runtime.

1. Máy A: nhập tên, chọn **Tạo mạng chat**.
2. Trong **Cài đặt → Mạng chat → Hiện mã mời tham gia**, sao chép mã và cung cấp cho máy B.
3. Máy B: nhập tên, dán mã, chọn **Tham gia**. Hai máy cần cùng mạng LAN và cùng cổng discovery.
4. Mở **Người dùng**, chọn người cần chat. UDP discovery chạy mỗi 5 giây.
5. Nhập tin, bấm **Gửi** hoặc Enter. Shift+Enter xuống dòng. Tin chưa giao được có trạng thái **Chưa đồng bộ**; nhận ACK sau khi máy kia lưu DB thì đổi sang **Đã nhận**.
6. Nếu không thấy người dùng, chọn **Quét lại mạng**. Nút đổi thành **Dừng quét** trong lúc quét TCP.

Không cần nhập IP server. Mỗi máy cần tạo dữ liệu riêng; không sao chép database của A sang B.

## Build, test và chạy từ source

PowerShell, thư mục làm việc:

```powershell
Set-Location 'C:\Users\Admin\source\repos\dattiphu2022\LANChatAppByMrD'
dotnet restore LanChat.sln
dotnet build LanChat.sln --no-restore
dotnet test tests/LanChat.Tests --no-restore
dotnet run --project src/LanChat.WinForms --no-build
```

Project khởi động trong Visual Studio: **LanChat.WinForms**. Cần SDK có thể target .NET 9 và thành phần Windows Desktop. Máy phát triển này đã build bằng SDK 10.0.400, chạy trên .NET 9.0.19.

Publish:

```powershell
dotnet publish src/LanChat.WinForms -c Release --self-contained false -o artifacts/Phase1
```

## Hai instance trên cùng máy

Mở hai cửa sổ PowerShell ở thư mục solution:

```powershell
# Instance A
dotnet run --project src/LanChat.WinForms --no-build -- --data-dir .codex-temp/manual-A --port 54546
```

```powershell
# Instance B: TCP khác, UDP vẫn cùng cổng để broadcast tự tìm nhau
dotnet run --project src/LanChat.WinForms --no-build -- --data-dir .codex-temp/manual-B --port 54547
```

Tạo cluster ở A rồi nhập cùng mã ở B như hướng dẫn trên. Mỗi instance giữ nguyên `--data-dir` và cổng khi mở lại. Không dùng cùng thư mục dữ liệu cho hai tiến trình; ứng dụng có khóa chống mở trùng.

Kiểm tra tự động bằng hai **tiến trình WinForms thật**, có chụp giao diện và tự đóng các tiến trình do bài kiểm tra tạo:

```powershell
dotnet build LanChat.sln
# UDP broadcast mặc định, không cấu hình endpoint peer
pwsh -File scripts/Verify-TwoInstances.ps1 -UseBroadcast
# Kiểm tra độc lập với chính sách broadcast: UDP loopback giữa hai cổng
pwsh -File scripts/Verify-TwoInstances.ps1
```

Script yêu cầu **PowerShell 7**, tạo dữ liệu mẫu trong `.codex-temp/two-instances-<thời điểm>` và không ghi vào dữ liệu sử dụng thật. Dữ liệu mẫu gồm hai hồ sơ, cùng mã cluster, hai lời chào nhiều dòng/Unicode. Mỗi lần chạy tạo thư mục mới; để bắt đầu lại chỉ cần chạy lại script. Không có lệnh reset/xóa dữ liệu thật.

Các tệp `A.json`, `B.json`, ảnh `.png`, cây layout và database được giữ lại để kiểm tra. Chế độ `--verify-ui` chỉ tồn tại trong Debug. Chi tiết kết quả thực tế: [docs/VERIFICATION.md](docs/VERIFICATION.md).

## Mạng và Firewall

Mặc định: **UDP 54545** cho discovery, **TCP 54546** cho chat và NodeInfo. UDP response công bố TCP port riêng nên hai instance trên một máy có thể dùng hai TCP port khác nhau.

Nếu Windows Firewall hỏi, chỉ cho phép ứng dụng trên mạng nội bộ tin cậy. Có thể nhờ quản trị viên chạy các lệnh dưới đây trong PowerShell Administrator trên từng máy; dự án **không tự sửa firewall**:

```powershell
$lanChatExe = (Resolve-Path '.\artifacts\Phase1\LanChat.WinForms.exe').Path
New-NetFirewallRule -DisplayName 'LanChat TCP Private' -Direction Inbound -Program $lanChatExe -Protocol TCP -LocalPort 54546 -RemoteAddress LocalSubnet -Profile Private -Action Allow
New-NetFirewallRule -DisplayName 'LanChat UDP Private' -Direction Inbound -Program $lanChatExe -Protocol UDP -LocalPort 54545 -RemoteAddress LocalSubnet -Profile Private -Action Allow
```

Sửa TCP port trong rule nếu dùng cổng khác. Máy khác/VLAN khác, Wi-Fi client isolation hoặc mạng chặn broadcast có thể cần quản trị viên điều chỉnh. TCP scan chỉ tìm cổng service đang cấu hình; không dò tất cả cổng.

Thông tin mạng lấy bằng `NetworkInterface.GetAllNetworkInterfaces()`: IPv4/mask/gateway, ưu tiên Ethernet/Wi-Fi đang Up, lọc adapter ảo/VPN phổ biến. Tính subnet theo mask thật, không hard-code /24. Tên adapter ảo do nhà cung cấp tùy biến có thể cần bổ sung bộ lọc.

Scan TCP: 32 kết nối song song, timeout 600 ms, bỏ địa chỉ máy mình/network/broadcast, không cần ping. Quét tự động sau 10 giây rồi mỗi 120 giây với tổng subnet tối đa 4.096 host để tránh tạo lưu lượng lớn. Quét thủ công vẫn hỗ trợ subnet lớn, duyệt địa chỉ từng phần và cho phép hủy. /31 và /32 dùng quy tắc point-to-point/host route.

## Dữ liệu, bảo mật và log

- Mặc định: `%LOCALAPPDATA%\LanChat\Data\lanchat.db`; `--data-dir` thay đổi vị trí. Chọn ổ local, không đặt trên thư mục mạng.
- `UserId`, `NodeId` sinh một lần, độc lập với IP/tên máy/tên hiển thị. Entity mới dùng `Guid.CreateVersion7()`; DirectConversation dùng Guid suy ra ổn định từ ClusterId và hai UserId đã sắp thứ tự.
- EF Core migration tự chạy lúc mở; SQLite WAL; mỗi thao tác lưu tin và cấp sequence nằm cùng transaction. Tin nhận có unique index theo ClientMessageId và OriginNodeId/OriginSequence.
- Tin đang chờ được lưu ngay trong bảng Messages ở Phase 1. Retry exponential backoff tối đa 30 giây, vẫn tiếp tục sau restart. **Chưa phải Event Store/Outbox replication của Phase 2**.
- Peer offline vẫn còn trong Users/PeerNodes. Presence Online hết hạn sau 25 giây không nhận discovery/tin. Không coi không scan thấy là xóa user.
- Thứ tự tin dùng PhysicalTicks, LogicalCounter, OriginNodeId, Id. Trạng thái đọc local dùng cờ từng tin, không suy ra từ đồng hồ máy gửi.
- Hiển thị 200 tin mới nhất trước; **Xem tin nhắn cũ hơn** nạp thêm 200. UI dùng lại conversation/message controls; không dựng lại toàn bộ danh sách mỗi lần nhận tin.
- Mã mời chứa ClusterId và secret. Secret lưu local bằng Windows DPAPI CurrentUser; không gửi secret qua socket, không ghi secret/nội dung chat vào log.
- Gói UDP/TCP có version, RequestId, HMAC-SHA256, giới hạn kích thước, thời hạn 120 giây và kiểm tra phát lại. Các máy nên đồng bộ giờ. Phase 1 xác thực theo **secret dùng chung trong cluster**, chưa có chứng thực khóa riêng từng node.
- **Nội dung chat trên đường truyền chưa được mã hóa**; HMAC kiểm tra xác thực/toàn vẹn, không cung cấp tính bí mật. TLS, phân quyền node và hardening thuộc Phase 5.
- Log cuốn theo ngày/dung lượng trong `Data\Logs`, giữ tối đa 14 file, 5 MB/file.
- Sao lưu cả thư mục dữ liệu khi ứng dụng đã đóng. DPAPI gắn với tài khoản Windows hiện tại; không coi bản sao `cluster.json` là mã mời sử dụng được trên tài khoản/máy khác.

## Cấu trúc và giới hạn Phase 1

```text
LanChat.sln
src/LanChat.Domain          Entity, enum, direct identity, Hybrid Logical Clock
src/LanChat.Application     Interface, DTO, ChatService
src/LanChat.Infrastructure  EF/SQLite/migration, socket, discovery, scan, retry, logging
src/LanChat.WinForms        Generic Host startup, Forms, UserControls, debug UI verification
tests/LanChat.Tests         Unit và integration test
scripts                    Kiểm tra hai tiến trình
docs                       Kiến trúc, kết quả, checklist nghiệm thu
```

Đã có hồ sơ local, avatar local, danh bạ Online/Offline, tìm tên người dùng/cuộc trò chuyện, chat text/Unicode/multiline, copy, ghim, đánh dấu đọc, cài đặt mạng/lưu trữ/cluster, cửa sổ chẩn đoán và đóng background services có cancellation. Tùy chọn im lặng được lưu local; Phase 1 chưa phát thông báo desktop.

**Chưa triển khai**: chat nhóm, leader/quorum/failover, immutable event store, replication toàn cluster/anti-entropy/cursor, đồng bộ read state, sửa/thu hồi/reaction/reply/forward, tìm nội dung tin bằng FTS, kéo thả/tải file P2P, TLS và phân quyền node. Hai bên cuộc trò chuyện giữ lịch sử của họ; máy C chưa nhận toàn bộ lịch sử A/B. Menu Nhóm/File hiển thị “Sắp có” và bị vô hiệu hóa.

Xem [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) và [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md) trước khi triển khai các phase tiếp theo.
