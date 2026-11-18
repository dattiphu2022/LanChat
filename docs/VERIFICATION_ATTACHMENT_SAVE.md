# Kiểm chứng lưu đính kèm và mở lại bản đã lưu

Ngày kiểm tra: 18/11/2026, Windows, .NET Framework 4.8 x64.

## Kết quả

| Hạng mục | Kết quả |
| --- | --- |
| Build Debug và Release | Thành công, 0 cảnh báo, 0 lỗi |
| `LocalAttachmentTests` | 19/19 đạt |
| `WindowsUiTests` | 26/26 đạt |
| Hồi quy, loại riêng ca lịch sử nhóm đã xác nhận lỗi nền | 140/140 đạt |
| Hai tiến trình WinForms với dữ liệu riêng | Đạt, mỗi tiến trình xác minh lưu/mở bản sửa của một tệp và một ảnh |
| Bố cục đính kèm 100%, 150%, 200% | Đạt bằng giả lập DPI của control; đã kiểm tra ảnh chụp |
| WinForms thật trên màn hình hiện tại | Đạt ở DPI 125%, gồm album kích thước tối thiểu 780 × 580 |

Lượt chạy toàn bộ còn lỗi `GroupHistoryTests.OneRemainingHolderAutomaticallySharesAllPagesWithCurrentGroupMembers`: hết thời gian chờ đồng bộ lịch sử 215 tin tại dòng 96. Ca này cũng thất bại khi chạy riêng trên bản hiện tại và trên **HEAD gốc `19e8ddb`** trích xuất vào `.codex-temp/attachment-save-baseline`, chưa có các thay đổi lưu đính kèm. Chưa xác nhận toàn bộ bộ kiểm thử xanh; không sửa giao thức hoặc điều phối mạng để né lỗi này.

## Phạm vi đã xác minh

- Lần lưu đầu chuyển bản làm việc đã sửa, giữ tệp nguồn người gửi và blob gốc. Khởi tạo host mới trên cùng dữ liệu vẫn mở đúng vị trí đã lưu.
- Lần lưu sau chép nội dung hiện tại sang nơi mới, giữ bản đã lưu trước. Chọn đúng đích đang dùng không tạo bản sao.
- Tệp, ảnh và thư mục có chỉnh sửa, tệp mới, thư mục rỗng; tên trùng giữa tệp và thư mục tự thêm hậu tố.
- Nhánh di chuyển cùng ổ và nhánh sao chép qua tệp tạm. Nhánh sao chép được ép bằng điểm kiểm thử `ForceLocalCopy`, chưa thử trên hai ổ vật lý khác nhau.
- Tệp bị khóa; lỗi tạo metadata; khóa metadata đúng lúc công bố; hủy khi sao chép và ngay sau khi ghi ý định di chuyển. Không báo thành công trước khi ghi xong ánh xạ.
- Phục hồi ý định bị gián đoạn trước/sau di chuyển bằng host mới; ưu tiên nguồn còn tồn tại, dùng đích nếu nguồn đã chuyển. Đây là mô phỏng điểm gián đoạn, chưa thử mất điện.
- Mở đồng thời phải chờ lưu và nhận đường dẫn đã cam kết. Tải nền không tạo lại bản trong LanChat khi bản đã lưu ngoài bị mất.
- Tìm lại vị trí, khôi phục bản gốc, giữ ánh xạ khi hủy. UI cảnh báo không thể khôi phục chỉnh sửa cục bộ đã mất.
- Lưu hàng loạt giữ các mục đã xong, tiếp tục sau lỗi từng mục, báo mục bị hủy; album chỉ chọn ảnh cùng `MessageId`.
- Nút Mở/Lưu phát đúng lệnh, giữ các control khi bind lại cùng dữ liệu. Đóng tiến độ mở không đóng hộp thoại khác; callback sau khi dispose được bỏ qua.
- Hai tiến trình xác minh đường dẫn chuyển sang thao tác mở sau cả hai lần lưu và kiểm tra SHA-256 bản gốc vẫn đúng. Hook bắt đường dẫn mở, không thực sự chạy ứng dụng ngoài hoặc bộ cài trong kiểm thử.

## Cách chạy lại

```powershell
dotnet build LanChat.sln --no-restore -m:1 -p:UseSharedCompilation=false
dotnet build LanChat.sln -c Release --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test tests/LanChat.Tests/LanChat.Tests.csproj --no-build --no-restore
# Để kiểm tra riêng phần còn lại khi điều tra lỗi lịch sử nhóm:
dotnet test tests/LanChat.Tests/LanChat.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~OneRemainingHolderAutomaticallySharesAllPages'
pwsh -NoProfile -File scripts/Verify-TwoInstances.ps1
```

Các tệp kiểm chứng của lượt này:

- `.codex-temp/test-results/attachment-save-regression-final.trx`: 140 ca đạt.
- `.codex-temp/test-results/attachment-save-all-retry.trx`: lượt toàn bộ có lỗi lịch sử nhóm.
- `.codex-temp/test-results/attachment-save-group-isolated.trx`: ca lịch sử nhóm chạy riêng.
- `.codex-temp/baseline-results/baseline-group.trx`: cùng lỗi trên HEAD gốc.
- `.codex-temp/attachment-save-two-instances-final-20261118/A.json`, `B.json`: hai tiến trình đều `Success=true`, `VerifiedSavedAttachments=2`.
- `.codex-temp/attachment-save-ui-final/attachments-96.png`, `attachments-144.png`, `attachments-192.png`: ảnh đối chiếu DPI; album thật nằm cùng thư mục báo cáo hai tiến trình.

Hướng dẫn thao tác đã cập nhật trong README và `docs/HUONG_DAN_SU_DUNG.md` được nhúng vào ứng dụng.
