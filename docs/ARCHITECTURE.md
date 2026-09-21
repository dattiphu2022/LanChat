# Kiến trúc Phase 1

## Phân lớp

Domain không phụ thuộc EF, mạng hoặc UI. Application tham chiếu Domain; Infrastructure triển khai các hợp đồng Application. WinForms chỉ gọi application services/store contracts, không có EF query trong handler, không xử lý socket trong Form.

Generic Host đăng ký DbContextFactory, ChatStore singleton, ChatService, network info, WireProtocol, PeerTransport, LanScanner và bốn hosted services. ChatStore dùng DbContext mới cho mỗi thao tác, một semaphore cho transaction ghi trong process. Khóa `instance.lock` ngăn hai instance dùng chung DB. SQLite WAL hỗ trợ đọc đồng thời.

## Gửi và nhận

```text
ChatInput → ChatService → transaction SQLite:
    local sequence + HLC + Conversation/Members + Message(LocalPending)
→ trả về UI
→ PendingMessageDeliveryService đọc tối đa 50 tin đến hạn
→ TCP ChatPush (DTO + HMAC)
→ người nhận validate + transaction + unique indexes
→ ACK chỉ sau commit
→ người gửi cập nhật Delivered
```

Nếu ACK mất sau commit, người gửi thử lại cùng MessageId/ClientMessageId/OriginSequence với một envelope RequestId mới. Người nhận nhận diện bản trùng và ACK lại. Xung đột cùng định danh nhưng khác payload bị từ chối. Các recipient khác nhau gửi song song tối đa 8; tin của từng recipient gửi theo sequence. Sau lỗi tăng retry count, giữ nguyên text/identity, hẹn lại tối đa 30 giây. Không có thao tác xóa tin tự động.

Phase 1 chưa có immutable event store. Không gọi cơ chế retry này là full replication: tin mới chỉ đến người nhận trực tiếp. Khi làm Phase 2, cần thêm ReplicationEvent/Outbox/Cursor vào cùng transaction với projection và migration dữ liệu cũ.

## Discovery và identity

UDP listener dùng cổng chung với ReuseAddress; broadcast tới directed broadcast của các NIC phù hợp và limited broadcast. DiscoveryRequest/Response có NodeInfo DTO, user profile, node identity, service port, protocol/application version và cluster trong envelope. IP lấy từ địa chỉ nguồn gói tin, không dùng làm khóa user.

Discovery mỗi 5 giây làm mới LastSeen và hồ sơ. Hết 25 giây UI hiển thị Offline nhưng giữ dữ liệu. Tên/IP/port thay đổi không tạo một direct conversation khác. AvatarPath chỉ là dữ liệu local, không gửi đường dẫn filesystem sang máy khác.

TCP fallback không dùng ping. Scan có concurrency 32 và timeout 600 ms, endpoint chỉ được ghi nhận nếu trả lời NodeInfo đúng protocol và HMAC. Scan thủ công cancel được; scan tự động giới hạn subnet nhỏ. `--discovery-target IPv4:port` là tùy chọn kiểm thử/diagnostic, không phải cấu hình server.

## Ranh giới bảo mật

Mỗi bộ dữ liệu thuộc một cluster đã tạo/tham gia rõ ràng. Cluster không khớp, HMAC sai, gói quá lớn, quá hạn, phát lại hoặc sai protocol bị loại. Peer có cùng shared secret được coi là thành viên tin cậy; chưa ngăn một thành viên cố ý giả mạo node khác. Không có TLS trong Phase 1.

TCP framing gồm length 4 byte big-endian và JSON UTF-8, tối đa 128 KiB. UDP tối đa 8 KiB. Message text tối đa 16.000 ký tự. Mỗi kết nối có timeout, tối đa 32 handler đồng thời. Replay cache giới hạn 10.000 RequestId trong cửa sổ khoảng 125 giây.

## Đường đi các phase tiếp theo

1. Phase 2: bổ sung nhóm, immutable events/projections, durable outbox, cursors liên tục không bỏ qua gap, election state bền vững, heartbeat và membership có quorum đã thống nhất. Không lấy số peer vừa scan thấy làm mẫu số quorum tùy ý.
2. Phase 3: reconciliation/batch, full-history recovery, conflict rules xác định, edit/recall/reaction/reply/forward và search FTS5. Cần test reordered/delayed events và full replay.
3. Phase 4: metadata event riêng, binary local, streaming/temp/atomic rename/hash/cancel và drag/drop preview chờ Send.
4. Phase 5: node authentication, encrypted transport, rotation/revocation, control-plane quorum và thử nghiệm network partition. Không quảng cáo quorum an toàn trước khi test được partition và membership change.

Không có Leader giả hoặc election stub trong Phase 1. `NodeRole` là enum dự phòng cho hợp đồng tương lai; runtime hiện kết nối trực tiếp.
