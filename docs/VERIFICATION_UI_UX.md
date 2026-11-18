# Kiểm chứng cải thiện thao tác UI

Ngày kiểm tra: 18/11/2026.

## Các luồng đã kiểm tra

- Giới thiệu và cửa sổ chính dùng chung trạng thái cập nhật; đóng/mở Giới thiệu trong lúc tải; tải lỗi, hủy và thử lại; tái sử dụng bộ cài đã xác minh. Chỉ yêu cầu cài khi người dùng bấm Cài đặt ngay. Kiểm thử dùng HTTP giả lập và không khởi chạy bộ cài.
- Yêu cầu cài từ Giới thiệu được xử lý sau khi hộp thoại và trạng thái bận của thao tác mở Giới thiệu kết thúc.
- Tìm kiếm có kết quả về ngược thứ tự, đóng cửa sổ lúc đang đọc và mở kết quả bằng Enter.
- Giữ chữ, tệp và tin đang trả lời khi vào Sửa, nhấn Esc hoặc lưu sửa thành công. Giữ bản nháp khi gửi lỗi và khi hoàn tất gửi sau khi đã đổi hội thoại.
- Lựa chọn rỗng, lọc hết kết quả, Hủy/Esc, giới hạn 20 tệp và đường dẫn không tồn tại.
- Cài đặt và hộp thoại nhập tên giữ dữ liệu khi lưu lỗi; chặn đóng trong lúc ghi; tên rỗng không đóng hộp thoại.
- Bản sao tệp bị hủy giữ nguyên tệp đích cũ và dọn tệp tạm. Cửa sổ nhúng chọn đúng hộp thoại đang mở làm cửa sổ cha.
- Bảng thành viên dùng chung lần tải đang chờ trong cùng hội thoại; lần tải cũ không ghi đè trạng thái sau khi chuyển/đóng hoặc lưu thành viên.

## Kết quả

- Build Debug và Release: thành công, không có cảnh báo.
- Bộ kiểm thử hồi quy: 112/112 ca đạt; trong đó có 16 ca UI mới chạy trên luồng STA với vòng lặp WinForms.
- Kiểm tra hai tiến trình WinForms bằng UDP loopback: cả A và B thành công. Mỗi máy xác nhận 6 tệp đính kèm, mở tệp, xem album, trích dẫn, ảnh từ bảng nhớ tạm và thêm/bớt thành viên từ hội thoại.
- Rà soát ảnh Giới thiệu ở kích thước tối thiểu với mô phỏng DPI 96/144/192 (100%/150%/200%). Kiểm thử mô phỏng kích thước, font và DPI của control; không đổi thiết lập màn hình Windows. Các nút cập nhật và Đóng vẫn nằm trong vùng hiển thị; phần giới thiệu có thể cuộn.
- Ảnh từ hai tiến trình thật được chụp ở DPI 120 (125%), gồm chat rộng/hẹp, bảng hội thoại, nhóm, cài đặt, chẩn đoán và album 780/1080.

Các lần chạy đầu phát hiện lỗi dọn CancellationTokenSource khi Dispose lặp và tải bảng thành viên cạnh tranh; đã sửa và chạy lại. Riêng ca đồng bộ lịch sử nhiều trang được tăng thời gian chờ phản ứng từ 15 lên 35 giây để cho phép vòng đồng bộ nền kế tiếp; giữ nguyên các kiểm tra nội dung và không đổi mã giao thức/đồng bộ.

## Chạy lại và hiện vật cục bộ

### Hiệu chỉnh căn nút và tốc độ chọn hội thoại

- Hai hàng nút trong Giới thiệu dùng chung chiều rộng, chiều cao và lề; các cột trùng nhau cả khi nút tải đổi thành hủy/cài. Kiểm thử kiểm tra tọa độ và kích thước tại DPI mô phỏng 96/144/192.
- Danh sách Tin nhắn có đường phân cách dưới mỗi người/hội thoại dày đúng một pixel. Đổi lựa chọn tô sáng ngay hai hàng liên quan, giữ nguyên các control.
- Tái sử dụng control tin nhắn theo thứ tự hiển thị; chỉ cập nhật layout nếu nội dung/trạng thái/đính kèm thực sự thay đổi. Đóng menu cũ khi một control được dùng cho tin khác, giữ đúng đích của thao tác trả lời.
- Lần mở lịch sử dài đầu tiên chuẩn bị control theo lô nhỏ, nhường vòng lặp UI giữa các lô; có trạng thái đang tải và hủy tác vụ cũ khi chọn người khác. Kiểm thử hai tiến trình bổ sung thao tác chuyển liên tiếp giữa hai hội thoại để kiểm tra kết quả cuối cùng.

Đo phần dựng UI với 80 tin nhắn chữ trên cùng máy kiểm thử (không gồm truy vấn database):

| Thao tác | Trước | Sau |
|---|---:|---:|
| Chuyển sang hội thoại khác | 1.661 ms | 26,4 ms |
| Quay lại hội thoại trước | 1.647 ms | 25,6 ms |
| Cập nhật dữ liệu không đổi | 23,2 ms | 3,4 ms |

Lần tạo 80 control đầu tiên vẫn cần khoảng 1,2 giây tổng thời gian trong phép đo đồng bộ; đường chạy thực tế tách việc này thành các lô để giao diện tiếp tục nhận thao tác, thay vì chặn toàn bộ vòng lặp UI. Các số đo là kết quả chạy tại máy này, không phải cam kết thời gian cho mọi máy hoặc mọi loại đính kèm.

Kết quả sau hiệu chỉnh: **115/115 kiểm thử đạt**, gồm **19 ca UI**; build Debug/Release không cảnh báo; hai tiến trình chat thật thành công ở DPI 120. Ảnh và số đo trước/sau nằm trong `.codex-temp/ui-polish-before/`, `.codex-temp/ui-polish-after/`; báo cáo TRX trong `.codex-temp/ui-polish-final-tests/`; kiểm tra hai tiến trình trong `.codex-temp/ui-polish-two-instances/`.

Chạy từ thư mục solution trên Windows:

```powershell
dotnet build LanChat.sln --no-restore -m:1 -p:UseSharedCompilation=false
dotnet build LanChat.sln --no-restore -c Release -m:1 -p:UseSharedCompilation=false
dotnet test tests/LanChat.Tests/LanChat.Tests.csproj --no-build --no-restore -m:1
pwsh -NoProfile -File scripts/Verify-TwoInstances.ps1
```

Đặt biến môi trường `LANCHAT_UI_CAPTURE_DIR` nếu muốn lưu ảnh kiểm thử DPI. Testhost cần quyền kết nối loopback; lần chạy trong sandbox trước đó bị timeout kết nối, các lần kiểm chứng dùng testhost ngoài sandbox.

Hiện vật của đợt này nằm trong thư mục không theo dõi Git `.codex-temp/ui-ux-20261118/`: báo cáo TRX trong `verified-tests/`, ảnh `about-96.png`, `about-144.png`, `about-192.png`, và báo cáo/ảnh hai tiến trình trong `two-instances-final/`. Mỗi lần chạy hai tiến trình phải dùng thư mục kết quả mới, không dùng dữ liệu chat thật.
