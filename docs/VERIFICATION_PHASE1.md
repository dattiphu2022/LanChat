# Kết quả kiểm chứng Phase 1

Báo cáo lưu kết quả Phase 1 với target .NET 9 tại thời điểm kiểm thử. Xem [Kết quả kiểm chứng](VERIFICATION.md) cho bản hiện hành dùng .NET Framework 4.8 và [Hướng dẫn sử dụng](HUONG_DAN_SU_DUNG.md) cho thao tác trên ứng dụng.

Ngày kiểm chứng: **21/09/2026**, Windows, SDK **10.0.400**, target **net9.0/net9.0-windows**, Desktop Runtime **9.0.19**.

## Build và migration

- `dotnet build LanChat.sln --no-restore`: đạt, **0 warning / 0 error**.
- `dotnet publish src/LanChat.WinForms -c Release --no-restore --self-contained false -o artifacts/Phase1`: đạt; EXE và dependencies đã tạo.
- `dotnet ef migrations has-pending-model-changes --project src/LanChat.Infrastructure --no-build`: không có thay đổi model chưa tạo migration.
- Có hai migration: `InitialPhase1`, `LocalReadState`. Kiểm thử khởi tạo mới, WAL/index, nâng từ schema đầu lên schema sau và giữ UserId/NodeId đều đạt.

## Automated tests

`dotnet test tests/LanChat.Tests --no-restore --logger "trx;LogFileName=phase1.trx"`: **23 passed / 0 failed / 0 skipped**.

Các nhóm kiểm thử:

- Network range /24, /25, /26, /27, /28; thêm /16, /31, /32, mask không liên tục; bỏ IP chính máy/network/broadcast.
- DirectConversation identity ổn định, tách theo cluster; HLC tiến qua clock từ xa.
- HMAC sai secret/tamper/replay bị loại, frame quá lớn bị chặn trước cấp phát payload.
- Migrate SQLite, WAL và unique index; giữ identity khi đổi tên/restart; nâng schema không mất hồ sơ.
- Nhận trùng chỉ một message, payload khác nhưng trùng identity bị từ chối.
- UDP discovery thật và TCP chat hai chiều không có server riêng.
- Network failure: tin còn LocalPending trong DB, restart rồi TCP hoạt động lại thì tự giao thành công.
- Cluster khác không lấy được NodeInfo/không nhập dữ liệu.
- Offline user còn trong danh bạ; đổi IP/tên không đổi direct conversation.
- 20 lần gửi local đồng thời có sequence riêng.
- Scanner dùng TCP thật và hỗ trợ cancellation.
- Tin đến muộn với CreatedAt cũ vẫn được tính unread sau khi đã mark-read tin trước.

TRX trên máy phát triển: `tests/LanChat.Tests/TestResults/phase1.trx`. Các test dùng DB/thư mục riêng trong `%TEMP%/LanChat.Tests`, không tác động dữ liệu sử dụng thật.

## Hai tiến trình WinForms

Đã chạy thành công hai chế độ:

1. UDP loopback có target tường minh, hai cổng UDP khác nhau — kiểm tra độc lập với broadcast policy.
2. **UDP broadcast mặc định, cùng discovery port, không cấu hình endpoint peer** — hai tiến trình tự tìm thấy nhau.

Lần broadcast thành công: `.codex-temp/two-instances-20260921-142928`.

- Hai EXE process, hai thư mục dữ liệu/SQLite/NodeId/UserId, TCP port riêng.
- Hai phía tự thấy peer, mở conversation và gửi “Xin chào từ Máy A/B” qua cùng handler gửi của UI.
- Hai DB cùng hai MessageIds, đều Delivered, pending=0.
- Đã render và xem ảnh ở client size **1240×800** và **1020×650**.
- Harness kiểm tra navigation, message bubbles và ô nhập hiện diện, kích thước đủ và không bị che bởi parent.
- Đã xem cửa sổ Settings và Diagnostics. Các tiến trình do harness tạo tự thoát, không còn giữ cổng.

Ảnh và báo cáo được lưu trong [verification/](verification/). Báo cáo chỉ chứa định danh và tin mẫu; không chứa secret/mã mời.

## Giới hạn của bằng chứng

Kiểm tra trên **hai tiến trình của cùng một máy Windows**, chưa trực tiếp chạy trên hai PC vật lý. Firewall, Wi-Fi client isolation, VLAN và topology tại nơi triển khai cần kiểm tra theo [ACCEPTANCE.md](ACCEPTANCE.md).

Leader/election/quorum/partition/full-history replication/file transfer chưa được triển khai hoặc kiểm thử; đây là phạm vi các phase sau. Bản publish Release được build thành công; harness gửi/nhận và chụp UI chạy trên Debug vì hook kiểm thử đã bị loại khỏi Release.
