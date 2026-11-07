# Facebook Analytics — evidence hiện hành

## Cập nhật điều hướng ngày 2026-11-07

Theo yêu cầu người dùng, Analytics đã chuyển vào **Facebook → Đăng bài Page → Analytics**, cạnh tab Đăng bài. Bỏ route/mục sidebar Facebook Analytics riêng. `FacebookPageScreen` lazy-load Analytics khi mở tab đầu tiên, giữ mounted sau đó và chỉ cho polling khi screen/tab đang active. Giữ tài khoản, Page, bộ lọc và dữ liệu khi đổi tab hoặc điều hướng màn khác rồi quay lại; không tự sync Facebook.

Regression điều hướng/Analytics 5/5 PASS; TypeScript/Vite và Desktop Release PASS 0 warning/error. Edge ẩn/fixture kiểm tab Analytics trong màn Page ở Light/Dark 800/1000/1280/1440 px, giữ bộ lọc khi đổi tab, dialog/history/Escape PASS; đã xem ảnh rộng Dark. Không đổi backend/schema/DB trong lần chuyển tab này. CodeGraph không trả kết quả, đối chiếu file trực tiếp. Kết quả live/WebView2 bên dưới thuộc implementation ban đầu; chưa chạy lại live hoặc WebView2 sau thay điều hướng.

Toàn bộ web tests chạy tuần tự (`node --test --test-concurrency=1 "src/**/*.test.ts"`) **67/67 PASS**. Lượt song song đầu 66/67, lỗi ở test Settings giữ lỗi kiểm phiên; không sửa Settings, lượt tuần tự đã PASS. Không coi lỗi timing này là lỗi do di chuyển Analytics.

Ngày 2026-11-06; owner Codex coordinator. Người dùng đã giao triển khai và chọn nghiệm thu trên một Fanpage thử nghiệm.

## FBA0 — đọc thật qua Fraylo

- DB nguồn mở ReadOnly, SQLite backup vào workspace để dựng registry. Không bootstrap/nâng cấp DB cá nhân. Dùng `PlaywrightBrowserController` + `ProfileOwner`, đúng profile Fraylo, `isHeadless=true`, danh tính phiên được so khớp; không log cookie/token. Không bấm đăng/sửa/xóa bài hoặc đổi actor.
- Danh sách Page tiếng Việt có 3 trang. Tên/link Page trong cùng card với liên kết quảng cáo `page_id` và inbox `asset_id`. ID Business Suite có thể khác ID profile công khai; không lấy ID public profile thay thế asset_id.
- Bề mặt **Business Suite → Nội dung → Bài viết và thước phim → Đã đăng** có grid `Bài viết đã đăng`. Checkbox mỗi dòng có tên `Chọn mục có ID <id>`; các cột có thể chứa ký tự zero-width, phải chuẩn hóa nhãn. ID Reel đối chiếu URL chi tiết `content_id` khớp ID checkbox.
- Fanpage được chọn không có nội dung trong 90 ngày; chọn radio **Trọn đời** (tự áp dụng) đọc được 2 Reel cũ. Bộ lọc `Thước phim` được quan sát nhưng bản đầu đọc mọi bài đã đăng trong phạm vi Trọn đời, không tự giới hạn 90 ngày.
- Ở Reel thử, bảng có Views bị thiếu nhưng phần chi tiết có **lượt phát thước phim**. Hai chỉ số có ý nghĩa khác nhau nên giữ Views là thiếu.
- `Thời gian xem` trên bảng là số nguyên không ghi đơn vị; chi tiết hiển thị thời lượng theo giờ và phút. Bản đầu giữ raw với missingReason `unit_unverified`, không suy giây. Average watch time có đơn vị `giây` được hỗ trợ.
- Ô reactions/comments/shares có thể chứa thêm `0 từ quảng cáo`; chỉ nhận cấu trúc tổng + dòng paid đã quan sát, không cộng hai giá trị.
- Ngày nguồn có thông báo **Giờ Thái Bình Dương**. Ngày đăng thiếu năm/giờ/timezone phải giữ nhãn gốc; ngày đầy đủ DD/MM/YYYY có thể dùng lọc ngày, không giả timestamp UTC.
- Ba notice đã quan sát: “Chúng tôi đã chuyển phần Mục tiêu sang tab Kết quả” → “Hiển thị sau”; “Khám phá kế hoạch hàng tuần mới” → “Bỏ qua”; “Tab Bài viết & tin giờ có tên là Nội dung” → “Bỏ qua”. Chỉ đóng khi heading và nút trong cùng dialog khớp.

Raw chẩn đoán chỉ ở `.tmp/fba-probe/out` (không commit, chứa nội dung Page). Fixture regression phải rút gọn và dùng dữ liệu tổng hợp.

## Baseline trước sửa

- Web: 62/62 PASS.
- Solution Release `-m:1 --no-restore`: PASS, 1 warning CS8604 cũ ở smoke lifecycle. Build graph mặc định thất bại 0 diagnostic, dùng build tuần tự đã có bằng chứng.
- FinalSchema smoke v10 PASS; Facebook application 170 PASS (gồm thay đổi đang có từ task khác).
- CodeGraph đã thử nhưng không trả kết quả, dừng và đọc trực tiếp source. Bảo toàn working tree sẵn có.

## Điều chỉnh so với plan

FBA0 đạt Page discovery + Reel thật trên Page đã chọn + bài thường trên một Fanpage khác. Chọn **bảng nội dung đã đăng/Trọn đời** thay vì grid Insights có kỳ 28/90 ngày. Metric thiếu và raw chưa rõ đơn vị giữ nguyên. Bản đầu dùng JSON có cấu trúc cố định cho payload metric từng dòng (có value/raw/approximate/missingReason); ID/capture/Page và index/FK vẫn là cột thật, không tạo engine EAV. Browser/UI/DB integration sau đó cần evidence riêng, không suy từ probe này.

## Kết quả implementation

| Hạng mục | Kết quả |
|---|---|
| FinalSchema | PASS: schema/model parity, DB mới v11, nâng từ v7/v8/v9/v10; fixture v10 cố định, giữ AppSettings/dữ liệu cũ, backup giữ v10, rollback CREATE khi xung đột; version tương lai bị từ chối. |
| Analytics smoke | **29 PASS**: SQLite thật, null/0, raw/xấp xỉ, không cộng paid, không đoán đơn vị, account/Page isolation, replay operation, capture mới không cộng lịch sử, lỗi/cancel giữ dữ liệu cũ, double-click, cancel sai scope, recovery, account tắt, browser budget, bridge camelCase/allow-list. Edge fixture: checkbox role ngầm, nhãn zero-width, UI thay đổi, discovery asset_id khác public ID, shared profile lock. |
| Regression đăng Facebook | **180 application PASS**, gồm ca Analytics Running giữ bài cùng profile Queued. **37 browser fixture PASS**, không đăng thật. |
| Web | **67/67 PASS**; bốn test Analytics riêng và navigation bổ sung, giữ stale-response/account isolation và không auto sync. TypeScript/Vite PASS. |
| Build | Full solution Release `-m:1 --no-restore` PASS, lần incremental cuối 0 warning/error; clean baseline có warning CS8604 cũ. Desktop Release chuẩn đã chép UI mới vào wwwroot. |
| Visual | Edge ẩn dùng dữ liệu tổng hợp, Light/Dark ở 800/1000/1280/1440 px: không tràn document, account/Page cao 40 px và không cắt; dialog/history/Escape PASS. Đã xem ảnh rộng Dark và hẹp Light. |
| Adapter live | Discovery **3 Page**; Fanpage người dùng chọn đọc **7 nội dung (2 Reel, 5 bài)**, đúng ID/nhãn ngày. Views thiếu cả 7, average watch có đơn vị giây trên 2 Reel; các giá trị thiếu giữ null. Partial, không giả đầy đủ. |
| Service live + persistence | **2 lần cập nhật PASS** qua application service + adapter thật, kết quả mỗi lần 7 nội dung Partial; history tăng đúng 1 mốc/lượt (bản sao đã có 1 mốc ban đầu → 2 rồi 3). SQLite bootstrap v11 trên bản sao, DB nguồn luôn ReadOnly; không sửa DB gốc hoặc đăng bài. |
| WebView2 thật | WPF window ẩn + WebView2HostManager + FacebookAnalyticsHandlers thật + SQLite fixture: **React→bridge→DB trả 7 dòng; dialog/history PASS**. Fixture không có browser Facebook thật/queue đăng. Ảnh nội bộ `.tmp/fba-webview.png` đã xem. |

Sandbox chặn Edge launch và formatter named pipe; các lượt tương ứng đã chạy lại ngoài sandbox và PASS. Không thay cờ che automation. Có lúc profile thật bị khóa bởi tiến trình khác, probe tự dừng; không kill/đóng tiến trình người dùng. Các lỗi adapter ban đầu (DTO Playwright cần constructor rỗng, role checkbox ngầm, metadata tải muộn) đã sửa và kiểm lại live.

## Giới hạn còn mở

- **NOT_RUN:** toàn luồng click Cập nhật trong app cá nhân rồi đồng bộ live; thay vào đó đã kiểm riêng service/live/bản sao DB và WebView2/bridge/fixture. Không tự mở app production hoặc nâng DB cá nhân.
- **NOT_RUN:** DPI Windows 125%/150% trên WebView2, mất quyền/checkpoint live và Fanpage tiếng Anh. Hiện chỉ hỗ trợ nhãn VI đã quan sát; không tự đổi ngôn ngữ tài khoản.
- Giới hạn 50 nội dung và lazy load có thể dừng sớm; kết quả ghi rõ phạm vi, không hứa thu đủ Fanpage. Không xác nhận số followers, retention hoặc tổng thời gian xem quy đổi.
- Tổng KPI chỉ cộng cùng capture/tập bộ lọc; lịch sử không dựng số liệu theo ngày. Ngày đăng thiếu năm giữ raw và bị loại khỏi lọc ngày. Raw/precision/missingReason giữ ở payload có cấu trúc; không truyền cookie/token/path profile lên UI.
- Snapshot không tự cắt lịch sử DB; history response giới hạn 25 mốc và danh sách capture 20. Chưa có chính sách dọn capture theo dung lượng.
- Lỗi lưu DB giữ dữ liệu cũ và không chạy browser lại; nếu không ghi nổi trạng thái cuối thì UI báo lượt gián đoạn, mở lại app để recovery. Không báo thành công khi chưa lưu.

Không commit/push. Giữ các thay đổi nhóm/cá nhân/theme sẵn có. Lần mở Release tiếp theo trên DB v10 sẽ backup `pre-facebook-analytics-v11` trước nâng cấp cộng.
