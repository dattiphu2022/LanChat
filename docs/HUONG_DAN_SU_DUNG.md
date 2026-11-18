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
- Khi **Sửa** tin nhắn, bản nháp trước đó được giữ, gồm cả ảnh, tệp và tin đang trả lời. Hủy sửa hoặc lưu sửa thành công sẽ khôi phục bản nháp đó. Nếu gửi lỗi, nội dung vẫn còn để thử lại.
- Dùng **Tìm tin** hoặc **Ctrl+F** trong hội thoại. Chọn một kết quả để mở vị trí của tin trong lịch sử.

## Gửi ảnh, tệp và thư mục

- Chọn **Đính kèm → Thêm tệp** hoặc **Thêm thư mục**. Hộp thoại chọn thư mục của Windows cho phép nhập đường dẫn. Bạn cũng có thể kéo ảnh, tệp hoặc thư mục từ Explorer vào ô soạn tin.
- Chọn **Dán ảnh** để lấy ảnh trong bảng nhớ tạm. **Ctrl+V** dán ảnh khi bảng nhớ tạm chỉ có ảnh. Khi có cả chữ và ảnh, **Ctrl+V** dán chữ.
- Chọn **Chụp vùng** để tạm ẩn LanChat. Kéo chuột chọn vùng, rồi nhả chuột để chụp. Nhấn **Esc** để hủy. LanChat hiện lại sau cả hai thao tác.
- Ảnh dán hoặc chụp xuất hiện dưới dạng PNG trong bản nháp. Kiểm tra bản xem trước, gỡ tệp nếu cần, rồi bấm **Gửi**. Mỗi tin có thể đính kèm tối đa 20 mục.
- Khi gửi hoặc nhận tin, LanChat tự tải ảnh, tệp và toàn bộ nội dung thư mục về nền, kể cả khi bạn đang xem hội thoại khác. Bấm tên tệp hoặc thư mục để mở bản đã chuẩn bị trên máy. Nếu máy giữ tệp đang ngoại tuyến, ứng dụng tự thử lại và tiếp tục kiểm tra tệp còn thiếu khi mở lại LanChat.
- Bấm ảnh trong tin nhắn để xem lớn. Dùng phím mũi tên để chuyển ảnh, cuộn chuột để phóng to và nhấn **Esc** để đóng.

## Lưu đính kèm và mở lại bản đã sửa

- **Mở** cạnh mỗi tệp, thư mục hoặc ảnh dùng ứng dụng mặc định của Windows; thư mục mở trong Explorer. Bấm trực tiếp tên tệp/thư mục vẫn mở như trước, còn bấm ảnh vẫn vào khung xem LanChat với ảnh gốc của tin nhắn.
- **Lưu về máy** lưu bản đang làm việc, gồm các chỉnh sửa bằng Word, Excel hoặc trình sửa ảnh. Tệp dùng hộp thoại chọn tên và nơi lưu. Thư mục dùng hộp thoại chọn nơi lưu, rồi giữ nguyên thư mục con và nội dung đang làm việc. Dòng trạng thái và tooltip hiển thị vị trí đã lưu.
- Lần lưu đầu chuyển bản làm việc của LanChat ra vị trí đã chọn. Nếu lưu tiếp sang nơi khác, ứng dụng tạo bản sao mới và giữ bản đã lưu trước. Từ đó, **Mở** luôn dùng vị trí mới nhất, kể cả khi khởi động lại. Đóng tệp trong ứng dụng khác nếu Windows báo tệp đang bận, rồi thử lưu lại.
- Với tin có nhiều đính kèm, mở menu **… → Lưu tất cả đính kèm của tin…**. Chỉ chọn thư mục một lần cho cả ảnh, tệp và thư mục của tin đó. Trong khung xem ảnh, menu **… → Lưu tất cả ảnh của tin…** chỉ lưu ảnh cùng tin với ảnh đang chọn. Các thao tác tương ứng cũng có ở bảng **Thông tin hội thoại** và cửa sổ xem tệp.
- Lưu hàng loạt tự thêm `(2)`, `(3)` nếu trùng tên; nếu đích đúng là bản đang dùng thì giữ nguyên. Có thể **Hủy** trong cửa sổ tiến độ. Những mục đã lưu được giữ; lỗi một mục không ngăn các mục còn lại. Cửa sổ kết quả ghi rõ vị trí hoặc lỗi của từng mục. Lưu không tự mở tệp.
- Nếu bản đã lưu bị mất hoặc ổ đĩa chưa kết nối, chọn **Tìm lại tệp/thư mục…**, **Tạo lại từ bản gốc** hoặc **Hủy**. Tìm lại giữ các chỉnh sửa của bản bạn chọn. Tạo lại chỉ lấy nội dung gốc trong tin nhắn và không thể phục hồi chỉnh sửa cục bộ đã mất. Ứng dụng giữ vị trí cũ cho đến khi bạn chọn cách xử lý.
- Bản gốc dùng để đồng bộ, chuyển tiếp và xem ảnh trong chat được giữ riêng. Muốn chia sẻ nội dung đã sửa, đính kèm lại tệp thành tin mới. Khi sao lưu dữ liệu, sao lưu thêm các bản làm việc đã lưu bên ngoài thư mục LanChat.

## Cập nhật và thoát ứng dụng

1. Mở **Giới thiệu → Kiểm tra cập nhật**. Nếu có bản mới hơn, chọn **Tải bản cập nhật** ngay tại đây. Kết quả, tiến độ và các nút cập nhật luôn đồng bộ với cửa sổ chính.
2. Bạn có thể đóng Giới thiệu để tiếp tục chat trong lúc tải nền, hoặc chọn **Hủy tải**. Mở lại Giới thiệu sẽ thấy tiến độ hiện tại. Nếu tải lỗi hoặc đã hủy, có thể tải lại.
3. Tải xong, LanChat kiểm tra kích thước và SHA-256 rồi chờ bạn chọn **Cài đặt ngay**. Hoàn tất hoặc hủy tác vụ đang chạy, xử lý bản nháp theo lời nhắc để tiếp tục. Ứng dụng xác minh lại bộ cài trước khi thoát để cài đặt.
4. Mở lại **Giới thiệu** sau khi cài để đối chiếu phiên bản. Có thể cài thủ công EXE từ GitHub Releases bằng cùng tài khoản Windows đang dùng LanChat.

Kiểm tra cập nhật cần Internet. Nhắn tin và truyền tệp giữa những máy đã kết nối sử dụng mạng nội bộ. Nút **X** hoặc **Thu nhỏ** đưa ứng dụng xuống khay hệ thống. Nhấp đúp biểu tượng để mở lại hoặc nhấp phải rồi chọn **Thoát LanChat**.

Các hộp thoại có nút **Hủy/Đóng** và hỗ trợ **Esc**. Khi có tác vụ ghi đang chạy, chờ hoàn tất trước khi đóng. Ứng dụng hỏi trước khi bỏ nội dung chưa lưu; bản nháp chỉ được giữ trong phiên làm việc hiện tại. Nếu bấm thông báo tin nhắn lúc đang mở hộp thoại, LanChat đưa hộp thoại lên trước và chuyển tới tin nhắn sau khi bạn đóng hộp thoại đó.

## Khi không thấy máy hoặc cần sao lưu

- Kiểm tra các máy đang chọn đúng tab mạng và có cùng mã. Máy tạo mạng nên mở LanChat trong lần kết nối đầu tiên.
- Bấm **Quét tất cả**. Nếu vẫn chưa tìm thấy, kiểm tra các máy cùng mạng LAN, quyền tường lửa trên mạng tin cậy và cài đặt cô lập thiết bị của Wi-Fi.
- Khi sao lưu, chọn **Thoát LanChat** từ khay hệ thống rồi chép cả thư mục `%LOCALAPPDATA%\LanChat\Data` vào vị trí an toàn. Bản sao lưu có thể chứa dữ liệu chat đã giải mã. Khóa thiết bị gắn với tài khoản Windows hiện tại.
