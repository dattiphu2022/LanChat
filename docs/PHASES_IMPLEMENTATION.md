# Triển khai Phase 2–5

Đây là ghi nhận triển khai theo từng phase năm 2026. Tài liệu sử dụng, đóng gói và kết quả kiểm thử hiện hành nằm ở [Hướng dẫn sử dụng](HUONG_DAN_SU_DUNG.md), [Đóng gói và phát hành](../installer/README.md) và [Kết quả kiểm chứng](VERIFICATION.md).

Giữ nguyên dữ liệu Phase 1 và giao diện native WinForms. Không thay đổi dữ liệu người dùng khi kiểm thử.

## Phase 2

Tạo `DistributedModels.cs`, `DistributedContracts.cs`, `ChatStore.Replication.cs`, `ReplicationBackgroundService.cs`, `ConsensusService.cs` và migration. Sửa ChatStore, DbContext, transport, registration. Event/projection/outbox cùng transaction; cursor chỉ tiến qua sequence liên tục. Control log và vote bền vững; voter membership không suy ra từ số peer online. Thêm member lần lượt bằng joint consensus (majority cấu hình cũ và mới), không tự loại voter offline.

## Phase 3

Thêm projection xác định cho edit/recall/reaction/receipt, reply/forward/local delete, FTS5, UI context menu và search. Giữ event immutable. Test đảo thứ tự event, duplicate, offline recovery và tính đồng nhất của projection.

## Phase 4

Thêm attachment metadata, file storage và stream P2P có cancel/progress/SHA256/temp/atomic rename. UI preview kéo nhiều file chờ Send, ảnh local có preview. Binary không tự replicate.

## Phase 5

Nâng protocol, TLS với certificate cluster đã pin trong mã mời, credential lưu DPAPI, xác thực HMAC. Thêm chẩn đoán consensus/replication, debug fault injection, kiểm thử partition/quorum và migration cấu hình cũ. Cập nhật tài liệu, build Release, kiểm thử nhiều process.

Mỗi phase phải build/test/migration trước khi chuyển tiếp. Những giới hạn hoặc kiểm chứng trên máy vật lý còn thiếu phải được ghi rõ trong báo cáo cuối.

## Kết quả triển khai ngày 22/09/2026

Các hạng mục Phase 2–5 nêu trên đã có mã chạy và giao diện tương ứng. Build Debug/Release thành công, 40 bài kiểm thử đạt, model khớp đủ 3 migration. Hai tiến trình WinForms xác minh chat/nhóm/sửa/reply/reaction/kéo thả/tải tệp TLS và SHA256. Bản Release nằm ở `artifacts/LanChat-1.0`.

Control log giữ các sự kiện thay đổi nhóm/membership; chat event store giữ các sự kiện chat/profile/receipt. Binary không replicate. Giao diện có chẩn đoán và các nút mô phỏng lỗi chỉ trong Debug.

Tại thời điểm kiểm chứng Phase 5, chưa có benchmark cụm lớn, E2EE, mã hóa toàn bộ cơ sở dữ liệu, voter decommission hay compaction. Phạm vi bảo mật hiện tại nằm trong [Kiến trúc và luồng dữ liệu](ARCHITECTURE.md). Nghiệm thu trên PC vật lý theo [Checklist nghiệm thu](ACCEPTANCE.md).
