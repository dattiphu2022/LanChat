# Hướng dẫn sử dụng LanChat

Trong LanChat, mở **Giới thiệu → Hướng dẫn sử dụng** để đọc tài liệu này khi không có Internet.

## Cài đặt và kết nối lần đầu

1. Từ trang GitHub Releases của LanChat, tải và chạy **LanChat-Setup-Windows-x64.exe** trên Windows 10 hoặc Windows 11 x64.
2. Mở **Cài đặt**, sửa **Tên** và **Đơn vị**. Tên hiển thị theo thứ tự đơn vị rồi đến tên người dùng.
3. Nếu đã được cấp mã, mở **Mạng chat**, dán mã vào ô **Mã tham gia**, rồi bấm **Thêm mã**. Có thể dán nhiều mã, mỗi mã một dòng.
4. Nếu bắt đầu mạng mới, chọn **Mạng chat → Tạo mạng**. Chọn mạng vừa tạo, mở **Chi tiết → Hiện mã mời tham gia**, sao chép mã và gửi riêng cho những người được phép tham gia.
5. Các máy dùng cùng mã và có đường kết nối đến nhau trong LAN sẽ xuất hiện trong **Danh bạ**. Chọn **Quét tất cả** khi cần tìm lại máy.

Mỗi tab tương ứng với một mạng. Khi gỡ mạng rồi nhập lại đúng mã, LanChat có thể mở lại dữ liệu đã lưu trên máy đó. Bản cài nội bộ có thể được cấp sẵn một mạng mặc định.

## Nhắn tin riêng và theo nhóm

1. Chọn người dùng trong **Danh bạ** hoặc mở tin nhắn từ danh sách hội thoại. Dùng **Ctrl+K** để tới ô tìm hội thoại.
2. Nhập tin, nhấn **Enter** để gửi hoặc **Shift+Enter** để xuống dòng. Tin được lưu trên máy và sẽ tiếp tục gửi khi kết nối trở lại.
3. Để tạo nhóm chat, mở **Nhóm → Nhóm mới**, tích chọn thành viên rồi bấm **Tạo nhóm**.
4. Trong **Nhóm**, chọn nhóm để xem dấu tích cạnh từng thành viên ở cột **Thành viên nhóm**. Bạn cũng có thể mở **Tin nhắn → chọn nhóm → Thông tin → Thành viên**. Ở cả hai nơi, chủ nhóm có thể tích để thêm, bỏ tích để bớt rồi bấm **Lưu thành viên**. Thay đổi thành viên cần đủ số máy điều phối của mạng.

Khi mạng thiếu phiếu và chỉ hai máy kết nối được với nhau, LanChat chọn một máy điều phối. Nếu cả hai máy chạy v1.2.3 trở lên, bạn có thể tạo nhóm mới cho hai người và chat ngay. Việc sửa nhóm đã có vẫn cần đủ phiếu. Khi đủ phiếu và máy tạo nhóm đang kết nối, nhóm mới được ghi vào nhật ký chung để các máy v1.2.2 nhận được.

Từ v1.2.5, các máy thành viên tự đồng bộ lịch sử nhóm sau khi kết nối. Chỉ cần một máy thành viên còn giữ và đọc được lịch sử đang trực tuyến, các thành viên hiện tại có thể nhận lại tin cũ, kể cả khi người gửi gốc đã tắt máy. Danh sách thành viên cần được đồng bộ trước. Hãy cập nhật các máy trong nhóm lên v1.2.5 để nhận đầy đủ lịch sử. Tệp và thư mục đính kèm tự tải về khi còn máy giữ bản gốc đang trực tuyến.

Nhấp phải hội thoại để ghim, tắt thông báo hoặc **Xóa hội thoại phía tôi**. Thay đổi này áp dụng trên máy của bạn. Bấm thông báo Windows để mở hội thoại tương ứng.

## Trả lời và tìm tin cũ

- Bấm biểu tượng dấu trích dẫn trên tin nhắn để trả lời. Bấm biểu tượng trái tim để chọn cảm xúc. Menu chuột phải có thêm các thao tác như sao chép, chuyển tiếp, sửa và thu hồi.
- Khi chọn **Chuyển tiếp** cho tin có ảnh, tệp hoặc thư mục, LanChat chuẩn bị bản gốc trên máy trước khi gửi. Thư mục được kiểm tra cả các tệp bên trong. Có thể bấm **Hủy** nếu quá trình tải mất thời gian. Nội dung chuyển tiếp dùng bản gốc đã nhận, kể cả khi bạn đã chỉnh sửa bản mở trên máy.
- Bấm khung trích dẫn trong tin trả lời để tới tin gốc. Có thể chọn khung bằng bàn phím rồi nhấn **Enter**. Với tin gốc đã thu hồi hoặc bị xóa phía bạn, khung báo trạng thái của tin.
- Bấm **Quay về tin nhắn mới** để trở lại cuối hội thoại. Bản nháp được giữ khi bạn xem tin cũ.
- Dùng **Tìm tin** hoặc **Ctrl+F** trong hội thoại. Chọn một kết quả để mở vị trí của tin trong lịch sử.

## Gửi ảnh, tệp và thư mục

- Chọn **Đính kèm → Thêm tệp** hoặc **Thêm thư mục**. Hộp thoại chọn thư mục của Windows cho phép nhập đường dẫn. Bạn cũng có thể kéo ảnh, tệp hoặc thư mục từ Explorer vào ô soạn tin.
- Chọn **Dán ảnh** để lấy ảnh trong bảng nhớ tạm. **Ctrl+V** dán ảnh khi bảng nhớ tạm chỉ có ảnh. Khi có cả chữ và ảnh, **Ctrl+V** dán chữ.
- Chọn **Chụp vùng** để tạm ẩn LanChat. Kéo chuột chọn vùng, rồi nhả chuột để chụp. Nhấn **Esc** để hủy. LanChat hiện lại sau cả hai thao tác.
- Ảnh dán hoặc chụp xuất hiện dưới dạng PNG trong bản nháp. Kiểm tra bản xem trước, gỡ tệp nếu cần, rồi bấm **Gửi**. Mỗi tin có thể đính kèm tối đa 20 mục.
- Khi gửi hoặc nhận tin, LanChat tự tải ảnh, tệp và toàn bộ nội dung thư mục về nền, kể cả khi bạn đang xem hội thoại khác. Bấm tên tệp hoặc thư mục để mở bản đã chuẩn bị trên máy. Nếu máy giữ tệp đang ngoại tuyến, ứng dụng tự thử lại và tiếp tục kiểm tra tệp còn thiếu khi mở lại LanChat.
- Bấm ảnh trong tin nhắn để xem lớn. Dùng phím mũi tên để chuyển ảnh, cuộn chuột để phóng to và nhấn **Esc** để đóng.

## Cập nhật và thoát ứng dụng

1. Mở **Giới thiệu → Kiểm tra cập nhật**. Nếu có bản mới hơn, đóng cửa sổ Giới thiệu và chọn **Tải và cập nhật** trên cửa sổ chính.
2. Khi tải hoàn tất, LanChat kiểm tra kích thước và SHA-256. Hoàn tất tác vụ hoặc chọn xử lý bản nháp theo lời nhắc để tiếp tục cài.
3. Mở lại **Giới thiệu** sau khi cài để đối chiếu phiên bản. Có thể cài thủ công EXE từ GitHub Releases bằng cùng tài khoản Windows đang dùng LanChat.

Kiểm tra cập nhật cần Internet. Nhắn tin và truyền tệp giữa những máy đã kết nối sử dụng mạng nội bộ. Nút **X** hoặc **Thu nhỏ** đưa ứng dụng xuống khay hệ thống. Nhấp đúp biểu tượng để mở lại hoặc nhấp phải rồi chọn **Thoát LanChat**.

## Khi không thấy máy hoặc cần sao lưu

- Kiểm tra các máy đang chọn đúng tab mạng và có cùng mã. Máy tạo mạng nên mở LanChat trong lần kết nối đầu tiên.
- Bấm **Quét tất cả**. Nếu vẫn chưa tìm thấy, kiểm tra các máy cùng mạng LAN, quyền tường lửa trên mạng tin cậy và cài đặt cô lập thiết bị của Wi-Fi.
- Khi sao lưu, chọn **Thoát LanChat** từ khay hệ thống rồi chép cả thư mục `%LOCALAPPDATA%\LanChat\Data` vào vị trí an toàn. Bản sao lưu có thể chứa dữ liệu chat đã giải mã. Khóa thiết bị gắn với tài khoản Windows hiện tại.
