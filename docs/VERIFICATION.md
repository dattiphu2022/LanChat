# Kết quả kiểm chứng Phase 1–5

Ngày kiểm chứng: **22/09/2026**. Windows, SDK **10.0.400**, target **net9.0 / net9.0-windows**, Desktop Runtime **9.0.19**.

## Kết quả

| Nội dung | Kết quả |
|---|---|
| Build Debug và Release | Đạt, 0 warning / 0 error |
| Automated tests | **40 passed / 0 failed / 0 skipped** |
| Migration/model | 3 migration, không còn thay đổi model chưa tạo migration |
| Publish | artifacts/LanChat-1.0/LanChat.WinForms.exe cùng dependencies |
| Khởi động gói Release | MainForm mở thành công trên dữ liệu mẫu riêng, đóng sạch, exit code 0 |
| Hai tiến trình WinForms | UDP discovery, TLS, chat hai chiều, nhóm và tệp đạt |
| Ảnh giao diện | Đã xem ảnh chat, resize, preview kéo thả, ảnh nhận, nhóm, settings và diagnostics |

Lệnh kiểm chứng chính:

```powershell
dotnet build LanChat.sln --no-restore
dotnet build LanChat.sln -c Release --no-restore
dotnet test tests/LanChat.Tests --no-restore --logger "trx;LogFileName=all-phases.trx" --results-directory TestResults
dotnet ef migrations has-pending-model-changes --project src/LanChat.Infrastructure --no-build
dotnet publish src/LanChat.WinForms -c Release --no-restore --self-contained false -o artifacts/LanChat-1.0
pwsh -NoProfile -File scripts/Verify-TwoInstances.ps1 -UseBroadcast
```

## Phạm vi bài kiểm thử

- Network range /24, /25, /26, /27, /28, /16, /31, /32 và mask không hợp lệ.
- HLC, định danh direct conversation, message identity/sequence bền vững và ghi đồng thời.
- Migration/WAL/index; nâng schema Phase 1 có tin cũ, giữ UserId/NodeId/message/ghim, tạo event và FTS một lần.
- Duplicate/out-of-order event, cursor không vượt gap, trigger cấm sửa/xóa event.
- Edit/recall hội tụ; receipt/read, reaction add/remove, reply/forward; chặn sửa/thu hồi tin người khác.
- FTS không dấu, cập nhật sau sửa/thu hồi; xóa phía tôi không ảnh hưởng peer.
- Node D khôi phục **215 tin** A/B qua nhiều trang TLS, thứ tự và MessageIds giống nhau; không trở thành thành viên hội thoại A/B. Truy vấn cửa sổ 101 tin quanh kết quả tìm kiếm.
- Outbox/retry, dữ liệu tồn tại khi mạng lỗi và tiếp tục sau restart.
- Ba voter với majority/joint membership, tạo nhóm, chat nhóm; cô lập leader, đa số bầu lại, thiểu số không đổi nhóm, chat local vẫn lưu và đồng bộ sau nối lại.
- Hai candidate xin phiếu cùng term: chỉ một được chấp nhận; vote tồn tại sau restart; candidate có log cũ bị từ chối.
- Tạo/đổi tên/ảnh/thành viên/chủ nhóm/xóa nhóm; kiểm tra quyền chủ nhóm và nhóm đã xóa không gửi tiếp.
- Binary không tự replicate cùng metadata; tải TLS streaming, progress, SHA256, hủy giữa chừng và dọn tệp tạm.
- Từ chối tệp bị hỏng; nhập lại sửa blob local; peer đã tải phục vụ lại khi không biết nguồn gốc.
- Chặn hash/path không hợp lệ; nguồn không có báo lỗi và giữ metadata.
- TLS certificate khác bị chặn; cùng certificate nhưng HMAC secret sai vẫn bị từ chối.
- DPAPI bảo vệ credential trong cluster.json; mã mời giữ bootstrap, cấu hình giữ port và cho phép CLI override.
- Database không đổi sang cluster khác; UNC storage bị từ chối.

Test failover dùng thời gian rút ngắn (timeout 2 giây, heartbeat 200 ms) để chạy nhanh. Runtime mặc định dùng timeout 15 giây cộng probe/discovery/jitter, heartbeat 2 giây. Chưa đo thời gian failover thực tế trên ba PC vật lý.

## Hai tiến trình giao diện thật

Lần broadcast đạt: **.codex-temp/two-instances-20260922-132257**.

- Hai EXE, hai SQLite/thư mục, TCP port riêng, NodeId/UserId khác nhau.
- Discovery UDP broadcast tự tìm nhau, không khai báo endpoint peer.
- Gửi từ cùng handler của nút Gửi, sửa tin qua composer, reply và reaction qua handler tương ứng.
- Kiểm tra DragEnter/DragDrop với hai tệp local: chưa gửi ngay, có preview.
- Hai phía cùng 4 tin trực tiếp, metadata 4 tệp, tải và đối chiếu SHA256 của cả 4 tệp.
- Một nhóm được commit, cả hai máy gửi và nhận tin nhóm.
- Quorum ổn định: 2 voters, Joint=null, term=1, commit index=4 trong báo cáo mẫu.
- Chụp chat ở 1240×800 và 1020×650, preview file/ảnh, nhóm, settings, diagnostics.
- Các tiến trình do harness tạo tự thoát; không dừng tiến trình sử dụng thật.

Bằng chứng trong [verification/phase2-5/](verification/phase2-5/): A.json, B.json, all-phases.trx và ảnh PNG. Không sao chép cluster.json, secret hoặc database vào thư mục bằng chứng.

Đã xem:
- [Chat nhóm](verification/phase2-5/A.group-chat.png)
- [Preview trước gửi](verification/phase2-5/A.drop-preview.png)
- [Ảnh đã tải](verification/phase2-5/B.image-preview.png)
- [Quản lý nhóm](verification/phase2-5/A.groups.png)
- [Chẩn đoán](verification/phase2-5/A.diagnostics.png)

## Giới hạn của bằng chứng

Kiểm tra UI dùng **hai tiến trình cùng máy Windows**, còn failover/partition dùng **ba host riêng và SQLite riêng trong integration test**. Chưa chạy ba PC vật lý, kiểm thử dài ngày, DPI đa dạng hay tải tệp 20 GB. Thao tác kéo thả được đưa qua WinForms DragEnter/DragDrop trong harness; chưa thao tác chuột kéo trực tiếp từ Explorer. Các nút quản trị nhóm có kiểm thử nghiệp vụ; chưa click thủ công hết mọi nút và thông báo desktop.

Không coi các kết quả này là chứng nhận an toàn cho cụm lớn hoặc thành viên ác ý. Cluster chia sẻ credential và toàn lịch sử; không có E2EE, mã hóa DB, voter decommission hoặc control-log compaction. Xem [ACCEPTANCE.md](ACCEPTANCE.md) để nghiệm thu thực địa và [ARCHITECTURE.md](ARCHITECTURE.md) cho phạm vi thiết kế.

Bằng chứng Phase 1 cũ giữ ở [VERIFICATION_PHASE1.md](VERIFICATION_PHASE1.md).

## Cập nhật UX mở tệp — 22/09/2026

Bấm tệp trong tin nhắn mở bằng ứng dụng mặc định của Windows; nếu chưa có local thì tự tải, kiểm tra SHA256, tạo bản giữ đúng tên/đuôi dưới Attachments/Open và chuyển sang thao tác mở. Bản mở local tách khỏi blob P2P, giữ các sửa đổi local khi bấm lại. Có progress/hủy khi chuẩn bị lâu và chặn bấm trùng trong lúc tải.

- Build Debug và publish Release đạt; không đổi schema/migration.
- Kiểm thử tập trung FileTransferTests: **7 passed / 0 failed**, gồm local, remote qua TLS, mở lại khi nguồn offline, giữ sửa đổi local mà blob không đổi, tệp trùng tên/tên không an toàn, tệp hỏng và hủy tải. Tổng số bài hiện có là 43; lần này chạy tập trung 7 bài liên quan, không chạy lại toàn bộ 43 bài.
- Harness hai WinForms đạt tại `.codex-temp/two-instances-20260922-135229`: mỗi phiên gọi cùng handler bấm tệp 5 lần (4 tệp và mở lại một tệp), kiểm tra tên/đuôi, nội dung SHA256 và đường dẫn local tái sử dụng.
- Harness thay riêng bước khởi chạy ứng dụng ngoài bằng bộ ghi nhận đường dẫn; chưa xác minh thủ công Word/Excel/PDF viewer trên máy người dùng. Bản Release dùng Windows shell với UseShellExecute và verb open.

Bằng chứng: [verification/attachment-open/](verification/attachment-open/) gồm A.json, B.json và attachment-open.trx.

## Ảnh trực tiếp và album — 22/09/2026

- Build Debug và publish Release đạt, không cảnh báo/lỗi; không đổi schema/protocol.
- Chạy lại **toàn bộ 43 kiểm thử: 43 passed, 0 failed, 0 skipped**, bao gồm 7 bài truyền/mở tệp; kết quả `image-gallery.trx`.
- Harness hai tiến trình WinForms thật đạt tại `.codex-temp/two-instances-20260922-140952`. Mỗi phía gửi một tệp văn bản và hai PNG (ngang 800×450, dọc 450×800), kèm chú thích hai dòng; cùng nhận đủ 6 tệp và đối chiếu SHA256.
- Trước khi mở gallery, cuộn qua các tin ảnh: xác nhận cả 4 thumbnail đã có bitmap và cả 4 blob nằm trong cache local, bao gồm ảnh từ phía kia tự tải qua TLS.
- Mỗi phiên gọi handler bấm ảnh 4 lần; khung gallery chứa 4 ảnh của hai người gửi. Kiểm tra caption, giữ xuống dòng, phóng to thay đổi tỷ lệ, 100%, fit, chuyển ảnh và chuyển nhanh khi tác vụ trước chưa xong. Kết quả cuối không bị ảnh cũ ghi đè.
- Chụp và xem ảnh chat ở 1240×800/1020×650, gallery ở 1080×800/780×580; kiểm tra tự động tất cả nút gallery không bị khuất. Cửa sổ nhỏ tự xuống hàng nút điều khiển.
- Luồng tệp thường vẫn được gọi 3 lần mỗi phiên (hai tệp và mở lại), giữ đúng tên/đuôi và dùng lại đường dẫn local. Bước khởi chạy ứng dụng ngoài được thay bằng hook Debug như lần kiểm chứng trước.

Bằng chứng: [verification/image-gallery/](verification/image-gallery/) gồm A.json, B.json, ảnh PNG và TRX. Đây là kiểm tra trên cùng một PC; chưa thao tác chuột thủ công, kiểm tra DPI khác nhau hoặc xác minh từng định dạng EXIF/GIF/TIFF và ảnh sát giới hạn lớn. Các mục nghiệm thu thực địa nằm trong [ACCEPTANCE.md](ACCEPTANCE.md).
