# Fraylo — Đăng bài Facebook (Page, Group, trang cá nhân)

Ngày: 2026-11-06. Trạng thái: **ĐÃ TRIỂN KHAI CỤC BỘ luồng đăng nhóm chữ/ảnh; chưa nghiệm thu đăng thật vào nhóm.** Plan này không cấp quyền thực thi; mỗi giai đoạn cần lệnh trực tiếp của người dùng. Thay thế bản nháp plan Facebook gửi trong chat; phần khác bản nháp nằm ở [§3](#3-khác-bản-nháp-ban-đầu-và-lý-do).

> Cập nhật hiện hành: [tab Đăng bài nhiều bài/schema v10](20261106-facebook-posts-tab.md). Theo yêu cầu mới, bỏ quota 20 lượt/24 giờ; giữ tối thiểu 5 phút xuyên bài trên cùng tài khoản. Mô tả D5/điều hướng cũ bên dưới đã được phần cập nhật này thay thế. Bằng chứng mới và NOT_RUN xem plan/handoff.
> Cập nhật đăng cá nhân 2026-11-06: adapter giai đoạn A/D đã nối vào UI/hàng đợi hiện hành; chữ/link/ảnh, hẹn giờ; Nháp/Lịch sử hiện dùng chung toàn Facebook theo cập nhật 2026-11-07. Xem [handoff cá nhân](FACEBOOK_PERSONAL_HANDOFF.md). Page/video chưa triển khai. Không đổi schema cho lát này.

**Mục tiêu:** đăng bài chữ/link, ảnh, video lên Page, Group và trang cá nhân bằng Playwright, nhiều đích một lần, đăng ngay hoặc hẹn giờ, kết quả từng đích độc lập, không đăng trùng.

> **AI viết lại bài nhóm — 2026-11-07:** [đã tích hợp cục bộ/schema v14](20261107-facebook-group-ai-rewrite.md). Form nhóm/hẹn giờ có Viết lại nội dung hoặc Đăng nguyên bản, lưu lựa chọn cùng nháp. Chọn nguyên bản giữ chữ/ảnh và bỏ AI; chọn viết lại cho bài mới có chữ tạo tối đa10 biến thể/job trong queue AI chung, lưu nội dung riêng vào từng task trước khi đăng; toàn bài chờ đủ nội dung hợp lệ. Bổ sung có chủ đích cho D3: chỉ được thay TextSnapshot một lần ở bước AI trước Preparing, sau đó bất biến. Giữ pacing, snapshot media, lịch, đăng cá nhân và chính sách không tự retry; không đặt bài Facebook vào Job video. Chỉ bước AI dùng task type riêng FacebookContentRewrite không có Project, cùng account pool/budget với video/Analytics. Qualification live của tính năng mới còn mở.

**Kiến trúc:** khu vực Facebook riêng (chọn nền tảng ở sidebar), dữ liệu và luồng đăng riêng (bảng SQLite mới, scheduler riêng); dùng lại browser/profile, Asset/Library, SQLite bootstrap, bridge và mẫu CAS/lease của Fraylo. Không sửa `TikTokVideoUploader` thành bộ đăng chung.

**Công nghệ:** C#/.NET, EF Core SQLite, Playwright .NET (không thêm dependency), React/TypeScript. Quy tắc nền: [ENGINEERING_RULES](../agents/ENGINEERING_RULES.md), [UI_BROWSER_PLAN](UI_BROWSER_PLAN.md) §5–6, [UI_THEME_PLAN](UI_THEME_PLAN.md), mẫu tham chiếu [TIKTOK_PUBLISHING_HANDOFF](TIKTOK_PUBLISHING_HANDOFF.md).

## Phạm vi đã triển khai theo yêu cầu 2026-11-06

Người dùng ưu tiên Nhóm Facebook và yêu cầu tải tất cả nhóm đã tham gia, chọn cụ thể/ngẫu nhiên N/tất cả để tạo lô. Luồng nhóm, UI, bridge và hàng đợi đã triển khai; [handoff nhóm](FACEBOOK_GROUPS_HANDOFF.md) ghi hành vi và giới hạn. Page/video trong bản kế hoạch dưới đây vẫn chưa triển khai. Cập nhật schema v9: hẹn giờ (bảng `FacebookTaskSchedule`, không thêm trạng thái DB; lỡ lịch = `NeedsUser` mã `schedule_missed`) và nháp lưu bền (`FacebookDraft`, một bảng, không phiên bản hóa) đã làm cho luồng nhóm, khác thiết kế §5–§6 (cột `due_at_utc`, bảng `FacebookDraftMedia`, trạng thái `Scheduled/Missed`) vì đổi CHECK trạng thái/cột cần dựng lại bảng đang chứa dữ liệu. Quyết định recovery hiện hành: không tự retry, queued sau restart chờ người dùng Tiếp tục.

## Ràng buộc chung (áp dụng cho mọi giai đoạn)

- Browser Facebook chạy **ẩn**; chỉ hiện cho đăng nhập thủ công qua CTA. Không fallback sang hiện, không che dấu automation (xem D4). Ngoại lệ hiển thị của TikTok không áp dụng.
- Không nhập mật khẩu hộ người dùng, không đọc cookie/token ra React/log/Git, không vượt CAPTCHA/checkpoint.
- Không tự retry sau khi bấm Đăng. Chỉ yêu cầu mới của người dùng mới tạo tác vụ mới.
- Nội dung, media, tài khoản, đích của tác vụ là **bản chụp bất biến** tại lúc tạo.
- Test dùng SQLite/profile/thư mục tạm riêng; fixture HTML không phải bằng chứng Facebook thật. Thiếu quyền/tài khoản thì ghi `NOT_RUN`.
- Mọi UI feature kiểm Light/Dark, bàn phím, cửa sổ hẹp. Giao tiếp và tài liệu tiếng Việt, identifier tiếng Anh.

## Hàng đợi Facebook chung — 2026-11-07

Theo yêu cầu triển khai của người dùng, sidebar Facebook có ba mục **Hàng đợi / Lịch sử / Nháp** dùng chung cho bài **Nhóm/Cá nhân**. Theo chỉnh sửa tiếp của người dùng, bỏ các nút/tab Hàng đợi/Lịch sử/Nháp khỏi cả hai màn soạn; quản lý qua sidebar Facebook. Đăng nhóm chỉ giữ Tạo bài đăng/Hẹn giờ đăng; cá nhân hiển thị form soạn. Bỏ danh sách nháp lặp trong form nhóm, giữ tên và thao tác Lưu/Cập nhật nháp. Sau khi tạo bài vẫn chuyển hàng đợi chung. Đăng Fanpage vẫn chưa được hỗ trợ, không thêm lựa chọn giả.

- Lọc nơi đăng, tài khoản, trạng thái và tìm nội dung; mỗi bài hiện đúng loại nơi đăng và tài khoản. Tạm dừng, tiếp tục, đăng ngay, đổi giờ, hủy có xác nhận và chi tiết dùng API/revision hiện hành.
- Nháp chung có tìm tên/nội dung, lọc nơi đăng/tài khoản, mở về đúng màn soạn và xóa sau xác nhận. Mở nháp giữ mã nháp, nội dung, ảnh, giờ hẹn; lưu cập nhật nháp gốc. Nháp bị xóa ở màn chung thì form đang giữ nội dung sẽ lưu thành nháp mới khi quay lại.
- Nhân bản đưa nội dung/ảnh/nơi đăng về đúng màn soạn nhóm hoặc cá nhân, bỏ giờ hẹn cũ; không tự gửi bài. Chuyển màn bình thường giữ bản nháp và bộ lọc.
- Đọc cùng dữ liệu SQLite hiện hành qua facebook.list; không đổi contract/backend/schema hay dữ liệu cá nhân. Chỉ màn hàng đợi đang mở polling mỗi 5 giây; lỗi tải giữ dữ liệu đã có, lỗi thao tác vẫn hiện sau refresh. Unknown nằm trong lịch sử và không có nút tự gửi lại.
- Owner Codex coordinator tuần tự, tự review. Dependency luồng đăng nhóm/cá nhân hiện hành có sẵn; phạm vi ghi App.tsx, appNavigation.test.ts, FacebookPage.tsx, FacebookPersonalPage.tsx, FacebookPosts.tsx, FacebookQueue.tsx, facebookQueue.test.ts, FacebookDrafts.tsx, facebookDrafts.test.ts và docs liên quan. Giữ mọi sửa đổi có sẵn, không commit/push.
- Baseline Facebook/navigation **12/12 PASS**; sau sửa targeted **13/13**, toàn web **70/70 PASS**; TypeScript/Vite và Desktop Release **PASS, 0 warning/error**. Regression gồm dữ liệu trộn nhóm/cá nhân, account filter, lỗi tải/thao tác, clone đúng form và giữ nháp. Edge headless fixture Light1280/Dark800 đã xem, không tràn ngang, lọc cá nhân PASS; ảnh .tmp/facebook-queue-light.png và .tmp/facebook-queue-dark.png. Browser từ tool không tới localhost sandbox; fixture đồng môi trường ngoài sandbox PASS.
- **Sửa UI theo ảnh tiếp theo:** bỏ các nút quản lý trùng và danh sách nháp lặp trong form; Facebook/navigation17/17, TypeScript/Vite/Desktop Release0warning/error và diff-check PASS. Visual/WebView2 sau lượt xóa nút NOT_RUN.
- **Mở rộng Nháp/Lịch sử chung:** toàn web **71/71 PASS**, gồm điều hướng sidebar, mở nháp Nhóm/Cá nhân và lưu đúng mã nháp gốc, bộ lọc/xác nhận xóa/lỗi giữ dữ liệu. TypeScript/Vite/Desktop Release0warning/error PASS. Đã xem ảnh `.tmp/facebook-drafts-light.png` / `.tmp/facebook-history-dark.png` ở800px và fixture hàng đợi Light1280/Dark800. CodeGraph vẫn unavailable từ lượt trước; đọc source trực tiếp. Giữ thay đổi FBR/originalText hiện có, sửa biến clonePost.originalText ở đúng effect. Không đổi backend/schema cho lát UI này.
- **NOT_RUN:** WebView2/DPI125–150 và tài khoản Facebook thật sau đổi UI; không đăng/sửa bài thật. Mở lại bản Release để dùng giao diện mới.

## Nhật ký và trạng thái tác vụ — 2026-11-07

- **Facebook → Hàng đợi / Lịch sử → Chi tiết → Nhật ký tác vụ** hiển thị mã lượt, thời gian, bước và mã lỗi cho từng nơi đăng Nhóm/Cá nhân. Hàng đợi hiện bước đang chạy hoặc lý do chờ gần nhất gồm giãn cách, cửa sổ đăng nhập, Analytics, lượt đăng cùng tài khoản và giới hạn trình duyệt.
- Log giữ tối đa 50 mốc mỗi lượt trong SQLite, gồm tạo/hẹn giờ, thao tác hàng đợi, kiểm tệp, mở browser, xác minh đích, soạn và đính ảnh, checkpoint trước click, kết quả và khôi phục. Tách rõ lỗi trước gửi, chờ duyệt và kết quả chưa xác định sau checkpoint. Không chèn ghi log giữa click và đọc xác nhận, không tự gửi lại.
- **Facebook → Đăng bài Page → Analytics** hiện nhật ký lượt cập nhật hiện tại và các lượt trong lịch sử. Mốc đọc dữ liệu của lượt đang chạy hiển thị trực tiếp, lưu cùng kết quả cuối. Nếu app dừng đột ngột, log Analytics giữ mốc bắt đầu và đối soát gián đoạn. Chi tiết lượt viết lại có mã job để đối chiếu với **Hệ thống → Tiến trình**.
- SQLite v15 nâng cấp có backup và giữ nguyên dữ liệu. Tác vụ đã hoàn tất trước khi nâng cấp có thể chưa có nhật ký. Mốc chẩn đoán không sao chép nội dung bài, ảnh, cookie, token hay exception thô.

## 1. Phạm vi

**Bản đầu:** nhiều tài khoản Facebook (mỗi tài khoản một profile); đích Page / Group / trang cá nhân; bài chữ+link, 1–10 ảnh, hoặc 1 video (không trộn ảnh và video trong một bài); chọn nhiều đích; đăng ngay hoặc hẹn giờ; nháp, hàng đợi, lịch sử, lỗi cụ thể.

**Để sau:** Reels, Story, bình luận tự động, analytics Facebook, lặp lịch, sửa/xóa bài đã đăng, video nhập từ ngoài Fraylo (D6).

Analytics Fanpage/Reels [đã triển khai cục bộ trong module riêng](20261106-facebook-analytics.md) theo yêu cầu mới; live và WebView2 fixture có evidence riêng. Analytics không mở rộng chức năng đăng Page/Reels trong tài liệu này; queue đăng cùng profile chờ Analytics đọc xong.

**Giới hạn cố hữu của hẹn giờ cục bộ:** Fraylo và máy phải đang chạy đúng giờ. Lịch quá hạn lúc app đóng không tự đăng muộn; hiển thị để người dùng chọn (trạng thái `Missed`).

## 2. Hiện trạng đã đối chiếu với code (2026-11-06)

| # | Phát hiện | Hệ quả cho plan |
|---|---|---|
| F1 | Bảng `Job` có `project_id NOT NULL` và `CHECK task_type IN (Script, Image, VideoPrompt, VideoRender)` (`final-schema.sql`). | Bài Facebook (chữ/ảnh, không thuộc Project) **không đi qua `Job`**. Dùng bảng riêng, chỉ mượn mẫu lease/CAS. |
| F2 | Đăng TikTok lưu journal trong `AppSettings` dạng JSON và quét bằng `Contains("\"Preparing\"")` (`TikTokPublishingService.cs:257-261`); không có hàng đợi/hẹn giờ ([bulk-publishing](20261101-bulk-publishing.md) cố ý loại). | Hẹn giờ + truy vấn hàng đợi + lịch sử cần bảng có chỉ mục thật, không bê mô hình JSON này. |
| F3 | `SqliteDatabaseBootstrapper` (`CurrentSchemaVersion = 7`): version thấp hơn → backup `pre-reset` rồi **xóa mọi bảng** tạo lại. | Thêm bảng Facebook bằng cách tăng version sẽ **xóa dự án và tài khoản đã đăng nhập** của người dùng. Xem D1. |
| F4 | `PlaywrightBrowserController.cs:232`: mọi kind **trừ TikTok** thêm `--disable-blink-features=AutomationControlled` và chạy cửa sổ thật đẩy ra `-32000,-32000`; TikTok chạy headless thật, không che. | Facebook theo nhánh TikTok. Không dùng cờ che automation. Xem D4. |
| F5 | `TikTokVideoUploader` đã có: khóa theo profile (`ProfileOperations`), kiểm danh tính live trước upload, checkpoint `beforeSubmit` trước click, `EnsureNoChallengeAsync`, hợp đồng DOM EN/VI nghiêm ngặt. | Tái dùng **mẫu** (không tái dùng code). Facebook adapter tự viết theo cùng kỷ luật. |
| F6 | Tài khoản: `ProviderAccountService.cs:131` chỉ cho `chatgpt-web/gemini-web/museai/tiktok`; `:160` map kind; `BrowserProfileKind` chỉ có `SharedAI/MuseAI/TikTok`; `AccountSettingsPanel.tsx` có tab theo provider; `IPlatformSessionProber` + `ProviderUrlResolver` quyết định probe/URL. | Thêm provider `facebook` theo đúng đường TikTok đã đi: 5 điểm chạm, liệt kê ở Giai đoạn C. |
| F7 | `Asset.project_id` nullable; `UploadAssetAsync` nhận ảnh không cần Project. Video chỉ sinh trong Project (`SaveVideoAssetAsync(projectId…)`). `TikTokPublishingService.FindReadyVideosAsync` (private) quyết định video "đăng được". | Ảnh: dùng Library/upload sẵn. Video v1: chỉ video sẵn sàng của Project; tách `FindReadyVideosAsync` thành lớp dùng chung (hành vi TikTok không đổi). |
| F8 | Sidebar `App.tsx` đã có nhóm "Sản xuất / Đăng & phân tích / Hệ thống"; trang giữ mounted bằng wrapper `block/hidden` (draft không mất khi đổi route). | Chuyển nền tảng không cần cơ chế lưu draft mới, chỉ cần không unmount. |
| F9 | Ngân sách browser `scheduler.max_open_browsers` (`SettingsService.KeyMaxOpenBrowsers`) chỉ được thi hành trong `JobReservation` (AI). Đăng TikTok không kiểm ngân sách. | Scheduler Facebook phải tự kiểm ngân sách trước khi mở profile (Giai đoạn E). |

## 3. Khác bản nháp ban đầu và lý do

| Bản nháp | Plan này | Lý do |
|---|---|---|
| Mục "Tài khoản & nơi đăng" nằm trong khu Facebook | **Đăng nhập/xác minh phiên ở Cài đặt → Tài khoản (tab Facebook mới)**; khu Facebook chỉ còn **Nơi đăng** (Page/Group/trang cá nhân) | UI_BROWSER_PLAN §1, §6 đặt login/profile ở Cài đặt; hai nơi quản lý một phiên sẽ lệch trạng thái. Khu Facebook chỉ hiển thị trạng thái phiên và CTA sang Cài đặt. |
| "Dùng lại queue" | Bảng và scheduler riêng, mượn mẫu lease/CAS/wakeup | F1. |
| Chưa nêu schema | Bảng mới + nâng cấp cộng 7→8 giữ dữ liệu (D1, đã chốt) | F3: reset schema sẽ xóa dữ liệu người dùng ngay lần cập nhật đầu. |
| Trạng thái: chờ / thành công / lỗi | Vòng đời rõ: `Scheduled, Queued, NeedsUser, Preparing, Submitting, Published, PendingApproval, Failed, Unknown, Missed, Canceled` ([§6](#6-vòng-đời-tác-vụ-và-quy-tắc-an-toàn)) | Mỗi quy tắc bắt buộc của bản nháp (Group chờ duyệt, mất kết nối sau Đăng, hết phiên, lịch quá hạn) cần một trạng thái riêng để UI/test chỉ vào được. |
| Không nói về tốc độ đăng | Giãn cách theo profile + ghi chú rủi ro tài khoản | Đăng dồn nhiều bài bằng automation dễ khiến Facebook hạn chế tài khoản; chặn ở phía app rẻ hơn mất tài khoản. |
| Giai đoạn A rồi mới B | **A và B chạy song song**, C1 (đăng nhập Facebook) làm trước để A có profile thật | B không cần bằng chứng Facebook; rút đường găng, C2 chỉ chờ A. |
| Giai đoạn A: "có bằng chứng" | A có **cổng G-A** (headless có dùng được không) và probe **dùng đúng đường launch của Fraylo** | Bằng chứng chạy bằng browser khác (browser pane, Chrome cá nhân) không chứng minh được yêu cầu "chạy ẩn". |

## 4. Quyết định thiết kế

- **D1 — Nâng cấp schema. ĐÃ CHỐT (2026-11-06): nâng cấp cộng 7 → 8, giữ dữ liệu.** Facebook chỉ **thêm** bảng. Khi `user_version == 7`, bootstrapper chạy riêng file `Sql/facebook-v8.sql` (toàn `CREATE TABLE/INDEX`) trong một transaction, backup trước như `pre-reset`, rồi đặt `user_version = 8`; DB mới vẫn dựng từ `final-schema.sql` (đã gộp bảng mới). Version khác 7 và 8 giữ hành vi reset hiện tại. Lý do: AGENTS cho phép reset khi *đổi kiến trúc*, còn đây là thêm tính năng — reset sẽ xóa dự án, prompt, tài khoản và phiên đăng nhập. Đây là chỉ dẫn mới của người dùng, thay mặc định "reset schema" của AGENTS cho riêng thay đổi này. Chi phí ≈ 15 dòng + test FinalSchema (xem Giai đoạn C).
- **D2 — Một tác vụ = một đích.** Bài chọn N đích tạo N `FacebookPostTask` cùng `batch_id`. Mỗi tác vụ có vòng đời, lỗi, kết quả riêng.
- **D3 — Bản chụp bất biến.** Tác vụ lưu `content_snapshot_json` (chữ, link), `target_snapshot_json` (loại, tên, URL, actor), cùng bảng `FacebookPostTaskMedia` (asset_id, sha256, thứ tự). Sửa nháp, đổi tên đích, đổi asset không ảnh hưởng tác vụ đã tạo. Khi chạy, kiểm lại sha256 từng tệp; lệch → `Failed(media_changed)` trước khi mở Facebook.
- **D4 — Chế độ ẩn.** `PlaywrightBrowserController` coi Facebook như TikTok (headless thật, không cờ `AutomationControlled`). Không thêm biện pháp né nhận diện bot. Nếu Facebook không cho phép headless dùng được (cổng G-A) → **dừng và báo người dùng**: theo UI_BROWSER_PLAN §5 chọn giữa tắt automation Facebook hoặc chấp nhận ngoại lệ hiển thị có chủ đích. Không tự chuyển.
- **D5 — Giãn cách và hạn mức.** Mặc định: tối thiểu **5 phút** giữa hai lần *bấm Đăng* trên cùng profile; tối đa **20 bài/24 giờ/profile**. Chưa tới hạn → tác vụ ở `Queued` với lý do "Chờ giãn cách / đủ hạn mức", không lỗi. Hằng số trong code (`FacebookPacing`); đưa lên Cài đặt chỉ khi có yêu cầu. Các số này là mặc định thận trọng, **chưa có căn cứ từ Facebook** — Giai đoạn A ghi lại mọi cảnh báo quan sát được để chỉnh.
- **D6 — Nguồn media v1.** Ảnh: Library + tải từ máy (đã có). Video: chỉ video sẵn sàng của Project (cùng quy tắc đăng TikTok). Nhập mp4 tùy ý cần thêm loại Asset, để sau.
- **D7 — Trùng nội dung.** Chặn cứng: cùng `client_request_id`, hoặc một tác vụ `Preparing/Submitting` trên profile (chỉ mục duy nhất một phần). Cảnh báo mềm (người dùng xác nhận được): cùng `content_hash` + cùng đích đã `Published/PendingApproval/Unknown` trong 7 ngày.
- **D8 — `Unknown` không có nút "đánh dấu".** Khác TikTok không cần đối soát: trạng thái chỉ báo "chưa xác định", đưa liên kết đích để người dùng tự kiểm tra, và nút **Đăng lại** (tạo tác vụ mới, cảnh báo có thể trùng bài). Giữ đúng tinh thần quyết định 2026-10-30 (bỏ đối soát) và quy tắc "không tự gửi lại".
- **D9 — Tiến trình.** Hàng đợi Facebook không gộp vào trang "Tiến trình" (job AI). Sidebar hiện badge số tác vụ đang chạy cạnh mục Hàng đợi.
- **D10 — Thông báo rủi ro.** Lần đầu thêm tài khoản Facebook, hiện một thông báo một lần: tự động hóa trình duyệt có thể bị Facebook hạn chế tài khoản, nên dùng tài khoản/đích mình sở hữu và giãn cách. Lưu `facebook.risk_ack` trong `AppSettings`. Không phải điều khoản pháp lý, chỉ thông tin.
- **D11 — Không dùng Graph API.** Cân nhắc và loại: không có API đăng lên trang cá nhân; đăng Group qua API đã bị Meta gỡ theo hiểu biết hiện tại (cần xác minh nếu muốn xem lại); Page cần ứng dụng Meta + duyệt quyền + lưu token, đi ngược mô hình local-first không token. Đổi lại Playwright chịu rủi ro giao diện Facebook đổi (giảm bằng hợp đồng DOM nghiêm ngặt như TikTok).

## 5. Mô hình dữ liệu

Tài khoản **không** có bảng mới: dùng `BrowserProfile` (kind `Facebook`) + `ProviderAccount` (`provider_key = 'facebook'`) như TikTok.

```text
FacebookTarget            -- nơi đăng
  id, profile_id → BrowserProfile (RESTRICT), kind {Profile|Page|Group}, name, url (đã chuẩn hóa),
  actor (tên danh tính đăng, vd tên Page), verification_state {Unverified|Verified|Failed},
  verification_message, verified_at_utc, lifecycle {Active|Archived}, revision, created/updated_at_utc
  UNIQUE (profile_id, url)

FacebookDraft             -- nháp (một bản đang sửa; không phiên bản hóa)
  id, text, link_url, revision, created/updated_at_utc
FacebookDraftMedia        -- draft_id → Draft (CASCADE), asset_id → Asset (RESTRICT), sort_order

FacebookPostTask          -- một đích
  id, batch_id, client_request_id UNIQUE, target_id → Target (RESTRICT), profile_id,
  content_snapshot_json, target_snapshot_json, content_hash,
  mode {Now|Scheduled}, due_at_utc, state, reason_code, message,
  attempts, cancel_requested, lease_owner_id, lease_expires_at_utc,
  submitted_at_utc,                     -- mốc ranh giới side effect
  external_post_url, activity_log_json, revision, created/updated_at_utc
  INDEX (state, due_at_utc); INDEX (profile_id, submitted_at_utc)
  UNIQUE INDEX (profile_id) WHERE state IN ('Preparing','Submitting')
FacebookPostTaskMedia     -- task_id → Task (CASCADE), asset_id → Asset (RESTRICT), sha256, sort_order
```

Ghi chú: `due_at_utc` = giờ hẹn, hoặc `created_at_utc` với chế độ Now (một trường duy nhất để quét và để xác định `Missed`). Thời gian lưu UTC ISO; UI hiển thị và nhập theo múi giờ máy. `activity_log_json` giới hạn 50 mục gần nhất (phục vụ chẩn đoán như `JobActivityLog`, không chứa cookie/URL có token).

## 6. Vòng đời tác vụ và quy tắc an toàn

```text
Now        → Queued
Scheduled  → (due_at ≤ now, đang chạy app) → Queued
Queued     → Preparing   [đủ điều kiện dispatch, xem dưới]
Preparing  → Submitting  [CAS trước click Đăng]
Preparing  → NeedsUser   [session_expired | checkpoint | captcha]   → (người dùng "Tiếp tục") → Queued
Preparing  → Failed      [identity_mismatch | target_not_found | media_changed | composer_not_found | media_rejected | …]
Submitting → Published | PendingApproval | Failed(rejected_by_facebook) | Unknown
Scheduled|Queued|NeedsUser|Missed → Canceled;  Preparing → Canceled (trước CAS)
Missed     → Queued (Đăng ngay) | Scheduled (giờ mới) | Canceled
```

**Điều kiện dispatch** (kiểm lại ngay trước khi lấy tác vụ): profile `Connected` và có danh tính; không có tác vụ `Preparing/Submitting` trên profile; qua giãn cách và hạn mức (D5); số profile đang mở dưới `scheduler.max_open_browsers` hoặc profile đã mở (F9); không có đăng nhập đang diễn ra trên profile.

**CAS ranh giới side effect** — bước duy nhất được phép chuyển sang `Submitting`, chạy trong `beforeSubmit` của adapter ngay trước click:

```sql
UPDATE FacebookPostTask SET state='Submitting', submitted_at_utc=@now, revision=revision+1
WHERE id=@id AND state='Preparing' AND cancel_requested=0 AND lease_owner_id=@owner;  -- phải trả về 1 dòng
```

0 dòng (đã hủy / mất lease / có tiến trình khác) → adapter dừng **trước** click. `Submitting` không hủy được (UI khóa nút, ghi "đã gửi lệnh đăng").

**Phân loại kết quả sau click** (bằng chứng cụ thể do Giai đoạn A chốt, ghi trong `FACEBOOK_UI_EVIDENCE.md`):
- Thấy xác nhận đăng thành công → `Published`; nếu Facebook báo bài chờ quản trị viên duyệt → `PendingApproval` ("Chờ quản trị viên duyệt", không bao giờ ghi là đã xuất bản).
- Facebook hiện lỗi từ chối rõ ràng → `Failed(rejected_by_facebook)` kèm nguyên văn thông báo.
- Mất kết nối, đóng browser, hết thời gian chờ, **hoặc không thấy bằng chứng nào** → `Unknown`. Không retry.
- Đọc URL bài là tùy chọn (như TikTok): lỗi đọc URL hoặc đóng browser sau xác nhận không đổi kết quả đã đăng.

**Khôi phục khi mở app** (`FacebookTaskRecoveryService.RunAtStartupAsync`, chạy trước scheduler):
- `Preparing` (chưa có `submitted_at_utc`) → `Queued`, `attempts+1`; `attempts ≥ 3` → `Failed(attempts_exhausted)`.
- `Submitting` → `Unknown(app_interrupted_after_submit)`.
- `Scheduled`/`Queued` có `due_at_utc` < thời điểm khởi động → `Missed`. Trong lúc app chạy, tác vụ trễ do chờ slot/giãn cách vẫn chạy bình thường.

**Kiểm tra bắt buộc trong adapter trước `beforeSubmit`:** danh tính tài khoản live khớp profile; đang ở đúng đích (URL chuẩn hóa khớp); actor trong composer khớp `target_snapshot.actor` (Page: đúng Page, không phải trang cá nhân); số/loại media đã đính kèm khớp snapshot; nội dung đọc lại từ composer khớp snapshot. Không duy nhất / không tìm thấy → dừng, không đoán selector.

## 7. Điều hướng và giao diện

Sidebar: nhóm **Sản xuất** (Dự án, Prompt, Tài nguyên) và **Hệ thống** (Tiến trình, Cài đặt) giữ nguyên. Nhóm "Đăng & phân tích" thành **Nền tảng** gồm bộ chọn `TikTok | Facebook` (segmented, có nhãn truy cập) và menu của nền tảng đang chọn:

| Nền tảng | Mục |
|---|---|
| TikTok | TikTok Analytics, Đăng video (như hiện tại, không đổi hành vi) |
| Facebook | **Nơi đăng**, **Soạn bài**, **Hàng đợi** (badge), **Lịch sử** |

- Mọi trang vẫn mounted (`block/hidden` như `App.tsx` hiện nay) nên đổi nền tảng giữ draft và màn đang mở; nhớ route cuối của từng nền tảng trong state; nền tảng đã chọn lưu `localStorage` (try/catch, mất cũng không sao).
- **Nơi đăng:** theo tài khoản Facebook (trạng thái phiên, danh tính, nút "Mở Cài đặt" khi cần đăng nhập) → danh sách đích với loại, actor, trạng thái xác minh. Thêm đích: dán URL + chọn tài khoản → "Xác minh" (chỉ đọc, không đăng). Lưu trữ (archive) thay vì xóa nếu đã có tác vụ.
- **Soạn bài:** chữ, link, media (chọn từ Library / tải ảnh / chọn video Project), chọn đích có nhóm theo tài khoản, chọn Đăng ngay hoặc Hẹn giờ. Nút tạo hiện hộp xác nhận liệt kê từng dòng *tài khoản → đích → giờ* và số tác vụ sẽ tạo; cảnh báo mềm D7 nằm ở đây. Nháp lưu thủ công, nhiều nháp.
- **Hàng đợi:** tác vụ chưa kết thúc, theo giờ. Thao tác: Hủy, Đăng ngay, Đổi giờ (chỉ giờ — nội dung bất biến), Tiếp tục (`NeedsUser`), xử lý `Missed` (banner đầu trang khi có). Làm mới 3 giây khi trang active (như đồng bộ workflow), không cần kênh event mới.
- **Lịch sử:** tác vụ đã kết thúc, lọc theo tài khoản/đích/trạng thái, hiển thị lý do nguyên văn, liên kết bài nếu có, "Đăng lại" (nạp lại vào Soạn bài; cảnh báo trùng với `Unknown`). Tải thêm theo con trỏ `updated_at_utc`, 50 mục/lượt.
- Trạng thái luôn kèm chữ; `PendingApproval` và `Unknown` dùng nhãn riêng, không gộp vào "Thành công/Lỗi".

## 8. Kiến trúc thực thi và file chạm

| Lớp | File (mới = **M**) | Trách nhiệm |
|---|---|---|
| Contract | `Contracts/Facebook/FacebookDtos.cs` **M** | DTO + hằng trạng thái/mã lý do |
| Schema | `Infrastructure/.../Sql/final-schema.sql`, `facebook-v8.sql` **M**, `SqliteDatabaseBootstrapper.cs`, `Entities/Facebook*.cs` **M**, `Configurations/Facebook*.cs` **M**, `FrayloDbContext.cs` | bảng §5, nâng cấp 7→8 (D1) |
| Browser | `Browser/Publishing/FacebookPoster.cs` **M** (`IFacebookPoster`), `Browser/Probers/FacebookPlatformProber.cs` **M**, `BrowserEnums.cs`, `ProviderUrlResolver.cs`, `PlaywrightBrowserController.cs:232` | adapter DOM, probe danh tính, kind/URL/nhánh headless |
| Application | `Application/Publishing/ReadyVideoQuery.cs` **M** (tách từ `TikTokPublishingService`), `Application/Facebook/FacebookTargetService.cs`, `FacebookDraftService.cs`, `FacebookTaskService.cs`, `FacebookPostScheduler.cs`, `FacebookTaskRecoveryService.cs`, `FacebookPacing.cs` **M**; `Accounts/ProviderAccountService.cs:131,160` | CRUD, tạo tác vụ, vòng đời, scheduler, khôi phục |
| Host | `Desktop/Bridge/Handlers/FacebookHandlers.cs` **M**, `MainWindow.xaml.cs` (composition root, cạnh `TikTokPublishingService` ~dòng 358) | allow-list lệnh, khởi động scheduler/recovery |
| Web | `features/facebook/{FacebookNav,TargetsPage,ComposePage,QueuePage,HistoryPage,facebookApi}.tsx` **M**, `App.tsx`, `contracts/bridge.ts`, `features/settings/AccountSettingsPanel.tsx` (tab Facebook) | UI §7 |
| Test | `tests/Fraylo.Facebook.SmokeChecks/` **M** (thêm vào `Fraylo.sln`), `features/facebook/*.test.ts`, `appNavigation.test.ts`, `tests/Fraylo.FinalSchema.SmokeChecks`, `tests/Fraylo.Publishing.SmokeChecks` (hồi quy TikTok) | §9 |

Giao diện chính (chữ ký cố định để các giai đoạn khớp nhau):

```csharp
public interface IFacebookPoster
{
    Task<FacebookTargetProbe> VerifyTargetAsync(FacebookTargetInput input, CancellationToken ct);   // chỉ đọc
    Task<FacebookPostResult> PostAsync(FacebookPostInput input, Func<Task> beforeSubmit, CancellationToken ct);
}
public sealed record FacebookPostResult(FacebookPostOutcome Outcome, string? PostUrl, string? Detail); // Published | PendingApproval
public sealed class FacebookNeedsUserException(string reasonCode, string message) : Exception(message); // session_expired|checkpoint|captcha
// Mọi lỗi khác ném InvalidOperationException có reasonCode; sau beforeSubmit mà không có bằng chứng → FacebookUnknownOutcomeException.
```

Lệnh bridge (allow-list, payload kiểm ở backend, React không nhận đường dẫn profile/cookie): `facebook.targets.{list,add,verify,archive}`, `facebook.drafts.{list,save,delete}`, `facebook.tasks.{create,list,cancel,reschedule,post_now,resume,resolve_missed}`, `facebook.history.list`. `facebook.tasks.create` nhận `batchRequestId` + danh sách đích; `client_request_id` từng tác vụ = `{batchRequestId}:{targetId}` nên gửi lại cùng lô là replay, không tạo thêm.

## 9. Giai đoạn, điều kiện hoàn tất và kiểm chứng

**Thứ tự:** `C1 → (A ∥ B) → C2 → D → E → F` (C1 chỉ thêm kind/provider/tab đăng nhập để A có profile thật; C2 cần bằng chứng A). Mỗi giai đoạn kết thúc bằng build 0 lỗi + test của giai đoạn đó; không chuyển giai đoạn khi cổng chưa đạt.

> **C1 đã làm (2026-11-06):** kind/provider `facebook`, URL mặc định, nhánh headless không che automation (D4), `FacebookPlatformProber` (phiên = cookie `c_user` + `xs`, không đọc DOM; danh tính tạm là `Facebook (ID số)`, tên hiển thị bổ sung theo Giai đoạn A), tab Facebook trong Cài đặt → Tài khoản. Kiểm: smoke `facebook` PASS (đọc danh tính, nhận diện login/checkpoint, tạo tài khoản → profile kind Facebook, provider lạ vẫn bị từ chối), smoke `tiktok` PASS, `tsc` + settings/nav tests PASS, Browser Release build 0 warning. **NOT_RUN:** đăng nhập và probe Facebook thật, WebView2, xem tab trên UI Light/Dark.

> **Adapter trang cá nhân đã viết (2026-11-06):** `Browser/Publishing/FacebookPoster.cs` (`IFacebookPoster`, lõi `PostOnPageAsync` có thể test trên fixture) theo bằng chứng E1–E14: đính ảnh trước rồi gõ chữ, đọc lại chữ/số ảnh/đối tượng xem trước `beforeSubmit`, `Published` chỉ khi thấy "Your post has been published", không có xác nhận → `Unknown`, hủy sớm thì `Delete draft`. Smoke `tests/Fraylo.Facebook.SmokeChecks` **14/14 PASS** trên fixture HTML (Edge headless). **Live (2026-11-06 15:40):** `FacebookPoster.PostAsync` đăng thật 1 bài chữ + 1 ảnh, `Only me`, chạy ẩn → `Published`, `beforeSubmit` đúng 1 lần; bài đã chuyển vào thùng rác. **NOT_RUN:** Page, Group, video, bản tiếng Việt, `PendingApproval` thật (regex chờ duyệt là giả định), kết nối vào service/bảng tác vụ (Giai đoạn D–E).

### A. Xác minh Facebook thật (chỉ quan sát, chưa có tính năng)

Việc: console probe tạm trong `.tmp/facebook-probe` (không commit), tham chiếu `Fraylo.Browser` để dùng **đúng** `IBrowserProcessController` + cấu hình headless của D4; người dùng đăng nhập thủ công bằng cửa sổ login của Fraylo (profile kind `Facebook`, thêm sớm ở bước C1 để có profile thật). Với từng loại đích (trang cá nhân, Page, Group) dùng tài khoản/đích **do người dùng sở hữu dành cho kiểm thử**, probe mở composer, đính media, điền chữ, đọc lại, **dừng trước nút Đăng**. Ghi `docs/plans/FACEBOOK_UI_EVIDENCE.md` (ảnh chụp AX tree đã ẩn tên/ID, locale VI và EN).

Cần ghi rõ cho mỗi loại đích: cách vào composer; đọc danh tính/actor; cách chuyển sang danh tính Page; vai trò/quyền cần có; selector theo role/tên bilingual; chặn nào xuất hiện khi chạy ẩn (đăng nhập lại, checkpoint, "Is this you?"); cảnh báo giới hạn tốc độ nếu gặp. Sau đó, **với từng bài thử thật cần người dùng đồng ý riêng** (trang cá nhân: đối tượng "Chỉ mình tôi"; Page/Group: đích thử riêng; Group bật phê duyệt bài để thu được thông báo chờ duyệt) ghi: bằng chứng thành công, bằng chứng chờ duyệt, dạng URL bài, hành vi khi upload video lớn.

**Cổng G-A (bắt buộc):** (1) chạy ẩn qua đường launch của Fraylo tới được composer của cả ba loại đích; (2) có bằng chứng phân biệt `Published` / `PendingApproval` / lỗi; (3) có cách đọc actor để so với đích. Thiếu (1) → dừng theo D4. Thiếu (2) hoặc (3) cho loại đích nào → loại đích đó **không vào bản đầu**, ghi giới hạn.

### B. Điều hướng (song song với A)

`App.tsx`: bộ chọn nền tảng + menu theo nền tảng, route Facebook là trang rỗng/placeholder có trạng thái "chưa có dữ liệu"; `FacebookNav`. **Hoàn tất khi:** `appNavigation.test.ts` có ca: (1) đổi TikTok→Facebook→TikTok giữ caption đang sửa ở Đăng video; (2) route cuối mỗi nền tảng được nhớ; (3) tác vụ đăng TikTok đang chạy không bị ngắt (component vẫn mounted); (4) tab bàn phím/aria của bộ chọn; ảnh preview Light/Dark.

### C. Tài khoản và nơi đăng

C1 (đưa lên trước A): kind `Facebook` (`BrowserEnums.cs`), `ProviderAccountService.cs:131/160`, `ProviderUrlResolver` (+`DefaultFacebookUrl`), `PlaywrightBrowserController.cs:232` (D4), `FacebookPlatformProber` (đọc danh tính theo bằng chứng A), tab Facebook trong `AccountSettingsPanel`. C2 (sau A): D1 + bảng §5, `FacebookTargetService` (chuẩn hóa URL: chỉ `facebook.com`/`www.`/`m.`/`web.`, bỏ tham số theo dõi, từ chối host khác; `Verify` gọi `IFacebookPoster.VerifyTargetAsync`), `Nơi đăng` UI, thông báo D10.

**Hoàn tất khi (test SQLite thật + fixture):** (1) thêm cùng URL hai lần cho một profile → từ chối; hai tài khoản cùng URL → hai đích riêng; (2) URL host lạ / `javascript:` / có userinfo bị từ chối; (3) xác minh sai tài khoản (không phải admin/thành viên) → `Failed` kèm lý do, không thành `Verified`; (4) hai thao tác trên một profile không mở hai owner (khóa profile); (5) FinalSchema: DB v7 có dữ liệu (dự án, tài khoản, cài đặt) → lên v8 giữ nguyên từng dòng, có backup, đủ bảng/chỉ mục Facebook; nâng cấp lỗi giữa chừng → rollback, vẫn v7 nguyên vẹn; DB mới dựng từ `final-schema.sql` có cùng tập bảng với DB đã nâng cấp; DB v9 vẫn bị từ chối như hiện nay; (6) Settings: phiên Connected ẩn nút đăng nhập, hết hạn hiện lại.

### D. Đăng một bài (tuần tự: trang cá nhân → Page → Group; chữ → ảnh → video)

`FacebookPoster.PostAsync` theo từng loại đích; `FacebookTaskService.CreateAsync` + thực thi đồng bộ một tác vụ (chưa có scheduler): CAS §6, phân loại kết quả, nhật ký. UI Soạn bài tối thiểu (một bài, nhiều đích chưa cần). Tách `ReadyVideoQuery`.

**Hoàn tất khi:** fixture Playwright (Edge headless) cho từng loại đích và loại nội dung, tối thiểu: (1) thành công → `Published` + URL tùy chọn; (2) Group chờ duyệt → `PendingApproval` (UI ghi "Chờ quản trị viên duyệt"); (3) actor sai (đang là trang cá nhân thay vì Page) → `Failed(identity_mismatch)` **không click Đăng**; (4) checkpoint/hết phiên → `NeedsUser` đúng tài khoản; (5) ngắt browser/timeout sau click → `Unknown`, không gửi lại, replay cùng request id không đăng lần hai; (6) hủy giữa chuẩn bị → dừng trước click (CAS 0 dòng); (7) sửa nháp sau khi tạo → nội dung đăng vẫn là bản chụp; (8) tệp media đổi hash → `Failed(media_changed)`, chưa mở Facebook; (9) hồi quy: `Publishing.SmokeChecks` TikTok nguyên vẹn sau khi tách `ReadyVideoQuery`. **Kèm** một lượt đăng thật cho mỗi loại đích đã qua G-A, mỗi lượt do người dùng chấp thuận; chưa chạy thì `NOT_RUN`.

### E. Nhiều đích, hẹn giờ, hàng đợi

`FacebookPostScheduler` (vòng quét 15 giây + wakeup khi tạo/hủy), `FacebookTaskRecoveryService`, `FacebookPacing`, dispatch §6, UI Hàng đợi/Lịch sử, xác nhận lô, cảnh báo D7. Trước khi viết dispatch đọc `JobReservation.cs:153-160,413` để dùng cùng nguồn đếm profile đang mở (F9).

**Hoàn tất khi:** (1) 5 đích → 5 tác vụ; một đích lỗi không ảnh hưởng 4 đích còn lại (SQLite thật); (2) hai tác vụ cùng profile chạy tuần tự, chỉ mục duy nhất một phần chặn đồng thời; khác profile chạy song song nhưng không vượt `max_open_browsers`; (3) giãn cách 5 phút/hạn mức 20 bài: tác vụ chờ, không lỗi; (4) khởi động lại giữa `Preparing` → `Queued` chạy lại an toàn; giữa `Submitting` → `Unknown`; (5) lịch quá hạn lúc app đóng → `Missed`, `Đăng ngay/Đổi giờ/Hủy` đúng; lịch tới hạn khi app đang chạy → chạy đúng giờ (đồng hồ giả lập); (6) hủy tác vụ chưa chạy; `Submitting` không hủy được; (7) `NeedsUser` trên một profile không chặn profile khác; Tiếp tục sau khi đăng nhập lại; (8) gửi lại cùng `batchRequestId` không nhân đôi tác vụ.

### F. Nghiệm thu

Luồng lỗi (mất mạng, hết phiên, checkpoint, đích bị xóa, Facebook đổi giao diện → dừng báo rõ, không bấm selector đoán); UI Light/Dark, bàn phím/focus, 1280×800, 1440×900, DPI 125/150%; **browser ẩn** trên Windows thật (không ló cửa sổ, không giành focus); dữ liệu (nâng cấp 7→8 trên bản sao DB thật của người dùng); hồi quy TikTok (Publishing smoke, analytics, điều hướng). Ghi giới hạn còn lại vào STATUS (qua coordinator) và cập nhật UI_BROWSER_PLAN §1 một dòng. Kết quả chưa chạy ghi `NOT_RUN`.

## 10. Điểm cần review kỹ (Review Focus)

Các tình huống người dùng dễ gặp nhưng bản nháp không nói, và test nào giữ chúng:

1. **App đóng đúng giờ hẹn** → `Missed`, không đăng muộn (E-5).
2. **Mở app sau khi sập giữa lúc đăng** → `Unknown`, không đăng lại (E-4, D-5).
3. **Nhiều tài khoản cùng một đích** (đích trùng URL khác profile) → hai đích độc lập, không dùng nhầm phiên (C-1).
4. **Page đăng nhầm danh tính** (đang là trang cá nhân) → dừng trước click (D-3).
5. **Tệp ảnh/video bị đổi hoặc xóa giữa lúc hẹn và lúc chạy** → `Failed(media_changed)`, không đăng bản khác (D-8).
6. **Bài rất dài / emoji / xuống dòng**: giới hạn ký tự chốt theo bằng chứng A; validate ở backend; test emoji (rune, không UTF-16) và xuống dòng.
7. **Đổi múi giờ máy / lệch giờ** giữa lúc hẹn và lúc chạy: lưu UTC, hiển thị theo máy; test giờ hẹn qua nửa đêm.

## 11. Rủi ro còn lại (không giải được bằng đọc code)

- Giao diện và cơ chế chuyển danh tính Page của Facebook, thông báo chờ duyệt Group: **chỉ có sau Giai đoạn A**. Fixture không thay thế.
- Facebook có thể phát hiện automation/headless và đòi xác minh, hạn chế hoặc khóa tài khoản; Giai đoạn A là nơi biết sớm, plan không có biện pháp né.
- Hẹn giờ cục bộ không đăng khi máy tắt/ngủ; `Missed` chỉ giảm hậu quả. Cân nhắc `request_keep_awake` khi có tác vụ trong 30 phút tới (chưa làm).
- Giãn cách/hạn mức D5 là ước lượng thận trọng, chưa phải số của Facebook.

## 12. Quyết định

- **Đã chốt:** D1 — cộng bảng 7 → 8, giữ dữ liệu.
- **Áp dụng theo đề xuất, người dùng chưa phản đối (đổi được trước Giai đoạn D):** D6 video v1 chỉ từ Project; D5 giãn cách 5 phút và 20 bài/24 giờ/profile; đăng nhập Facebook quản lý ở Cài đặt → Tài khoản (§3 dòng 1).
- **Đã xác nhận (2026-11-06):** người dùng có sẵn tài khoản/Page/Group dành cho kiểm thử. Giai đoạn A vẫn cần người dùng đăng nhập thủ công và đồng ý riêng cho từng bài thử thật.
