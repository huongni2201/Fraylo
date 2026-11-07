# Facebook Analytics — Fanpage và Reels

Ngày: 2026-11-06 (UTC+07). Trạng thái: **ĐÃ TRIỂN KHAI CỤC BỘ — Facebook live qua service/bản sao DB và WebView2 fixture đã kiểm chứng; DPI và toàn luồng trên app cá nhân chưa nghiệm thu**.

**Bổ sung yêu cầu 2026-11-07, đã triển khai cục bộ:** [AI phân tích Analytics](20261107-analytics-ai-analysis.md) mở phần AI nhận xét sau khi đã thu thập/lưu số liệu, riêng từng Fanpage và tự gửi sau sync hợp lệ. Phần này thay giới hạn “chưa đưa AI nhận xét vào bản đầu” của phạm vi thu thập bên dưới cho giai đoạn tiếp theo; Nghiệm thu AI live vẫn mở.

Người dùng đã yêu cầu triển khai và chọn nghiệm thu một Fanpage thử nghiệm. Owner: Codex coordinator, triển khai tuần tự và giữ mọi sửa đổi sẵn có; không điều phối Antigravity hoặc agent con. [Evidence và giới hạn hiện hành](FACEBOOK_ANALYTICS_EVIDENCE.md) thay thế các giả định chưa kiểm chứng trong phần thiết kế ban đầu bên dưới.

## Kết quả triển khai và cách dùng

- Điều hướng cập nhật ngày 2026-11-07 theo yêu cầu người dùng: mở bản Release mới → Facebook → **Đăng bài Page → Analytics** → chọn tài khoản/Fanpage. Analytics là tab bên cạnh Đăng bài trong cùng màn hình, không còn mục sidebar/route riêng. Chỉ tải module/catalog khi mở tab lần đầu; giữ account/Page/bộ lọc và dữ liệu khi đổi tab hoặc rời/quay lại màn Page.
- **Tải Fanpage từ Facebook** chỉ tải danh mục; **Cập nhật số liệu** đọc nội dung qua browser ẩn, có Dừng cập nhật. Mở trang/Cập nhật trạng thái chỉ đọc dữ liệu cục bộ.
- Bề mặt được xác minh là **Business Suite → Nội dung → Đã đăng → Trọn đời**, tiếng Việt. Tối đa 50 nội dung/lượt; dừng quét sớm hoặc metric thiếu được thông báo, không coi là tổng toàn Fanpage. Không dùng cửa sổ Insights 28 ngày làm lifetime.
- Schema v11 nâng cấp cộng từ v7/v8/v9/v10, backup/transaction/rollback; giữ dữ liệu. Ba bảng mới và JSON payload metric có kiểu cố định; không đổi bảng TikTok/đăng nhóm. Dữ liệu kiểm live ghi vào bản sao DB; DB nguồn ReadOnly.
- Catalog/account/Page, start/cancel, dashboard phân trang và history dùng sáu command `facebook.analytics.catalog`, `.pages.sync`, `.sync`, `.cancel`, `.dashboard`, `.history`. Start trả capture Running; UI hỏi trạng thái cục bộ khi đang cập nhật. Không tạo progress event/queue riêng.
- Panel lịch sử dùng bảng các mốc thực tế, tối đa 25 mốc/nội dung; chưa vẽ biểu đồ hoặc suy số liệu theo ngày. Link nội dung có ô chỉ đọc để chọn/sao chép, không mở popup bị WebView2 chặn. Thumbnail chưa tải để tránh mở URL ngoài host.
- Các field follower/viewers/reach/retention chưa bật. Total watch time giữ raw khi đơn vị chưa được xác minh; average watch time chỉ nhận nhãn `giây` đã quan sát. Thiếu dữ liệu là null; số 0 là 0.
- Khi Analytics đang chạy, queue đăng Facebook cùng profile giữ Queued; login/browser cùng profile có khóa chung. Hết phiên/checkpoint yêu cầu xử lý trong Cài đặt. Lượt lỗi/cancel không ghi đè capture tốt trước đó, không tự thử lại.

Nghiệm thu cục bộ: schema v11/backup/rollback PASS; Analytics 29 kiểm tra PASS, Facebook application 180 PASS, browser Facebook 37 PASS; web 67/67 PASS; solution Release PASS. Visual Light/Dark 800/1000/1280/1440 PASS. Live discovery 3 Page, Page được chọn 7 nội dung (2 Reel), cả hai lần service sync Partial và lịch sử tăng đúng; WebView2 thật React→bridge→SQLite hiển thị 7 dòng và dialog/history PASS trong fixture cô lập. Bằng chứng chi tiết và NOT_RUN tại evidence.

## 1. Mục tiêu và phạm vi

Thêm tab **Analytics** trong **Facebook → Đăng bài Page** để chọn tài khoản quản lý, chọn Fanpage, đọc hiệu quả bài/Reels và xem lịch sử số liệu ngay trong Fraylo. Dùng được cả với nội dung đăng ngoài Fraylo; không phụ thuộc tính năng đăng Page/video hiện chưa triển khai.

**Bản đầu:**

- Chọn tài khoản Facebook đã có trong Cài đặt, tải danh sách Fanpage tài khoản có quyền xem Insights; lưu danh sách để mở lại không gọi Facebook.
- Chọn một Fanpage, bấm **Cập nhật số liệu**. Browser chạy ẩn qua profile Fraylo; chỉ login thủ công mới hiện browser.
- Thu thập tối đa **50 nội dung gần nhất** mỗi lần, gồm bài thường và Reels nếu giao diện đã được xác minh. Đây là giới hạn đề xuất cho bản đầu, cần điều chỉnh sau thử nghiệm; hiển thị rõ phạm vi đã quét.
- KPI từ tập nội dung đã đọc; bảng nội dung, sắp xếp theo chỉ số, xem chi tiết, lịch sử từng nội dung, thời điểm cập nhật và độ đầy đủ.
- Lưu SQLite cục bộ; mở lại app xem dữ liệu đã lưu được khi offline. Không tự đồng bộ lúc mở trang/app.

**Chưa đưa vào bản đầu:** nhóm, trang cá nhân, Instagram, quảng cáo/Ads Manager, doanh thu, inbox, tự đăng/sửa/xóa bài, lịch đồng bộ tự động, AI nhận xét nội dung, nhập/xuất CSV, biểu đồ retention chi tiết. Các phần này không là điều kiện hoàn tất bản đầu.

## 2. Cơ sở hiện tại và khác plan cũ

Đọc cùng [ENGINEERING_RULES](../agents/ENGINEERING_RULES.md), [STATUS](../agents/STATUS.md), [TASKS](../agents/TASKS.md), [SOURCE_PLAN §9](SOURCE_PLAN.md#9-tiktok-analytics), [UI_THEME_PLAN](UI_THEME_PLAN.md), [UI_BROWSER_PLAN](UI_BROWSER_PLAN.md), [SETTINGS_PLAN](SETTINGS_PLAN.md).

Đối chiếu source tại HEAD `e1721aa` cùng working tree đang có thay đổi, ngày 2026-11-06:

| Hiện trạng | Hệ quả |
|---|---|
| Analytics UI/API/DTO và `AnalyticsSnapshot` hiện phục vụ TikTok, có Overview/Content/Viewers. | Giữ đường TikTok; tạo module/contract Facebook riêng. Không ép số liệu Facebook vào trường likes hoặc dữ liệu affiliate TikTok. |
| Facebook đã có tài khoản/profile, probe, browser nhóm, đăng nhóm và scheduler. | Dùng lại đăng nhập và tài nguyên browser; Analytics chỉ đọc, không đưa vào hàng đợi đăng. |
| `FacebookTarget` hiện chứa các đích nhóm; chưa có danh mục Fanpage Insights. | Danh mục Page Analytics riêng, không giả Page thành nhóm hoặc suy quyền quản trị từ tên/link. |
| `FacebookPoster.ProfileOperations` đang khóa thao tác theo profile; host có quản lý login/browser budget. | Analytics phải tham gia cùng cơ chế sở hữu profile với đăng nhóm/login, không tạo khóa riêng không phối hợp. |
| `CurrentSchemaVersion = 10`; có nâng cấp cộng các phiên bản Facebook trước đó. | Thêm schema bằng nâng cấp cộng, backup/rollback và bảo toàn dữ liệu. Chốt version trống tiếp theo ngay trước sửa. |
| `App.tsx` đã chuyển nền tảng TikTok/Facebook; composition root ở `MainWindow.xaml.cs`. | Analytics nằm trong tab của màn Đăng bài Page theo yêu cầu 2026-11-07; giữ trạng thái từng nền tảng. |

CodeGraph được ưu tiên nhưng không trả kết quả trong lượt khảo sát; tiến trình tra cứu đã dừng và chuyển sang đọc trực tiếp các file liên quan. Chưa chạy build/test baseline vì lượt này chỉ lập plan; số PASS trong STATUS là evidence lịch sử, không phải kiểm chứng mới.

Plan này bổ sung Facebook Analytics ngoài phạm vi TikTok của SOURCE_PLAN §9/T25 và mở nhu cầu Analytics đã được để sau trong [Facebook publishing](FACEBOOK_PUBLISHING_PLAN.md). Không mở rộng đăng Page/Reels hoặc thay đổi hành vi đăng nhóm của [FB3](20261106-facebook-posts-tab.md).

## 3. Cách lấy dữ liệu và cổng kiểm chứng

**Chọn browser đọc giao diện được cấp quyền làm hướng bản đầu.** Không phụ thuộc nút Export, không cần đăng ký Meta app/token ngay từ đầu. Meta Business Suite hoặc phần Insights của Page/nội dung là các bề mặt cần khảo sát; chỉ hỗ trợ bề mặt thực sự quan sát được trên tài khoản thử.

Các hướng khác:

| Hướng | Vai trò |
|---|---|
| Đọc Insights bằng browser | Hướng triển khai đề xuất; phụ thuộc giao diện, ngôn ngữ và quyền thực tế. Phải qua cổng FBA0 trước khi chốt parser. |
| Nhập báo cáo Facebook xuất ra | Có thể bổ sung nếu Fanpage có Export; không giải quyết tài khoản thiếu nút này nên chưa làm trong bản đầu. |
| Meta API chính thức | Cân nhắc riêng nếu cần đồng bộ quy mô lớn; phải xác minh quyền, loại Page, metric và quy trình ứng dụng ở thời điểm triển khai. Plan không cam kết endpoint/quyền API. |

**FBA0 bắt buộc trước code thu thập:** dùng đúng đường browser chạy ẩn/profile của Fraylo, kiểm một Fanpage có quyền Insights và một Reel thật. Ghi nhận Page ID, nội dung ID, nhãn chỉ số, đơn vị, phạm vi thời gian và cấu trúc truy cập; chỉ đọc, không đăng hoặc thay cài đặt Facebook.

- Chưa có profile/Fanpage phù hợp: ghi `NOT_RUN`, nêu chính xác thiếu gì; có thể chốt phần thiết kế độc lập nhưng không coi adapter sẵn sàng.
- Nếu chỉ có số công khai, không có Insights: báo **Chưa có quyền xem số liệu chi tiết**, không đánh dấu đủ dữ liệu và không mặc định thay bằng crawler công khai.
- Nếu chạy ẩn bị chặn/không xác minh được Page hoặc chỉ số: dừng capability tương ứng; không chuyển sang browser hiện hoặc đoán selector.
- Chỉ hứa hỗ trợ ngôn ngữ đã có evidence. Ưu tiên giao diện tiếng Việt; tiếng Anh bổ sung khi đã quan sát. Không tự đổi ngôn ngữ Facebook.
- Evidence rút gọn, loại cookie/token/thông tin không cần thiết; không lưu toàn bộ trang/tài khoản vào repo.

## 4. Chỉ số và cách diễn giải

“Cần cho bản đầu” là mục tiêu sản phẩm, không là khẳng định Facebook chắc chắn cấp chỉ số. FBA0 chốt khả năng từng bề mặt; trường không đọc được giữ null và lý do.

| Nhóm | Dữ liệu | Mức ưu tiên |
|---|---|---|
| Fanpage | Page ID, tên, link; số follower tại lúc đọc nếu hiển thị | ID bắt buộc để phân tách; follower tùy khả năng |
| Nội dung | Post/Reel ID, link chuẩn, loại nội dung, caption/tiêu đề, ngày đăng; thumbnail và thời lượng nếu có | Cần cho bản đầu; không có ID tin cậy thì bỏ dòng và báo thiếu |
| Hiệu quả | Views, tổng reactions, comments, shares | Mục tiêu bản đầu, mỗi trường có thể thiếu độc lập |
| Chất lượng xem | Average watch time, total watch time | Tùy khả năng; chỉ hiển thị sau xác minh ý nghĩa/đơn vị |
| Phân phối | Viewers/unique viewers/reach, follower/non-follower | Để sau hoặc mở khi FBA0 xác minh; mỗi khái niệm giữ mã và định nghĩa riêng |
| Dấu vết | Capture ID, lúc đọc, nguồn/bề mặt, metric definition, phạm vi, completeness | Bắt buộc |

Các quy tắc bắt buộc:

1. **Lifetime khác period.** Số tích lũy trên Reel không gọi là “lượt xem 7/30 ngày”. Bản đầu lọc theo **ngày đăng nội dung**, hiển thị “Số liệu tích lũy cập nhật lúc …”. Nếu sau này lấy được báo cáo theo kỳ, lưu rõ ngày bắt đầu/kết thúc và timezone nguồn.
2. **Views khác viewers/reach.** Không đổi tên, cộng lẫn hoặc gộp Page-level với content-level; không cộng reach của nhiều bài thành số người duy nhất của Page.
3. Null/không có quyền/không hỗ trợ/lỗi đọc khác 0. Giữ trạng thái từng metric. Nếu nguồn chỉ hiện `1,2K`, giữ nhãn gốc và độ chính xác xấp xỉ; không coi đó là số đếm chính xác. Sai định dạng để null kèm lỗi, không đoán locale.
4. KPI tổng dùng đúng một capture và chỉ cộng metric tương thích trong các nội dung của capture đó. Nếu thiếu metric, ghi **Tổng từ N/M nội dung có số liệu**; không ghi tổng toàn Fanpage. Không cộng hai capture với nhau. Có thành phần xấp xỉ thì tổng cũng phải ghi xấp xỉ; không tăng độ chính xác so với nguồn.
5. Reactions không tự đổi thành likes. Không tính trung bình cộng average watch time của nhiều Reel; bản đầu hiển thị theo từng Reel. Không dùng reactions/comments/shares để suy số người tương tác duy nhất.
6. Bản đầu chưa tính engagement rate, completion rate hoặc retention từ số thiếu. Bổ sung công thức sau phải công bố tử/mẫu và định nghĩa nguồn.
7. Lịch sử là các **mốc quan sát**, không tự dựng dữ liệu ngày trước lần thu thập đầu. Chênh lệch hai mốc là “Thay đổi giữa hai lần cập nhật”, không gọi là lượt xem chính xác trong ngày. Số giảm vẫn giữ và giải thích có thể do điều chỉnh nguồn; không ép về 0.
8. Một Reel không xuất hiện trong lần quét giới hạn không đồng nghĩa đã xóa. Không xóa dữ liệu lịch sử hoặc lấy metric cũ lấp vào capture mới mà không ghi nguồn/thời điểm.

## 5. Luồng người dùng

1. Facebook → **Đăng bài Page → Analytics** → chọn tài khoản → chọn Fanpage đã lưu. Chưa có Page: bấm **Tải Fanpage từ Facebook**.
2. Mở trang chỉ đọc SQLite. Có dữ liệu cũ thì hiển thị ngay cùng thời điểm cập nhật; không tự mở browser.
3. Bấm **Cập nhật số liệu**: xác minh tài khoản/profile/Page, lấy slot browser và chạy thu thập một Page. Giao diện hiển thị các bước thật: kiểm phiên, mở Insights, đã đọc N nội dung, đang lưu.
4. Có nút Hủy; không tạo phần trăm hoặc tổng số nội dung giả. Bấm lặp không tạo hai lượt cùng Page/profile.
5. Kết thúc: thông báo đủ trong phạm vi yêu cầu hoặc một phần, thời gian cập nhật và lý do thiếu. Lỗi mới không xóa kết quả thành công cũ; UI tách lần thử gần nhất với dữ liệu đang xem.
6. Click một nội dung mở panel chi tiết trong Fraylo, xem chỉ số, dữ liệu thiếu và các mốc quan sát. Link Facebook chỉ mở khi người dùng chủ động.
7. Hết phiên/checkpoint: hiển thị CTA sang **Cài đặt → Tài khoản → Facebook**. Analytics không tự mở login, không vượt CAPTCHA.

Khu chính gồm bộ chọn tài khoản/Page, nút tải lại Page/cập nhật, KPI, bảng bài/Reels và panel lịch sử. Bộ lọc ngày đăng 7/30/90 ngày hoặc tất cả **trong tập đã thu thập**, lọc loại, tìm caption, sort metric; null nằm cuối. Có phân trang dữ liệu cục bộ; không tải toàn bộ lịch sử qua bridge trong một response.

Đổi account/Page hoặc rời route không áp response muộn sang Page mới; giữ lựa chọn/dữ liệu cục bộ khi đổi nền tảng. UI theo token hiện tại, Light/Dark/System, bàn phím, viewport 1280×800/1000×700 và DPI 125%/150%. Không đưa tên selector/browser/API vào luồng sản phẩm.

## 6. Thiết kế kỹ thuật dự kiến

### 6.1 Ranh giới module

- `Contracts/FacebookAnalytics`: DTO/command riêng, IDs chuẩn hóa và allow-list; React không gửi đường dẫn profile/cookie hoặc URL tùy ý cho browser.
- `Application/FacebookAnalytics`: kiểm account → Page binding, điều phối thu thập, lưu/query snapshot, completeness và cancellation. Tái dùng account/profile registry hiện có.
- `Browser/Analytics/FacebookInsightsBrowser`: interface và adapter đọc Page/nội dung; typed input/result, không phụ thuộc React/WPF. Không sao chép cơ chế reflection/object dependencies của TikTok crawler vào module mới.
- `Infrastructure/Persistence`: các bảng/EF config riêng cho Facebook Analytics. Giữ bảng/DTO TikTok hiện hành.
- `Desktop`: handler, composition root, quyền profile/slot và kết nối browser; một nguồn ownership cho login, đăng nhóm và Analytics. Nếu cần tách khóa chung khỏi `FacebookPoster`, coordinator chỉ sửa phần tối thiểu và thêm regression đăng nhóm.
- `Web/features/facebook-analytics`: nội dung/API/types/test riêng; `FacebookPageScreen` nhúng vào tab Analytics của route `facebook-page`. Không tạo route/sidebar Analytics riêng.

Tên command đề xuất: `facebook.analytics.pages.list`, `.pages.sync`, `.sync`, `.cancel`, `.summary`, `.content.list`, `.content.history`, `.captures.list`. Chốt request/response/error/version tại FBA1 trước khi triển khai; payload phân trang và giới hạn dưới trần bridge hiện hành. Progress event mang operationId/accountId/pageId; UI chỉ nhận operation đang xem. Lệnh cancel không thể hủy operation của account/Page khác.

### 6.2 Dữ liệu tối thiểu

Ưu tiên ba bảng, không dựng engine analytics đa nền tảng hoặc mô hình EAV tổng quát:

| Bảng dự kiến | Nội dung |
|---|---|
| `FacebookAnalyticsPage` | Binding account/profile hiện có với Facebook Page ID, tên, link chuẩn, capability được xác minh và thời điểm xác minh. Unique theo account + Page ID. Một Page có thể được nhiều account quản lý; không gộp/cộng các binding. |
| `FacebookAnalyticsCapture` | Binding, operation/request ID duy nhất, started/finished UTC, nguồn, locale, definition version, phạm vi quét, count, status, lỗi/thiếu, follower tại lúc đọc nếu có. |
| `FacebookContentMetricSnapshot` | Capture, Facebook content ID/type/link, metadata, nullable metric, basis/scope và đơn vị; metadata từng metric về raw label/precision/missing reason trong JSON có cấu trúc đã chốt. Unique capture + content ID. |

Index theo binding/thời điểm capture, content ID và capture ID; FK thật. Ràng buộc capture/content giữ account/Page nhất quán tại backend/DB, không tin ID client. Giờ lưu UTC; ngày đăng không xác định giờ/timezone giữ null hoặc độ chính xác nguồn, không tự tạo giờ giả.

Capture lifecycle đề xuất: `Running → Complete | Partial | Failed | Canceled`. Tạo Running bằng transaction ngắn trước I/O. Chỉ lưu dòng có identity đã xác minh. Kết thúc lưu terminal status + tập kết quả bằng transaction ngắn; response lặp cùng operationId trả kết quả đã có, không tạo capture trùng. Kết quả sau terminal bị từ chối. Cancellation giữ capture Canceled cùng số dòng đọc được nếu đã xác minh, không dùng làm dashboard mặc định. Crash để Running thì lần mở sau đánh dấu Failed với reason `interrupted`, không tự chạy browser lại.

Dashboard mặc định lấy capture Complete/Partial mới nhất có dữ liệu hợp lệ; Partial phải có banner. Failed/Canceled không thay capture mặc định. Capture Complete nghĩa là hoàn tất **phạm vi được giao**, không phải đã thu hết Fanpage. Snapshot đã terminal là bất biến.

### 6.3 Browser, quyền và giới hạn

- Xác minh danh tính account lẫn Page ID đang xem; không chọn Page bằng tên gần giống. Có Page binding không đồng nghĩa hiện còn quyền Insights, phải kiểm lại khi sync.
- Không tự chuyển danh tính đăng bài của tài khoản; ưu tiên điều hướng trực tiếp bề mặt Insights. Nếu bắt buộc chuyển actor và không bảo đảm cô lập/phục hồi thì FBA0 ghi blocker trước triển khai.
- Dùng chung khóa profile và browser budget; profile đang login/đăng bài thì báo đang bận, không tranh navigation/context. Không giữ slot khi chờ thao tác login của người dùng.
- Giới hạn đề xuất: một sync/profile; tối đa 50 nội dung, tổng thời gian 5 phút/lượt, timeout từng bước có giới hạn; chạm ngưỡng thì Partial có lý do. Cần đo tại FBA0 để chốt ngưỡng thực tế, không biến con số này thành cam kết hiệu năng.
- Đồng bộ chỉ do người dùng bấm; lỗi/khởi động lại không tự retry. Retry đọc bằng thao tác mới của người dùng không được kích hoạt hành vi đăng/sửa/xóa.
- Hủy/timeout luôn cleanup tab/lease của operation; không đóng context còn owner khác, không phá job đăng nhóm. Không giữ SQLite transaction trong lúc browser chạy.
- Chỉ điều hướng URL Facebook HTTPS đã chuẩn hóa và được backend tạo từ ID đã xác minh; không gọi endpoint nội bộ chưa có hợp đồng, không trích token hoặc vượt hạn chế quyền.
- Thumbnail là tùy chọn; không cho React nạp URL tùy ý qua WebView2. Chốt đường preview được host kiểm soát theo cơ chế hiện có, chưa hỗ trợ thì dùng placeholder.

### 6.4 Nâng cấp SQLite

Schema dự kiến tiếp theo là v11 **nếu v10 vẫn là baseline khi bắt đầu**. Thêm bảng/index bằng file nâng cấp riêng, giữ dữ liệu Project/account/prompt/đăng nhóm/TikTok Analytics; backup qua SQLite backup API, nâng cấp trong transaction, rollback khi lỗi. Cập nhật `final-schema.sql`, bootstrap và schema fixture cố định đồng bộ. Không reset DB cho tính năng này. Không mở/nâng DB cá nhân trong lượt lập plan hoặc test tự động.

## 7. Trạng thái các lát triển khai

| Lát | Dependency đã kiểm | Deliverable và trạng thái |
|---|---|---|
| FBA0 | Profile Facebook Connected + Page có quyền | Đã kiểm discovery, bài thường, 2 Reel và Trọn đời qua đúng browser ẩn Fraylo; metric dictionary/evidence được ghi. |
| FBA1 | FBA0 | DTO riêng; 3 bảng/schema v11, nâng cấp cộng/backup/rollback, query summary/list/history. FinalSchema và SQLite application smoke PASS. |
| FBA2 | FBA1 | `Browser/Analytics/FacebookInsightsBrowser.cs`, parser VI từ DOM đã quan sát, shared profile lock, cancellation/timeouts. Fixture + adapter live PASS; không nhận Page/nội dung chưa xác minh. |
| FBA3 | FBA1 + FBA2 | `Application/FacebookAnalytics/FacebookAnalyticsService.cs`, sáu command bridge/handler và composition root. Start/cancel/replay/capture lifecycle/query phân trang, recovery và browser budget có test; service live hai lượt trên bản sao DB PASS. |
| FBA4 | FBA3 | `Web/features/facebook-analytics/` được nhúng trong `FacebookPageScreen` → tab Analytics. Cache, chọn account/Page, filter/sort, KPI, bảng, panel history và stale-response guards; UI tests, visual hai theme, WebView2 fixture PASS. |
| FBA5 | FBA4 | Build/regression/live service/WebView2 fixture đã có evidence; toàn luồng sync trong app cá nhân và DPI Windows còn NOT_RUN. Feature **IMPLEMENTED LOCALLY**, chưa tự coi mọi release gate DONE. |

Owner các file dùng chung/schema/bridge/App shell: Codex coordinator; không có worker song song được tạo trong task này. Working tree có thay đổi từ các task khác, được giữ nguyên. Test project riêng `tests/Fraylo.FacebookAnalytics.SmokeChecks/` đã thêm vào solution.

Kiểm tra tiếp khi nghiệm thu bản người dùng: mở Release sau khi tác vụ đang chạy xong, chọn Fanpage và cập nhật trên UI thật; kiểm DPI 125%/150%, mất quyền/phiên thật. Không cần tính năng đăng Page/Reels để dùng Analytics. Evidence và giới hạn ở [FACEBOOK_ANALYTICS_EVIDENCE](FACEBOOK_ANALYTICS_EVIDENCE.md).

## 8. Tiêu chí hoàn tất bản đầu

1. Chọn đúng account và Page; đọc được tập bài/Reels đã xác minh bằng browser ẩn, không phụ thuộc nút Export.
2. Danh sách/chi tiết lưu bền và mở offline; không gọi Facebook chỉ vì mở màn hình.
3. Views/reactions/comments/shares có số khi nguồn cấp; thiếu hoặc không đọc được có nhãn đúng, không thành 0. Watch time chỉ có khi được xác minh.
4. Không nhầm lifetime/period, views/viewers, reactions/likes; KPI ghi rõ phạm vi/độ phủ, không tuyên bố tổng toàn Fanpage từ 50 bài.
5. Hai lần cập nhật giữ lịch sử, không nhân đôi capture, không dựng lịch sử trước lần thu thập đầu.
6. Sai account/Page/quyền/phiên bị chặn; hủy/lỗi/crash không làm mất capture trước hoặc ảnh hưởng đăng nhóm/TikTok.
7. Nâng cấp giữ dữ liệu có bằng chứng SQLite; UI, build/regression và kiểm Facebook/WebView2 thật có kết quả riêng.

## 9. Nguồn và giới hạn xác minh

- [Meta: Insights và số liệu Reels](https://about.fb.com/news/2023/11/helping-creators-test-content-and-earn-rewards/amp/) — mô tả professional dashboard, distribution và retention; là cơ sở khả năng sản phẩm, không chứng minh selector hoặc bộ chỉ số hiện tại của Page cụ thể.
- [Meta: phân tích Insights trong Business Suite](https://www.facebook.com/business/help/397273916756139) — có hướng dẫn xuất báo cáo; khả năng Export và truy cập trợ giúp phụ thuộc phiên/tài khoản. Nội dung tìm kiếm được đối chiếu ngày 2026-11-06; trang đầy đủ có thể yêu cầu login.
- Mọi URL/selector, metric availability và Page permission thực tế phải được FBA0 xác minh. Plan không coi tài liệu công khai hoặc các thử nghiệm đăng nhóm trước đó là evidence Facebook Analytics đã hoạt động.
