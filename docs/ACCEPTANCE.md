# Nghiệm thu LanChat trên mạng thật

Danh sách dưới đây dùng để nghiệm thu v1.2.5 trên các PC tại nơi sử dụng. Xem thao tác của người dùng và kết quả đã ghi nhận trong [Hướng dẫn sử dụng](HUONG_DAN_SU_DUNG.md) và [Kết quả kiểm chứng](VERIFICATION.md).

## Chuẩn bị

- [ ] Ít nhất ba PC Windows x64 dùng cùng bộ cài `LanChat-Setup-Windows-x64.exe`. Có một máy Windows 11 sạch với tài khoản thường để thử cài mới.
- [ ] Mỗi PC có thư mục dữ liệu local riêng; không clone database/NodeId.
- [ ] Sao lưu dữ liệu cũ khi app đóng trước nâng cấp.
- [ ] Cùng mã mời hoặc mã nâng cấp từ một máy duy nhất.
- [ ] UDP 54545 và TCP 54546 được phép trên LAN tin cậy; kiểm tra Wi-Fi isolation/VLAN.
- [ ] Thử hai PC lệch giờ hơn 120 giây. Khi nhận gói dò ngoài cửa sổ giờ, LanChat xác minh máy kia qua TCP/TLS trước khi đưa vào Danh bạ. Hai máy vẫn gửi và nhận tin sau khi xác minh.

## Chat và dữ liệu

- [ ] A/B/C tự thấy nhau, IP đổi không làm mất identity/danh bạ.
- [ ] Gửi text/Unicode/nhiều dòng; tin hiện ngay, rồi đồng bộ/đã nhận/đã đọc.
- [ ] Nhấp phải để reply/edit/recall/forward/reaction/delete local; người khác không sửa/thu hồi tin của mình.
- [ ] Thu hồi biến mất khỏi tìm kiếm; xóa phía tôi không xóa ở peer.
- [ ] Tìm không dấu trong nội dung tiếng Việt; mở đúng vị trí tin cũ.
- [ ] Bấm khung trích dẫn của tin đang hiển thị và tin ngoài trang hiện tại. Thử tin gốc đã thu hồi hoặc xóa phía tôi. Bấm **Quay về tin nhắn mới** và đối chiếu bản nháp.
- [ ] Ghim, im lặng và thông báo khi cửa sổ không có focus hoạt động theo cài đặt.
- [ ] Tắt một node, gửi nhiều tin trên các node khác rồi bật lại; lịch sử hội tụ, không trùng.
- [ ] Chỉ giữ một máy thành viên có đủ lịch sử nhóm, tắt máy gửi gốc rồi cho các máy thành viên v1.2.5 kết nối. Đối chiếu trên 100 tin, nội dung đã sửa, thu hồi, cảm xúc và tệp đính kèm. Thêm thành viên mới rồi xác nhận nhận được tin cũ. Người ngoài nhóm và thành viên đã bị gỡ được kiểm tra quyền truy cập.
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
- [ ] Trong mạng có ít nhất bốn voter, chỉ để hai máy v1.2.3 kết nối. Hai máy hiện cùng trung tâm, tạo và chat trong nhóm mới. Nút sửa nhóm đã có bị khóa. Bật thêm máy để đủ phiếu rồi đối chiếu nhóm mới trên máy v1.2.2.

## Tệp và giao diện

- [ ] Kéo nhiều tệp Explorer vào ChatView/ChatInput; có tên, size, preview và remove; chưa bấm Gửi thì chưa phát metadata.
- [ ] Chọn thư mục bằng cách nhập đường dẫn. Dán bitmap hoặc PNG vào bản nháp, gỡ ảnh trước khi gửi, rồi thử gửi sang máy khác.
- [ ] Bấm **Chụp vùng** khi một cửa sổ khác nằm phía sau LanChat. Xác minh ảnh lấy đúng vùng, LanChat hiện lại sau khi chụp hoặc hủy bằng **Esc**. Lặp lại ở hai mức DPI hoặc trên màn hình phụ.
- [ ] Tải ảnh/tài liệu từ A sang B; xem progress, hủy giữa chừng và tải lại.
- [ ] Gửi nhiều ảnh kèm chú thích nhiều dòng: ảnh hiện ngay trong chat của A/B, ảnh từ xa tự lưu local khi xuất hiện trong vùng xem.
- [ ] Bấm ảnh mở đúng ảnh đã chọn; caption giữ xuống dòng, tên người gửi và số thứ tự chính xác. Chuyển bằng thumbnail/Trước/Sau/phím mũi tên qua các lần gửi khác nhau.
- [ ] Zoom bằng cuộn chuột/nút, kéo ảnh đã phóng to, Vừa khung/100%, Esc đóng; đổi nhanh ảnh và đóng khi đang tải không bị ảnh cũ ghi đè hoặc lỗi ứng dụng.
- [ ] Thử ảnh gửi không kèm chữ, JPEG xoay EXIF, GIF, ảnh hỏng và ảnh vượt giới hạn; ảnh không tải được có thể thử lại, tệp lớn có thể mở bằng ứng dụng ngoài.
- [ ] Gửi ảnh, tệp và thư mục rồi chuyển sang hội thoại khác. Chờ tải nền hoàn tất, tắt máy gửi và bấm mở trên máy nhận. Kiểm tra đủ tệp, thư mục con và thư mục rỗng. Mở lại LanChat để xác minh các tệp còn thiếu tiếp tục được tải.
- [ ] Bấm lại khi máy gửi offline vẫn mở bản local; chỉnh sửa bản mở không làm thay đổi blob gốc phục vụ P2P.
- [ ] B đã tải có thể phục vụ cho C khi A offline.
- [ ] Chuyển tiếp từ B sang C một tệp và một thư mục có các tệp trùng nội dung. Thử lúc B chưa tải xong và lúc A đã offline sau khi B tải đủ. Sửa bản mở trên B rồi đối chiếu C nhận đúng bản gốc. Hủy trong lúc chuẩn bị và kiểm tra chưa có tin chuyển tiếp được gửi.
- [ ] Không còn nguồn online thì hiện lỗi rõ, metadata vẫn còn.
- [ ] Đối chiếu SHA256 với tệp gốc; thử nguồn bị hỏng và đường truyền ngắt.
- [ ] Resize cửa sổ, DPI 100/125/150%, tên/tin dài, nhiều tệp. Kiểm tra Ctrl+K, Ctrl+F, Shift+Enter, bản nháp theo hội thoại và tải tin cũ giữ vị trí cuộn.
- [ ] Kiểm tra ảnh lớn và tệp lớn theo kích thước thực tế của đơn vị.

## Vận hành

- [ ] Cài mới bản công khai và dùng **Mạng chat → Tạo mạng** hoặc **Thêm mã**. Với bản nội bộ có cấu hình cấp sẵn, thử giữ hoặc gỡ mạng mặc định khi thêm mạng riêng đầu tiên.
- [ ] Mở **Giới thiệu → Hướng dẫn sử dụng** khi máy mất Internet. Đổi chủ đề, cuộn và đóng bằng bàn phím ở mức 100%, 125% và trên cửa sổ hẹp.
- [ ] Từ bản cũ có chức năng cập nhật, dùng **Kiểm tra cập nhật**, tải EXE và thử nâng cấp tại chỗ bằng cùng tài khoản Windows. Đối chiếu hồ sơ, danh sách mạng và lịch sử. Thử hủy tải và hoãn cài khi còn bản nháp hoặc tác vụ đang chạy.

- [ ] Cài đặt chỉ có Tên, Đơn vị và Khởi động cùng hệ thống. Đăng xuất/đăng nhập để xác nhận Windows mở ứng dụng khi bật.
- [ ] Mở lần hai, kể cả dùng thư mục dữ liệu khác, chỉ có một cửa sổ LanChat hoạt động.
- [ ] Dán hai mã khác nhau, kiểm tra cả hai tab tìm máy cùng lúc và tin nhắn không xuất hiện ở mạng kia. Gỡ một mạng, xác minh mạng còn lại tiếp tục kết nối. Nhập lại mã để mở lại lịch sử.

- [ ] Join code sai/cluster khác không đồng bộ.
- [ ] Chẩn đoán/copy diagnostics không lộ secret, mã mời hoặc nội dung chat.
- [ ] Đóng ứng dụng dừng socket/background và có thể mở lại cùng cổng.
- [ ] Xác minh nội dung tin nhắn và khóa tệp chỉ được giải mã ở thiết bị được cấp quyền. Danh bạ, hiện diện và cấu trúc nhóm được chia sẻ trong mạng. Dữ liệu đã giải mã lưu cục bộ nên cần bảo vệ thiết bị và bản sao lưu.
- [ ] Có quy trình sao lưu, bảo vệ Windows account và xử lý máy mất vĩnh viễn. Bản hiện tại không tự loại voter offline.
