# Nghiệm thu LanChat trên mạng thật

Bản đã được kiểm thử tự động và chạy hai tiến trình trên cùng Windows. Các mục dưới đây cần làm trên các PC tại nơi sử dụng; không đánh dấu đạt thay cho kiểm tra thực tế.

## Chuẩn bị

- [ ] Ít nhất 3 PC Windows có .NET 9 Desktop Runtime; dùng cùng bản phát hành.
- [ ] Mỗi PC có thư mục dữ liệu local riêng; không clone database/NodeId.
- [ ] Sao lưu dữ liệu cũ khi app đóng trước nâng cấp.
- [ ] Cùng mã mời hoặc mã nâng cấp từ một máy duy nhất.
- [ ] UDP 54545 và TCP 54546 được phép trên LAN tin cậy; kiểm tra Wi-Fi isolation/VLAN.
- [ ] Giờ các PC tương đối đồng bộ, sai lệch dưới cửa sổ xác thực 120 giây.

## Chat và dữ liệu

- [ ] A/B/C tự thấy nhau, IP đổi không làm mất identity/danh bạ.
- [ ] Gửi text/Unicode/nhiều dòng; tin hiện ngay, rồi đồng bộ/đã nhận/đã đọc.
- [ ] Nhấp phải để reply/edit/recall/forward/reaction/delete local; người khác không sửa/thu hồi tin của mình.
- [ ] Thu hồi biến mất khỏi tìm kiếm; xóa phía tôi không xóa ở peer.
- [ ] Tìm không dấu trong nội dung tiếng Việt; mở đúng vị trí tin cũ.
- [ ] Ghim, im lặng và thông báo khi cửa sổ không có focus hoạt động theo cài đặt.
- [ ] Tắt một node, gửi nhiều tin trên các node khác rồi bật lại; lịch sử hội tụ, không trùng.
- [ ] Node mới tải được lịch sử A/B trong database nhưng giao diện không lộ hội thoại không thuộc nó.

## Nhóm, failover và partition

- [ ] Chờ Diagnostics thấy 3 Voters và Joint=0 trước thử tắt leader.
- [ ] Chủ nhóm tạo/đổi tên/ảnh, thêm/xóa người, chuyển quyền; thành viên không có quyền cấu trúc.
- [ ] Đóng leader, chờ khoảng timeout 15 giây cộng probe/discovery/jitter; hai máy còn lại có leader mới.
- [ ] Mở lại leader cũ; cùng leader/term và nhóm, không ghi đè control log đã commit.
- [ ] Cô lập một voter khỏi hai voter còn lại. Phía một voter không đổi nhóm được; phía hai voter vẫn đổi nhóm.
- [ ] Hai phía vẫn gửi chat local; nối lại và đối chiếu MessageIds/nội dung.
- [ ] Thử cụm hai voter: một máy offline thì thao tác nhóm bị chặn đúng quorum.

## Tệp và giao diện

- [ ] Kéo nhiều tệp Explorer vào ChatView/ChatInput; có tên, size, preview và remove; chưa bấm Gửi thì chưa phát metadata.
- [ ] Tải ảnh/tài liệu từ A sang B; xem progress, hủy giữa chừng và tải lại.
- [ ] B đã tải có thể phục vụ cho C khi A offline.
- [ ] Không còn nguồn online thì hiện lỗi rõ, metadata vẫn còn.
- [ ] Đối chiếu SHA256 với tệp gốc; thử nguồn bị hỏng và đường truyền ngắt.
- [ ] Resize cửa sổ, DPI 100/125/150%, tên/tin dài, nhiều tệp; không mất nút gửi.
- [ ] Kiểm tra ảnh lớn và tệp lớn theo kích thước thực tế của đơn vị.

## Vận hành

- [ ] Join code sai/cluster khác không đồng bộ.
- [ ] Chẩn đoán/copy diagnostics không lộ secret, mã mời hoặc nội dung chat.
- [ ] Đóng ứng dụng dừng socket/background và có thể mở lại cùng cổng.
- [ ] Xác nhận phạm vi tin cậy: mọi thành viên cluster có bản sao lịch sử; chưa có E2EE hoặc mã hóa database.
- [ ] Có quy trình sao lưu, bảo vệ Windows account và xử lý máy mất vĩnh viễn. Bản hiện tại không tự loại voter offline.
