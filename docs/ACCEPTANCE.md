# Nghiệm thu LanChat trên mạng thật

Bản đã được kiểm thử tự động và chạy hai tiến trình trên cùng Windows. Các mục dưới đây cần làm trên các PC tại nơi sử dụng; không đánh dấu đạt thay cho kiểm tra thực tế.

## Chuẩn bị

- [ ] Ít nhất 3 PC Windows 11 x64 dùng cùng ZIP. Có một máy Windows 11 sạch, tài khoản thường, chưa cài .NET Desktop Runtime.
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
- [ ] Chủ nhóm tạo/đổi tên, thêm/xóa người, chuyển quyền; thành viên không có quyền cấu trúc.
- [ ] Dừng đột ngột máy trung tâm sau khi ba voter ổn định. Đo từ heartbeat hợp lệ cuối tới PreVote (5 giây), trung tâm sẵn sàng (mục tiêu ≤ 7 giây) và bắt đầu đồng bộ (mục tiêu ≤ 1 giây sau xác nhận). Lặp 30 lượt trên LAN /24 và ghi p95.
- [ ] Đề cử một voter đã bắt kịp log, thử ứng cử, rút lui và mở lại để kiểm tra lựa chọn đã lưu. Tắt quyền làm trung tâm trên mọi máy, kiểm tra tin nhắn vẫn đồng bộ và thao tác đổi cấu trúc nhóm tạm dừng.
- [ ] Đo p95 thời gian thấy peer đầu tiên qua UDP (mục tiêu ≤ 1 giây) và qua TCP với UDP bị chặn (mục tiêu ≤ 3 giây).
- [ ] Mở lại leader cũ; cùng leader/term và nhóm, không ghi đè control log đã commit.
- [ ] Cô lập một voter khỏi hai voter còn lại. Phía một voter không đổi nhóm được; phía hai voter vẫn đổi nhóm.
- [ ] Hai phía vẫn gửi chat local; nối lại và đối chiếu MessageIds/nội dung.
- [ ] Thử cụm hai voter: một máy offline thì thao tác nhóm bị chặn đúng quorum.

## Tệp và giao diện

- [ ] Kéo nhiều tệp Explorer vào ChatView/ChatInput; có tên, size, preview và remove; chưa bấm Gửi thì chưa phát metadata.
- [ ] Tải ảnh/tài liệu từ A sang B; xem progress, hủy giữa chừng và tải lại.
- [ ] Gửi nhiều ảnh kèm chú thích nhiều dòng: ảnh hiện ngay trong chat của A/B, ảnh từ xa tự lưu local khi xuất hiện trong vùng xem.
- [ ] Bấm ảnh mở đúng ảnh đã chọn; caption giữ xuống dòng, tên người gửi và số thứ tự chính xác. Chuyển bằng thumbnail/Trước/Sau/phím mũi tên qua các lần gửi khác nhau.
- [ ] Zoom bằng cuộn chuột/nút, kéo ảnh đã phóng to, Vừa khung/100%, Esc đóng; đổi nhanh ảnh và đóng khi đang tải không bị ảnh cũ ghi đè hoặc lỗi ứng dụng.
- [ ] Thử ảnh gửi không kèm chữ, JPEG xoay EXIF, GIF, ảnh hỏng và ảnh vượt giới hạn; ảnh không tải được có thể thử lại, tệp lớn có thể mở bằng ứng dụng ngoài.
- [ ] Bấm tên tệp ngay trong tin nhắn: local mở bằng ứng dụng mặc định; remote tự tải, kiểm tra hash và mở, không cần bấm Tải.
- [ ] Bấm lại khi máy gửi offline vẫn mở bản local; chỉnh sửa bản mở không làm thay đổi blob gốc phục vụ P2P.
- [ ] B đã tải có thể phục vụ cho C khi A offline.
- [ ] Không còn nguồn online thì hiện lỗi rõ, metadata vẫn còn.
- [ ] Đối chiếu SHA256 với tệp gốc; thử nguồn bị hỏng và đường truyền ngắt.
- [ ] Resize cửa sổ, DPI 100/125/150%, tên/tin dài, nhiều tệp. Kiểm tra Ctrl+K, Ctrl+F, Shift+Enter, bản nháp theo hội thoại và tải tin cũ giữ vị trí cuộn.
- [ ] Kiểm tra ảnh lớn và tệp lớn theo kích thước thực tế của đơn vị.

## Vận hành

- [ ] Mở lần đầu vào Mạng mặc định mà không qua form nhập mã. Máy mới được cấp cùng mã mặc định có thể tìm máy tạo và trao đổi tin nhắn.
- [ ] Thêm mã riêng đầu tiên, thử cả giữ và gỡ mạng mặc định. Mở lại ứng dụng và thêm mã tiếp theo để kiểm tra lựa chọn được nhớ. Xác minh tin nhắn và tệp không xuất hiện ngoài mạng của mã đã dùng.

- [ ] Cài đặt chỉ có Tên, Đơn vị và Khởi động cùng hệ thống. Đăng xuất/đăng nhập để xác nhận Windows mở ứng dụng khi bật.
- [ ] Mở lần hai, kể cả dùng thư mục dữ liệu khác, chỉ có một cửa sổ LanChat hoạt động.
- [ ] Dán hai mã khác nhau, kiểm tra cả hai tab tìm máy cùng lúc và tin nhắn không xuất hiện ở mạng kia. Gỡ một mạng, xác minh mạng còn lại tiếp tục kết nối. Nhập lại mã để mở lại lịch sử.

- [ ] Join code sai/cluster khác không đồng bộ.
- [ ] Chẩn đoán/copy diagnostics không lộ secret, mã mời hoặc nội dung chat.
- [ ] Đóng ứng dụng dừng socket/background và có thể mở lại cùng cổng.
- [ ] Xác nhận phạm vi tin cậy: mọi thành viên cluster có bản sao lịch sử; chưa có E2EE hoặc mã hóa database.
- [ ] Có quy trình sao lưu, bảo vệ Windows account và xử lý máy mất vĩnh viễn. Bản hiện tại không tự loại voter offline.
