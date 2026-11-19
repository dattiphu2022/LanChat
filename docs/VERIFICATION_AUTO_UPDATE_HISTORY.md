# Kiểm chứng v1.2.9: cập nhật tự động và bộ nhớ đệm hội thoại

- `dotnet test tests/LanChat.Tests/LanChat.Tests.csproj -c Release --no-restore`: 200/200 kiểm thử đạt trên Windows, .NET Framework 4.8.
- Kiểm thử giao diện xác nhận bản cập nhật tự tải sau khi kiểm tra, đóng/mở Giới thiệu vẫn giữ tiến độ; hủy/lỗi tải có thể thử lại; cài đặt vẫn đợi thao tác người dùng.
- Badge trên nút Giới thiệu và nút Cài đặt ngay được kiểm tra ở 96, 144 và 192 DPI, kích thước cửa sổ tối thiểu. Các nút không bị cắt.
- Kiểm thử lịch sử xác nhận chuyển A → B → A tái sử dụng cùng dữ liệu khi revision không đổi; sau khi có tin mới ở A trong lúc B đang mở, quay lại A nhận đủ tin; xóa hội thoại xóa nội dung đang hiển thị.
- Kiểm thử tái sử dụng control xác nhận các hàng tin nhắn không bị hủy/tạo lại hoặc trùng khi chuyển nhiều lần giữa hội thoại dài và hội thoại trống.
- Bộ nhớ đệm riêng cho từng mạng, tối đa 12 hội thoại và 400 tin mỗi hội thoại. Giữ tối đa 80 control dự phòng; thay đổi revision làm mới lịch sử. Bộ nhớ đệm chỉ tồn tại trong phiên chạy.
- Publish công khai kiểm tra không chứa mã mạng nội bộ. EXE, bộ gỡ cài đặt và bộ cài được ký bằng chứng thư phát triển LanChat, xác minh chữ ký và tính toàn vẹn PE thành công.

Kết quả TRX và ảnh giao diện nằm trong `artifacts/v1.2.9-tests-final` và `artifacts/v1.2.9-ui`; bộ cài nằm trong `artifacts/v1.2.9-release/installer`.
