# Kiểm chứng v1.2.7: chọn nhiều tin, QR và ký số

Ngày kiểm tra: 19/11/2026. Không thay giao thức v4 hoặc cấu trúc database. Không chạy bộ cài trong kiểm thử.

## QR và giao diện

- Sau sửa cuối về chiều cao tối thiểu theo font/nút thực tế, 16 ca QR/ký số/ô soạn đã qua.
- 12 ca QR/ký số mới đã qua, gồm: ghép theo thời gian; giữ khoảng trắng, CRLF, dòng trống, dấu kết hợp và emoji; phân cách đúng `LF LF`; không thêm metadata.
- Kiểm tra biên 499/500/501 byte, 500 byte có tiếng Việt/emoji, dung lượng dấu phân cách, không cắt nội dung, từ chối Unicode không hợp lệ và QR không vượt Version 18.
- Xuất PNG rồi giải mã bằng ZXing để đối chiếu nguyên văn và mức sửa lỗi M. Thư viện tạo QR QRCoder chạy cục bộ; ZXing chỉ thuộc project kiểm thử, không đi kèm ứng dụng.
- Kiểm tra chọn từng tin/tất cả/bỏ chọn/hủy, giữ bản nháp và tái sử dụng control, cập nhật tin đã sửa, loại tin thu hồi/bị xóa, hủy lựa chọn khi đổi hội thoại. Bố cục nút đã qua mô phỏng DPI 100%, 150%, 200% ở cửa sổ hẹp.
- Chọn tất cả áp dụng phần lịch sử đang hiển thị; có hướng dẫn tải thêm tin cũ. Bản xem trước và nút xuất dùng cùng một snapshot văn bản, không lấy ngược văn bản đã chuẩn hóa từ TextBox để mã hóa.

## Build và hồi quy

- Debug/Release build thành công, 0 cảnh báo và 0 lỗi.
- Lượt hồi quy Release: **180/182 qua**. `NominationAndWithdrawalKeepVotesAndReplicateQueuedMessages` timeout trong lượt chung, **đã qua khi chạy riêng**.
- `OneRemainingHolderAutomaticallySharesAllPagesWithCurrentGroupMembers` vẫn timeout khi chạy riêng với 215 tin. Lỗi này từng tái hiện trên bản nền v1.2.5/v1.2.6 trước các thay đổi UI/ký số; không sửa thuật toán nhóm trong bản này. Không tuyên bố toàn bộ hồi quy xanh.
- Hai tiến trình v1.2.7, dữ liệu riêng và DPI thực 125%: chat riêng/nhóm, sáu tệp/ảnh mỗi máy, lưu bản chỉnh sửa và tải cập nhật LAN bằng gói giả lập đều qua. [Kết quả](verification/qr-signing/two-processes.json).
- Báo cáo TRX nằm trong `.codex-temp/test-results/release-v1.2.7*.trx`. Kết quả trước đó của cập nhật LAN, nguồn offline và tương thích v1.2.6 xem [kiểm chứng cập nhật LAN](VERIFICATION_LAN_UPDATE.md).

## Ký số và bản sao lưu

- Subject: **CN=LanChat Development**; RSA 3072-bit, SHA-256, Code Signing, Exportable, CurrentUser/My; hết hạn 19/11/2029.
- Thumbprint dùng chung cho EXE ứng dụng, bộ cài và phần gỡ cài đặt Inno Setup: **9CC153E400BB35AAD399B6BA9956DC92EF0DC377**.
- Công cụ tìm SignTool x64 trong Windows SDK. Sau ký, kiểm tra CMS/SHA-256, thumbprint và WinVerifyTrust. Ca sửa byte PE và ca khác thumbprint đều bị từ chối.
- Chữ ký phát triển tự ký có kết quả Windows **0x800B0109 — untrusted root**. Không thêm chứng thư vào Root/Trusted Publishers và không thay đổi chính sách Windows hay cấu hình Kaspersky.
- Tệp phát hành: `artifacts/v1.2.7-release/publish/LanChat.WinForms.exe` và `artifacts/v1.2.7-release/installer/LanChat-Setup-Windows-x64.exe`. Inno Setup chạy bước ký trước khi trả bộ cài hoàn chỉnh; kiểm tra lại trước khi upload release.
- PFX/CER/README được sao lưu tại Desktop thực tế qua Known Folder API: `C:\Users\Admin\OneDrive\Desktop\LanChat_CodeSigning_Backup\`. PFX dùng AES-256/SHA-256 với mật khẩu nhập trực tiếp qua PasswordBox. Đã đọc lại PFX bằng mật khẩu và ký/xác minh thử thách trong bộ nhớ để chứng minh khóa riêng khớp chứng thư; không lưu hay in mật khẩu.
- Khi script khởi tạo không còn khả dụng sau cảnh báo phần mềm bảo vệ, không tạo lại hoặc chạy lại script đó. Các bước ký/đóng gói dùng công cụ build và SignTool; bản sao lưu đã hoàn tất vẫn được giữ nguyên. PFX/khóa riêng/mật khẩu không vào Git hoặc assets phát hành.

[Cấu hình build và khôi phục chứng thư](CODE_SIGNING.md).

## Giới hạn kiểm chứng

DPI 100/150/200% được mô phỏng bằng font, kích thước và DPI control; màn hình thực của máy kiểm tra là 125%. Cần nghiệm thu thêm bàn phím/IME và chuyển màn hình vật lý trong môi trường sử dụng. Hộp thoại lưu giữ cấu hình Explorer của người dùng. Không chạy EXE bộ cài thật và không thay đổi chính sách chống virus để kiểm thử.
