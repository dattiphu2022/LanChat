# Checklist nghiệm thu Phase 1

## Chuẩn bị

- [ ] Hai máy Windows có .NET 9 Desktop Runtime, cùng LAN; sao chép cả thư mục publish.
- [ ] Mỗi máy có thư mục dữ liệu local riêng, không clone UserId/NodeId từ máy còn lại.
- [ ] Cho phép TCP 54546/UDP 54545 trên mạng Private khi firewall đang chặn.
- [ ] Máy A tạo mạng chat; máy B tham gia bằng cùng mã. Mã không đặt trong log/tài liệu chia sẻ công khai.

## Nghiệm thu chức năng

- [ ] Sau tối đa 5–10 giây A thấy B, B thấy A; tên máy, IP, trạng thái hiện đúng.
- [ ] Bấm người dùng để tạo direct chat; đổi tên hoặc restart không tạo cuộc trò chuyện mới.
- [ ] A gửi “Xin chào 👋” và tin nhiều dòng; B nhận, phản hồi được; hai phía hiện Đã nhận.
- [ ] Đóng B, gửi thêm ở A: tin xuất hiện ngay, còn Chưa đồng bộ. Mở B lại: tin đến trong khoảng discovery/retry, không trùng.
- [ ] Đóng/mở A khi đang có tin chờ: UserId/NodeId và tin chờ còn nguyên.
- [ ] Đổi DisplayName, đơn vị và avatar: lưu được, tên được cập nhật ở peer qua discovery; avatar hiện local.
- [ ] Người offline vẫn trong danh bạ; có Last Seen.
- [ ] Chọn Quét lại mạng; có thể hủy; UI vẫn thao tác được.
- [ ] Thu nhỏ cửa sổ; navigation, danh sách, bubble và nút Gửi không bị che.
- [ ] Copy, ghim/bỏ ghim, đánh dấu đọc hoạt động. Xem tin cũ hơn khi có trên 200 tin.
- [ ] Chẩn đoán hiển thị Node/IP/user/presence/Last Seen/pending; Copy Diagnostics không chứa mã mời.
- [ ] Đóng app sạch; có thể mở lại ngay mà không bị giữ cổng/khóa DB.

## Kiểm chứng dữ liệu và cô lập

- [ ] `dotnet test` đạt toàn bộ test; migration không có thay đổi model chưa tạo migration.
- [ ] `Verify-TwoInstances.ps1 -UseBroadcast` tạo hai report Success=true, cùng MessageIds, NodeId/UserId khác nhau, tất cả message Delivered.
- [ ] Một app tạo cluster khác không tự thấy/merge dữ liệu với A/B.
- [ ] Không chia sẻ hoặc copy `lanchat.db` qua mạng. Sao lưu sau khi đóng ứng dụng.

## Chưa nằm trong nghiệm thu này

Leader election/failover/quorum, nhóm, full history trên mọi node, file, reaction/edit/recall/reply/forward, TLS và thử nghiệm partition thuộc Phase 2–5. Kết quả hai process trên một host không thay cho kiểm tra firewall/Wi-Fi/VLAN của hai máy vật lý tại nơi triển khai.
