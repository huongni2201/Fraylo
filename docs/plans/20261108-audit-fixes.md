# Sửa năm lỗi từ review toàn project — 2026-11-08

Owner: Codex coordinator, triển khai tuần tự và tự review. Người dùng yêu cầu sửa cả năm finding trong chat. Dependency TM, UPG, FBA và Analytics đã tích hợp; giữ `tools/` chưa theo dõi. Không đổi schema, dùng DB/profile fixture riêng, không commit/push/phát hành hoặc mở DB cá nhân.

## Hành vi cần đạt

1. Nút Tạo lại của ReferenceVideo dùng cùng bộ dựng yêu cầu với workflow: giữ ảnh + video mẫu, hash, thời lượng đích độc lập, `PromptApproximation` và `FitOriginalAudioByAI`. Run đã hoàn tất chuyển về Running cùng transaction với job; run đang Paused giữ Paused. Giữ lịch sử video.
2. Scene là nguồn prompt, ảnh, audio và video hiện hành. Gỡ liên kết (`null`) không lấy lại từ bản sao AppSettings hoặc mặc định dự án. Caption vẫn giữ. Snapshot job đã lưu không đổi khi Scene sửa tiếp.
3. App và launcher giữ cùng mutex `Fraylo.Desktop.Installation`. App thứ hai không mở DB; launcher chỉ backup/cài khi app đã thoát và giữ khóa xuyên suốt thao tác. Kiểm thêm tiến trình Fraylo cũ chưa biết mutex. Runtime test dùng mutex riêng theo DB tạm. Không buộc đóng cửa sổ/công việc khác.
4. Lượt Analytics Facebook còn Running nhưng không có worker được phục hồi Failed trong phiên qua dashboard/cancel/lần cập nhật kế tiếp. Lỗi lưu không xóa capture thành công trước, không tự đọc browser lần nữa; replay cùng mã trả lượt lỗi, yêu cầu mới có thể chạy khi nơi lưu hoạt động trở lại.
5. Kỳ N ngày của TikTok gồm hôm nay và N−1 ngày trước theo lịch UTC hiện hành, dùng cùng mốc cho summary và biểu đồ. Tất cả vẫn giữ toàn bộ dữ liệu; không đổi null thành 0.

## Kiểm chứng

Baseline review: web76, workflow30, Facebook application183/rewrite78/adapter37/Analytics29, schema, Infrastructure, Publishing32, ReferenceVideo, Analytics AI và queue/retry/recovery/concurrency PASS. Build Release PASS; NuGet audit offline có cảnh báo.

Regression bổ sung trong `AuditFixSmokeTests`, `ReferenceVideoSmokeTests` và `FacebookAnalytics.SmokeChecks`; log lượt sửa ở `.tmp/audit-fixes-20261108/`.

- Release solution và TypeScript/Vite PASS. Hai cảnh báo còn lại: nullable cũ trong test concurrency và NU1900 do NuGet audit offline; không có cảnh báo mới từ phần sửa.
- Web76, workflow30, ReferenceVideo (gồm tạo lại, reserve và apply), Analytics9, audit-fixes (Scene/snapshot, kỳ1/7/30/Tất cả, mutex qua tiến trình con/release/abandoned), remediation14, queue19, retry9, recovery12, concurrency14 và Analytics AI PASS.
- Facebook Analytics33 PASS: bốn kiểm tra mới xác nhận lỗi lưu được phục hồi Failed trong phiên, không tự gọi browser/replay, giữ capture cũ và cho cập nhật mới sau khi nơi lưu hoạt động trở lại.
- TikTok Publishing32, Facebook application183 và Facebook rewrite78 PASS trên source cuối. Các trình duyệt trong test chỉ mở trang fixture cục bộ.
- Schema/model parity, nâng cấp/backup/rollback PASS. Launcher publish self-contained/single-file/trimmed với IL2026 coi là lỗi PASS; sandbox ban đầu chặn task host, lượt ngoài sandbox PASS. `dotnet format whitespace` trên các tệp C# đã sửa PASS ngoài sandbox; lượt sandbox bị treo và đã dừng.
- Bộ remediation cũ có hai fixture dùng `ProductOnly` đã bị loại khỏi sản phẩm; đổi sang ReadyImage, giữ mục tiêu kiểm nguyên văn prompt, kết quả14/14 PASS.

NOT_RUN: provider/Facebook/TikTok thật, cập nhật R2/bộ cài Windows sạch và UI WebView2/DPI. Mutex mới của launcher cần bộ cài chứa launcher mới; ZIP tự cập nhật hiện không thay launcher cũ. App mới kiểm thêm cửa sổ cũ trước khi handoff; không coi launcher cũ đã được vá.
