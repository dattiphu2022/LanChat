# Kiểm chứng v1.2.8: đính kèm, nhắc tên và chuyển tiếp

Ngày kiểm tra: 19/11/2026. Giữ giao thức v4 và hình dạng NodeInfoDto/EventDto đã ký; thêm migration cục bộ MessageMentions. Không chạy bộ cài trong kiểm thử.

## Hành vi và kiểm thử trọng tâm

- Đọc vị trí không đợi writer đang chuẩn bị; hai người chờ dùng chung một tác vụ. Hủy một subscriber giữ tác vụ còn người dùng; hủy subscriber cuối phải đợi writer dừng. Thử lại trong lúc drain không tham gia tác vụ đã bỏ. Kiểm thử có checkpoint trễ chủ động.
- Luồng tải phân biệt chờ, kết nối, tải, xác minh, chuẩn bị, lưu và ShellExecute. Tối đa hai nguồn chờ header; nguồn có header hợp lệ đầu tiên được chọn, nguồn dư dừng. Chờ nguồn theo dung lượng 30 giây–5 phút, body giữ idle timeout 15 giây. SHA-256 kiểm tra trong lượt giải mã/sao chép trước publish; bộ hồi quy tệp kiểm tra dữ liệu bị sửa, tệp/thư mục, hủy và tệp tạm.
- Metadata lời nhắc nằm trong bản mã. Kiểm tra tên trùng, UserId đích, UTF-16/emoji/CRLF, trim đầu văn bản, @All chốt thành viên lúc gửi, mute, sửa/thu hồi, đồng bộ lặp và khởi động lại. Sửa không phát toast lại; đánh dấu đọc xóa số @ chưa đọc.
- @All có sẵn trong tin giữ tập người nhận khi chỉ sửa chữ; @All được chọn mới giải quyết tập đích khi commit.
- UI STA kiểm tra gợi ý tên không dấu, Enter/Esc, Undo/Redo thật của TextBox, giữ metadata khi hủy sửa/gửi lỗi, tô xanh theo UserId và ánh xạ CRLF cho RichEdit. QR và sao chép tiếp tục dùng văn bản gốc.
- Chuyển tiếp nhiều tin tới người chưa từng nhắn/offline, cùng tên khác ID; xác nhận thứ tự, bản gốc không chứa sửa cục bộ, AttachmentId/FileKey riêng từng đích và bỏ metadata nhắc tên. Hủy giữ tin đã commit; thử lại UI chỉ gửi mục chưa hoàn tất. Giới hạn QR không khóa chuyển tiếp.

## Hai tiến trình và giao diện

- Hai tiến trình v1.2.8, dữ liệu riêng: chat riêng/nhóm, sáu đính kèm mỗi máy, hai bản lưu đã sửa, nhắc tên qua TCP, chuyển tiếp hai tin tới hai đích đều qua. [Báo cáo](verification/mentions-forwarding/two-processes.json).
- Hai tiến trình v1.2.8/v1.2.7: chat/tệp/nhóm và lưu bản chỉnh sửa đều qua. Máy v1.2.7 nhận và phát receipt cho tin @All có metadata mới; máy cũ vẫn đọc văn bản bình thường. [Báo cáo](verification/mentions-forwarding/interop-v1.2.7.json).
- DPI 100/150/200% được mô phỏng bằng font/kích thước/DeviceDpi trong WinForms. Nút chọn nhiều, QR, chuyển tiếp, Hủy, Gửi và hộp danh bạ vẫn truy cập được ở cửa sổ hẹp. Đối chiếu ảnh bitmap và ảnh cửa sổ thật; màn hình thực là 125%. Không đổi thiết lập DPI Windows.
- Computer Use trả lỗi tái nhận diện cửa sổ (owner mismatch) khi thử lấy cửa sổ LanChat; đã dừng nhập qua helper đó. Kiểm chứng giao diện dùng message loop STA, các control thật và ảnh DrawToBitmap do harness tạo. Chưa nghiệm thu IME bằng bàn phím vật lý hoặc chuyển màn hình DPI thực.

## Build, hồi quy và lỗi nền

- Debug build thành công; hồi quy Debug **193/193 qua**. Sau rà soát thêm @All cũ khi có thành viên mới, lượt kiểm thử trọng tâm **6/6 qua**, gồm một ca mới về đóng băng tập đích khi sửa.
- Toàn bộ **51/51 kiểm thử WinForms Release** và **6/6 ca trọng tâm Release** đã qua sau các sửa cuối về IME, bản nháp, @All và snapshot giữa các luồng.
- Lượt Release chạy song song đóng gói: 189/193 qua; các ca xác minh LAN, sai secret, bàn giao điều phối và lịch sử nhóm gặp lỗi thời hạn/đóng socket. Chạy lại tuần tự khi không build để phân biệt tải kiểm thử.
- Release tuần tự: **192/193 qua**, chỉ ca lịch sử nhóm timeout (7 phút 45 giây tổng lượt); ba lỗi kết nối/điều phối ở lượt song song không lặp lại. Lỗi lịch sử nhóm OneRemainingHolderAutomaticallySharesAllPagesWithCurrentGroupMembers đã có trên nền v1.2.5/v1.2.6 và báo cáo v1.2.7; không sửa thuật toán nhóm trong đợt này. Không suy diễn toàn bộ hồi quy xanh từ các lượt chạy riêng.
- TRX, ảnh DPI và báo cáo hai tiến trình đầy đủ nằm trong artifacts/v1.2.8-tests, artifacts/v1.2.8-two-processes và artifacts/v1.2.8-interop; không đưa database, khóa mạng hay nội dung thử vào release.

## Ký số và bộ cài

- Tái sử dụng CN=LanChat Development, thumbprint **9CC153E400BB35AAD399B6BA9956DC92EF0DC377**, CurrentUser/My; không tạo hoặc xuất lại chứng thư. EXE, bộ cài và uninstaller Inno Setup ký SHA-256 và xác minh CMS, thumbprint, PE/WinVerifyTrust.
- Chứng thư phát triển tự ký trả 0x800B0109 (root chưa được Windows tin cậy); không thêm Trusted Root/Trusted Publishers hoặc đổi Kaspersky/chính sách Windows.
- Bản sao lưu trước đó được giữ nguyên tại Desktop thật: C:\Users\Admin\OneDrive\Desktop\LanChat_CodeSigning_Backup\, đủ PFX/CER/README. Không đọc/lưu mật khẩu, không đưa PFX/khóa riêng vào Git hoặc assets. [Khôi phục chứng thư](CODE_SIGNING.md).
- Tệp công khai tại artifacts/v1.2.8-release/publish/LanChat.WinForms.exe và artifacts/v1.2.8-release/installer/LanChat-Setup-Windows-x64.exe. Pipeline xác nhận phiên bản và loại mã mạng nội bộ trước khi tạo bộ cài.
