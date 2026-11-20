# LanChat — bàn giao nâng cấp UI/UX (20/11/2026)

Đã triển khai các thay đổi UI của P0–P2 trên WinForms/.NET Framework 4.8. Giữ giao thức, Raft, E2EE, TLS, DPAPI, schema, dữ liệu và pipeline publish/ký/cập nhật. Không thêm thư viện UI hoặc Timer. Chưa tuyên bố nghiệm thu toàn bộ: một kịch bản lịch sử nhóm có sẵn vẫn timeout; một số kiểm tra hệ điều hành thực tế và bộ cài ký số còn cần thực hiện.

## 1. Build và kiểm thử

| Kiểm tra | Kết quả / chứng cứ |
|---|---|
| Debug solution | PASS, 0 warning / 0 error; verification/uiux/debug-build.log |
| Release WinForms | PASS, 0 warning / 0 error; verification/uiux/release-build.log |
| Toàn bộ UI tại đợt chốt | 75/75 PASS; ui-accepted/ |
| QR bốn DPI và highlight không dấu sau chỉnh cuối | 7/7 PASS; qr-search-delivery/ (4 QR + 3 highlight) |
| Bộ kiểm thử tuần tự | 218/218 PASS; delivery-sequential/; loại đúng một bài lịch sử nhóm được báo riêng bên dưới |
| Hai tiến trình WinForms thật | A/B Success=true; VerifiedLanUpdate=true; two-processes/A.json và B.json |
| Bộ lọc/chọn hội thoại sau rút gọn dấu trạng thái | 1/1 PASS; conversation-final/ |
| Whitespace | git diff --check không có lỗi whitespace |

Các bài kiểm thử dùng thư mục và dữ liệu giả riêng. Hai tiến trình xác nhận chat riêng/nhóm, 6 tệp/ảnh, mở và lưu bản chỉnh sửa, gallery, trích dẫn, clipboard, thành viên, nhắc tên/chuyển tiếp và truyền bộ cài giả qua LAN. Bộ cài giả không được thực thi. Tiến trình tự đóng.

Sau lượt toàn suite, các thay đổi cuối chỉ thuộc UI: icon trạng thái, callback gallery, QR, highlight và rút gọn dấu ghim/im lặng/@ trong hội thoại. Các phần này có lượt UI, kiểm tra tập trung và hai tiến trình riêng. [Tóm tắt máy đọc](verification/uiux/acceptance-summary.json). Không cộng các lượt lặp thành số bài kiểm thử duy nhất.

Trong lượt khảo sát UI, build kiểm thử dùng -p:LanChatDevelopmentSigning=false vì sandbox không truy cập được chứng thư. Khi phát hành v1.2.10, kiểm tra ngoài sandbox xác nhận chứng thư vẫn có khóa riêng; EXE và bộ cài đã được ký theo pipeline hiện có. Không sửa trust store. Xem [kiểm chứng phát hành](VERIFICATION_V1_2_10_RELEASE.md).

### Lỗi và các lượt đối chứng

- GroupHistoryTests.OneRemainingHolderAutomaticallySharesAllPagesWithCurrentGroupMembers timeout ở GroupHistoryTests.cs:96 khi chờ đủ 215 tin cho hai thành viên mới. Chạy riêng trên mã sửa và HEAD gốc e2056aa0577809c91c304d3db110dd90205d270e đều tái hiện. Chứng cứ: group-history-isolated/ và group-history-baseline/. **Mức trung bình:** chưa nghiệm thu kịch bản khôi phục nhóm lớn chỉ còn một máy giữ lịch sử. Chưa đủ bằng chứng kết luận mất dữ liệu. Không đổi giao thức, quorum hoặc tăng timeout để làm xanh bài này.
- Có một lượt full suite đạt 219/219 (final-tests/). Các lượt song song sau đó có lỗi mạng không ổn định: NetworkStream đã dispose trong LanUpdateTests.ChangedNodeAndTamperedSignatureAreRejected và timeout bàn giao trong DistributedTests.NominationAndWithdrawalKeepVotesAndReplicateQueuedMessages (delivery-tests/). Hai bài này đều PASS trong lượt tuần tự 218/218. Giữ cả báo cáo thất bại, không suy luận nguyên nhân là UI.
- Chạy tuần tự bằng tham số VSTest xUnit.ParallelizeTestCollections=false và xUnit.MaxParallelThreads=1 theo [tài liệu xUnit](https://xunit.net/docs/config-runsettings); không đổi cấu hình test mặc định trong repository.
- Một lượt QR gặp clipboard trống thoáng qua. Fixture cho thử lại tối đa ba lần, vẫn xác nhận handler copy thành công và văn bản đúng nguyên vẹn. Không sửa nội dung clipboard hoặc nới kiểm tra UTF-8 của sản phẩm.

## 2. Thành phần đã nâng cấp

| Hạng mục | Hành vi và phạm vi |
|---|---|
| P0 — Bố cục | Rộng/trung bình/hẹp theo kích thước logic quy đổi DPI; rail 96/60 px, dưới khoảng 800 px chuyển danh sách/chat. Nút quay lại và Alt+Trái. Giữ cùng control, draft, hội thoại và cache. Đặt ô điều hướng tường minh để không chồng khi ẩn hồ sơ. |
| P0 — Điều hướng | Tin nhắn, Danh bạ, Nhóm, Tệp, Mạng chat; tooltip khi rail thu gọn. Mạng trên toolbar chỉ cần khi chưa mở mạng. Thêm gom giới thiệu/cập nhật/hướng dẫn và giữ badge cập nhật. Quét tất cả khác quét mạng hiện tại. |
| P0 — Hội thoại | Tất cả / Chưa đọc / @ Tôi / Đã ghim; tên đậm khi chưa đọc, preview, thời gian riêng, badge, ghim/mute/@. Giữ tìm kiếm/bộ lọc/chọn kể cả khi hội thoại bị lọc ẩn. Bỏ bind khi dữ liệu không đổi và tái sử dụng row. Số đếm bộ lọc có trong AccessibleName. |
| P0 — Trạng thái tin | Icon dùng chung UiIcons, màu, chữ và tooltip. Local pending, synced, delivered, read chỉ dựa dữ liệu thực. Synced không suy ra người nhận đã nhận. Backend chưa có đủ bảy trạng thái độc lập/API retry tin thủ công; không tạo xác nhận giả. Lỗi gửi giữ draft, outbox giữ retry hiện có. |
| P1 — Soạn tin | Một hàng +/editor/Gửi; menu tệp, thư mục, dán ảnh, chụp vùng; giữ preview/context, tự giãn, Enter/Shift+Enter/Ctrl+V/kéo thả/Esc và bản nháp sửa/trả lời. |
| P1 — Trung tâm tệp | Theo từng mạng: tên/hội thoại/trạng thái/kích thước có dữ liệu; mở/lưu/chuẩn bị/thử lại. Ảnh mở bằng gallery cũ. Theo dõi row nhìn thấy qua timer AttachmentActions có sẵn; kiểm tra lại tệp chưa hoàn thành. Đóng chỉ ngừng theo dõi; tác vụ dùng lifetime mạng. Không thêm hủy truyền. Tổng dung lượng thư mục chưa có, hiển thị —. Lỗi nền không có snapshot thì chỉ báo chờ nguồn/chờ tải. |
| P1 — Tìm kiếm | Tìm nhanh tên/đơn vị không dấu dùng hàm có sẵn. Ctrl+F trong hội thoại, Ctrl+Shift+F toàn bộ lịch sử được phép xem; kết quả hai dòng, thời gian, snippet tô từ khóa kể cả không dấu, nút dừng, mở đúng MessageId. Giữ FTS/cancellation/generation, không đổi DB. |
| P1 — Kết nối | Chỉ báo peer thực, đang quét/gián đoạn/lỗi; menu quét mạng hiện tại/kiểm tra/chẩn đoán. Không suy mất khả năng chat từ leader. Chi tiết Raft ở chẩn đoán. Snapshot trạng thái dùng lại trong chu kỳ 500 ms; revision/hiện diện đổi làm mới ngay. |
| P1 — Nhóm | Tìm thành viên không dấu, đếm thay đổi chưa lưu, giữ draft khi lọc, thông báo thiếu máy điều phối. Giữ gate lưu/chờ commit/ngăn nhấn lặp và draft khi thất bại. |
| P1 — QR | Sao chép nguyên văn, bố cục dọc khi hẹp, PNG/đóng. Giữ dung lượng/giới hạn/lỗi sau copy/save. Plain Text UTF-8, 500 byte, ECC M, Version 18, client-only; không cắt văn bản. |
| P1 — Thông báo | Tùy chọn ẩn nội dung; giữ cursor chống lặp, ưu tiên mention, định tuyến đúng mạng/hội thoại/tin và UpdateCoordinator dùng chung. Chính sách mention vượt mute có sẵn được giữ. Chỉ cài cập nhật sau xác nhận. |
| P2 — Khởi đầu | Hướng dẫn một lần dùng lại hồ sơ, tạo/tham gia/mã mời và quét mạng. Cho Để sau; mở lại từ Thêm/khay. Không có cơ chế mời thứ hai hoặc hiển thị secret. Logic tự hiện/bước mạng mới chủ yếu được kiểm tra mã; render/dialog có kiểm thử. |
| P2 — Theme | Màu chữ phụ tăng tương phản, màu trạng thái chung, icon hệ thống thống nhất; không có hiệu ứng hoặc timer mới. |
| P2 — Cá nhân hóa | Cỡ chữ chat 10/11/12/14 pt; mật độ Gọn/Tiêu chuẩn/Thoải mái, áp dụng ngay và giữ mốc đọc/draft. ui-preferences.json cục bộ, không replicate. Dark mode chưa triển khai để tránh native control/màu nền không nhất quán. |
| P2 — Trợ năng | AccessibleName/description, tooltip rail/icon mới, focus row, Enter/Space danh bạ/hội thoại, Alt+Trái và Ctrl+Shift+F. Chưa coi là chứng nhận WCAG hoặc kiểm chứng screen reader toàn bộ. |

## 3. Tệp mã nguồn

Trong src/LanChat.WinForms:

- Controls sửa: AttachmentActions.cs, ChatControls.cs, ContactControls.cs, ConversationPanel.cs, GroupMembersControl.cs, Theme.cs, UiInteraction.cs.
- Forms sửa: AttachmentForm.cs, DpiForm.cs, GroupsForm.cs, MainForm.cs, MainForm.Advanced.cs, MainForm.Verification.cs, MessageSearchForm.cs, NetworkWorkspace.cs, NetworkWorkspace.Updates.cs, QrShareForm.cs, SettingsForm.cs.
- NetworkCatalog.cs.
- Mới: Controls/MessageDeliveryPresentation.cs (có DeliveryStatusLabel), Controls/SearchResultListBox.cs, Forms/TransferCenterForm.cs, Forms/GettingStartedForm.cs, UiPreferences.cs.

Kiểm thử: sửa WindowsUiTests.cs và WindowsCompactWorkspaceTests.cs; thêm WindowsUxTests.cs, WindowsUxWorkflowTests.cs, WindowsUxAppearanceTests.cs, WindowsUxEvidenceTests.cs. Cập nhật HUONG_DAN_SU_DUNG.md.

Không sửa Domain/Application/Infrastructure, migrations, project/target/publish hoặc installer. Danh mục khảo sát trước thay đổi: verification/uiux/source-inventory.txt (102 file C#). Đã đọc kiến trúc, hợp đồng, các luồng UI liên quan và kiểm thử trước khi triển khai; tái sử dụng history cache, FTS, draft, mentions, transfer coordinator, QR và update coordinator có sẵn.

## 4. Ảnh và ma trận kiểm tra

**Trước nâng cấp, cửa sổ rộng 660 px**

![Bố cục trước nâng cấp ở 660 px](verification/uiux/before/chat-660.png)

**Sau nâng cấp, cửa sổ rộng 660 px**

![Bố cục sau nâng cấp ở 660 px](verification/uiux/after/chat-660.png)
- [Sau 800](verification/uiux/after/chat-800.png) · [Sau 1280](verification/uiux/after/chat-1280.png).
- [Hai máy thật — giao diện](verification/uiux/two-processes/A.1020.png) · [Nhóm](verification/uiux/two-processes/A.groups.png) · [Thông tin](verification/uiux/two-processes/A.conversation-panel.png).
- [QR 200%](verification/uiux/after/qr-192.png) · [Cài đặt 200%](verification/uiux/after/preferences-192.png) · [Trung tâm tệp](verification/uiux/after/transfer-center.png).
- [Báo cáo A](verification/uiux/two-processes/A.json) · [B](verification/uiux/two-processes/B.json).

Ma trận: 660×450, 800×600, 1024×768, 1280×800, 1920×1080 theo kích thước logic, nhân scale 96/120/144/192 DPI. Các mức DPI này được mô phỏng layout/font, không đổi cấu hình màn hình. DpiForm có test seam scoped vì DeviceDpi của HWND luôn phản ánh monitor thật. Lượt hai máy và benchmark dùng DPI thật 120 (125%). Không đánh đồng mô phỏng với kiểm thử phần cứng ở bốn DPI.

## 5. Hiệu năng

Cùng Windows session/net48, cỡ chữ thực tế 10 pt, dataset và phương pháp: hai hội thoại 24 tin, 12 lượt chuyển ấm; dựng 80 tin và 6 bind ấm. Mỗi bản chạy testhost riêng, nối tiếp, không chạy test khác đồng thời. HEAD gốc được dựng ở thư mục thử riêng, không thay checkout. Cặp sạch cuối đã rebuild để bỏ binary có hook khảo sát.

| Chỉ số — cặp sạch cuối | Trước | Sau |
|---|---:|---:|
| Trung vị chuyển hội thoại | 59,31 ms | 59,65 ms |
| Dựng 80 tin lần đầu, gồm native handles | 1.041,44 ms | 1.096,15 ms |
| Trung vị bind ấm 80 tin | 66,97 ms | 58,96 ms |

Chuyển hội thoại gần như tương đương (+0,33 ms); cold render tăng khoảng 55 ms trong cặp cuối; bind ấm thấp hơn trong cặp cuối. Các lượt khảo sát trước dao động rộng nên không tuyên bố tăng tốc toàn ứng dụng, kết luận thống kê hoặc bảo đảm trên mọi máy/cụm lớn. Chưa đo startup, RAM/CPU. Đã bỏ layout workspace trùng và đọc trạng thái SQLite trùng trong chu kỳ UI; không thêm Timer. Hook phân tách bước đã gỡ khỏi mã bàn giao.

[Số đo tổng hợp và mẫu thô](verification/uiux/performance-comparison.json); toàn bộ lượt khảo sát được giữ trong measurements/. Các kết quả có hook chỉ dùng chẩn đoán, cặp CleanRebuiltPair là phép đo bàn giao.

## 6. Phần còn lại

1. **Trung bình:** timeout khôi phục lịch sử nhóm lớn có sẵn, đã đối chứng trên mã gốc; chưa sửa backend hoặc tuyên bố nghiệm thu kịch bản này.
2. **Chưa triển khai:** dark mode nhất quán toàn bộ WinForms.
3. **Cần PC nghiệm thu:** Windows 10/11 với bốn DPI thật, đổi monitor, IME, Narrator và toast hệ điều hành. Phần định tuyến notification giữ cơ chế cũ, riêng tư được kiểm thử qua incoming-notice UI path; chưa tự động bấm toast Windows thật.
4. **Phát hành cập nhật:** publish và ký bộ cài đã được thực hiện cho v1.2.10. Chưa cài vào dữ liệu thật của người dùng và chưa benchmark startup/RAM/CPU/cụm lớn.

## 7. Tái chạy và tài liệu tham chiếu

- Build: dotnet build LanChat.sln --no-restore -m:1 -p:UseSharedCompilation=false -p:LanChatDevelopmentSigning=false.
- Suite tuần tự: dotnet test tests/LanChat.Tests --no-build --no-restore --filter FullyQualifiedName!~OneRemainingHolderAutomaticallySharesAllPagesWithCurrentGroupMembers -- xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1. Bài bị loại phải chạy riêng và vẫn báo cáo kết quả.
- Hai máy: pwsh -NoProfile -File scripts/Verify-TwoInstances.ps1 -WithLanUpdate -OutputDirectory <thư-mục-thử-mới>.
- LANCHAT_UI_CAPTURE_DIR điều khiển nơi lưu ảnh. LANCHAT_UX_EVIDENCE bật fixture CaptureResponsiveWorkspaceAndMeasureConversationSwitches và lưu JSON/screenshot. Không dùng thư mục dữ liệu LanChat thật.
- Tham chiếu thiết kế: [Microsoft responsive layout](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design), [WCAG 2.2](https://www.w3.org/TR/WCAG22/), [xUnit VSTest configuration](https://xunit.net/docs/config-runsettings).
