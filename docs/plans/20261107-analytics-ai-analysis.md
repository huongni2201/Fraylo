# Fraylo — AI phân tích số liệu TikTok và Facebook

Cập nhật 2026-11-08 (UTC+07). **ĐÃ TRIỂN KHAI CỤC BỘ** theo yêu cầu triển khai của người dùng. Owner Codex coordinator, làm tuần tự và tự review. ChatGPT Web và Gemini Web ở chế độ thử nghiệm cho tác vụ AnalyticsAnalysis. Nghiệm thu live vẫn mở.

## 1. Yêu cầu đã chốt

Người dùng yêu cầu: sau khi lấy được số liệu phân tích TikTok/Facebook, gửi cho AI phân tích. Hai lựa chọn đã xác nhận trong chat:

- **Phân tích riêng từng tài khoản TikTok hoặc từng Fanpage**, không gộp hai nền tảng.
- **Tự gửi sau khi đồng bộ thành công**, không bắt bấm nút phân tích ở mỗi lần cập nhật.

Luồng mong muốn: **Đồng bộ → lưu số liệu → đóng băng đầu vào → xếp hàng AI → lưu và hiển thị báo cáo**. Báo cáo cho biết nội dung nào hiệu quả, điểm cần cải thiện, số liệu làm căn cứ, giới hạn dữ liệu và 3–5 việc nên thử tiếp theo.

Các mục dưới ghi thiết kế và tiêu chí kiểm tra. Mục 10 ghi kết quả trên bản triển khai. Đọc cùng [STATUS](../agents/STATUS.md), [TASKS](../agents/TASKS.md), [ENGINEERING_RULES](../agents/ENGINEERING_RULES.md), [SOURCE_PLAN §9](SOURCE_PLAN.md#9-tiktok-analytics), [Facebook Analytics](20261106-facebook-analytics.md), [UI_BROWSER_PLAN](UI_BROWSER_PLAN.md), [UI_THEME_PLAN](UI_THEME_PLAN.md), [SETTINGS_PLAN](SETTINGS_PLAN.md).

## 2. Hiện trạng đã đối chiếu

Source khảo sát tại HEAD `e1721aa` cùng working tree có nhiều thay đổi sẵn có ngày 2026-11-07. CodeGraph được gọi trước tra source nhưng trả `Transport closed`; đã chuyển sang đọc trực tiếp. Không index lại hoặc chạy provider thật.

| Thành phần hiện có | Hệ quả cho thiết kế |
|---|---|
| `AnalyticsSnapshot`, `VideoAnalyticsRecord`, `TikTokAnalyticsDailyRecord`; `AnalyticsService`, `TikTokAnalyticsSyncService` | Có dữ liệu video và Overview/Viewers theo ngày; cần truy vấn đầu vào có provenance riêng cho AI. |
| Summary TikTok có thể chọn bản mới nhất theo video/ngày từ nhiều snapshot; top video hiện chỉ tải 15 dòng và không nhận cùng bộ lọc thời gian như summary | Không gửi thẳng state của dashboard cho AI; tránh gộp sai kỳ hoặc coi top 15 là toàn bộ kênh. |
| Facebook có capture Complete/Partial, metric raw/approximate/missing, phân trang 20 dòng; thu thập tối đa 50 nội dung/lượt | AI đọc toàn bộ capture đã lưu, không chỉ trang đang hiện; giữ mức đầy đủ và giới hạn 50. |
| Facebook hiện lấy số tích lũy; lọc Days là ngày đăng nội dung | Không diễn giải thành số phát sinh trong 7/30 ngày hoặc xu hướng hằng ngày. |
| ChatGPT/Gemini có text automation, hiện định tuyến Script/Image/VideoPrompt | Có thể tái sử dụng vận chuyển text nhưng chưa có tác vụ AnalyticsAnalysis; không mượn Script để sinh báo cáo. |
| `Job.ProjectId` bắt buộc; `JobService.EnqueueAsync` kiểm Project và Scene; schema chỉ có bốn loại job video | Phải hỗ trợ job Analytics độc lập trước khi nối AI. Không tạo Project/Scene giả. |
| Settings có ScriptProvider/ImageProvider/VideoPromptProvider, chưa có AnalyticsProvider | Thêm lựa chọn AI riêng cho phân tích, không ngầm dùng provider kịch bản. |
| TM đã tích hợp và còn bước nghiệm thu live | Coordinator đã nhận ownership các file dùng chung cho AIA sau khi đối chiếu checkpoint, dependency và baseline Release. |

Không đổi crawler TikTok/Facebook chỉ để phục vụ AI; không gửi cookie, profile path, credential, HTML thô hoặc database cho provider.

## 3. Phương án và phạm vi bản đầu

**Chọn queue AI hiện có + tác vụ `AnalyticsAnalysis` có phạm vi Analytics.** Giữ chung account pool, browser budget, lease/cancel, checkpoint và policy không tự retry. Bổ sung một báo cáo lưu bền liên kết với job và nguồn số liệu.

Hai hướng khác chưa chọn: gọi AI trực tiếp từ màn Analytics (dễ mất tác vụ khi đổi màn/restart và bỏ qua điều phối tài nguyên), hoặc chỉ xuất/copy số liệu để người dùng tự hỏi AI (không đáp ứng tự gửi). Không thêm API key/provider mới, dịch vụ nền ngoài app hoặc hàng đợi thứ hai.

Phạm vi:

- TikTok: tài khoản đang đồng bộ; Overview/Content/Viewers thực sự có trong snapshot vừa lưu. Nhập CSV/JSON thành công cũng dùng cùng bước hậu xử lý, tránh luồng nhập và đồng bộ có hành vi khác nhau.
- Facebook: account + Page của capture vừa hoàn tất; Post/Reel trong capture đó. Giữ tài khoản/Page là phạm vi của báo cáo dù người dùng đổi màn trước khi AI xong.
- Báo cáo bằng tiếng Việt, xem lại offline và có lịch sử. Cho **Phân tích lại** thủ công để xử lý lỗi hoặc dữ liệu đã có trước khi tính năng được bật.
- Không tự tạo/sửa prompt sản xuất, kịch bản, dự án, lịch đăng hoặc bài đăng từ báo cáo. Không xem video/ảnh, không đọc bình luận hoặc suy đặc điểm người xem chưa có dữ liệu.
- Không tự chạy AI khi mở trang, đổi bộ lọc, reload dữ liệu local hoặc khởi động app; không quét phân tích toàn bộ lịch sử cũ.

## 4. Khi nào tự gửi

1. Host hoàn tất ghi snapshot/capture vào SQLite trước. Kết quả thu thập và kết quả AI là hai kết quả độc lập.
2. Tạo yêu cầu phân tích bền vững theo nguồn vừa lưu. Một source operation chỉ tạo một yêu cầu tự động, dù event/bridge response bị phát lại.
3. TikTok có ít nhất một metric hợp lệ trong Content/Overview/Viewers thì đủ đầu vào; Content chỉ có header nhưng Overview có dữ liệu vẫn phân tích được. Facebook Complete hoặc Partial có metric hợp lệ đều được phân tích; Partial luôn ghi rõ chỉ phân tích phần đã lấy được.
4. Failed/Canceled, không có metric hợp lệ hoặc chưa commit dữ liệu: không gửi. Giá trị 0 hợp lệ không bị coi là thiếu.
5. Thiếu AnalyticsProvider/capability chưa sẵn sàng: giữ số liệu, báo **Đã cập nhật số liệu — chưa phân tích AI: …** và CTA vào Settings. Không dùng provider khác hoặc tự phân tích backlog khi đổi Settings.
6. Đủ cấu hình nhưng hết slot thì tác vụ ở `Queued`. Nếu chưa có tài khoản AI đủ điều kiện cho nhà cung cấp đã chốt, tác vụ vẫn ở `Queued` và có nút **Đến Cài đặt Tài khoản** trong Tiến trình. Khi tài khoản được xác minh và bật hoạt động, scheduler chạy lại chính tác vụ đang chờ. Nếu phiên của tác vụ đã liên kết hết hạn, xử lý qua `AwaitingUser` và đăng nhập lại theo quy tắc browser; phiên TikTok/Facebook dùng riêng để thu thập số liệu.
7. Lỗi AI/parser/timeout không làm sync thất bại và không xóa số liệu. Không tự retry sau lỗi hoặc restart theo policy NR1. Bấm Phân tích lại là hành động tường minh; không gửi lại mù khi lần trước có thể đã được provider nhận.

Phạm vi tự động là **dữ liệu của lần đồng bộ/nhập vừa hoàn tất**, không phải trang bảng, sort, search hay bộ lọc đang hiện sau đó. UI nói rõ phạm vi này. Lần phân tích lại dùng nguồn cố định được chọn từ báo cáo/lần cập nhật, không âm thầm đổi sang số liệu mới.

Đề xuất không thêm một công tắc tự gửi riêng ở bản đầu: chọn AnalyticsProvider là cấu hình cho luồng người dùng đã yêu cầu; để Chưa chọn thì không gửi. Settings/màn Analytics giải thích số liệu sẽ được gửi cho nhà cung cấp đó sau mỗi lần cập nhật có dữ liệu.

## 5. Chuẩn hóa đầu vào và giới hạn gửi

Host tạo `AnalyticsAnalysisInput` có version, platform, accountId, pageId nếu có, snapshot/capture IDs, nguồn, capturedAtUtc, kỳ dữ liệu và timezone nguồn nếu biết; danh sách metric với đơn vị, phạm vi, raw, approximate và missing reason. Không gán timezone hay đơn vị chưa xác minh.

- **TikTok:** bản tự động dùng snapshot vừa commit; không dùng hàm summary hiện đang hòa nhiều snapshot làm nguồn sự thật. Daily theo `PeriodDateUtc`, content theo thời điểm/định nghĩa nguồn; thiếu kỳ thì ghi unknown. Không trộn tổng Overview với tổng Content thành một tổng lớn hơn, không cộng lại cùng video hoặc các bản xuất chồng kỳ. Chỉ so kỳ trước khi đủ hai kỳ tương thích; bản đầu không truy vấn thêm lịch sử để cố tạo so sánh.
- **Facebook:** dùng duy nhất capture vừa commit, bảo toàn status/source/scope, raw/approximate/missing; account–Page–capture phải khớp ở backend. Không lấy số cũ lấp vào capture mới. Không cộng reach thành người duy nhất, không đổi reactions thành likes, không tự quy đổi total watch time chưa rõ đơn vị.
- Phép tính làm ở C# theo dữ liệu đủ điều kiện; ghi công thức và các thành phần. Mẫu số 0/thiếu → không tính. Tổng một phần ghi N/M; tỷ lệ tương tác chỉ có khi tử/mẫu đầy đủ, cùng scope và định nghĩa. Average watch time không lấy trung bình cộng giữa các video.
- Không suy GMV/đơn hàng/tỷ lệ chuyển đổi từ view hoặc click. Chỉ đưa metric tiền khi có nguồn và currency/scale đáng tin cậy; không mang giả định VND từ dashboard sang dữ liệu không có currency.
- Giữ ID nội bộ và caption/tiêu đề cần cho dẫn chứng; bỏ định danh người xem, cookie/token, query string nhạy cảm, HTML, đường dẫn local. Nội dung caption là dữ liệu không tin cậy, không được làm chỉ dẫn cho AI.
- Đóng băng JSON + prompt đã render + hash + provider/settings revision + prompt version trước enqueue. Sync tiếp theo hoặc đổi Settings không thay đầu vào báo cáo cũ.

**Giới hạn bắt buộc:** gửi JSON rút gọn trực tiếp, với tổng hợp trên tập đầy đủ và một mẫu có căn cứ. Kiểm `MaxInputLength` của capability, tính cả prompt/schema/hướng dẫn và dữ liệu; các text capability hiện khai báo mức 8000 nên không hứa gửi toàn bộ bảng. Trước khi gửi, tổng hợp số học trên toàn bộ tập hợp lệ, sau đó chọn mẫu nội dung xác định gồm nhóm cao/thấp và có/thiếu dữ liệu, khử trùng ID, giảm số dòng/độ dài caption cho tới khi vừa giới hạn. Không dùng riêng top views để kết luận toàn kênh.

Manifest giữ `totalRecords`, `includedRecords`, `omittedRecords`, giới hạn thu thập, cách chọn mẫu, ngày có/thiếu dữ liệu và cờ rút gọn caption. UI và AI đều thấy giới hạn; chi tiết từng bài chỉ được nhận xét cho các bài thực sự gửi. Nếu phần bắt buộc vẫn quá dài thì `InputTooLarge`, không cắt JSON/prompt giữa chừng. Bản đầu không chia nhiều lần hỏi AI rồi tự hợp nhất.

## 6. Báo cáo AI

Prompt yêu cầu phản hồi JSON có `schemaVersion` và các phần:

| Phần | Nội dung |
|---|---|
| Tóm tắt | Hiệu quả của tập dữ liệu được gửi; tình trạng đầy đủ và kỳ quan sát. |
| Điểm nổi bật | Nội dung/chỉ số cao hoặc thấp, tham chiếu `evidenceId` vào metric/dòng đầu vào. |
| Cần cải thiện | Quan sát có căn cứ; không quy nguyên nhân thành sự thật. |
| Đề xuất | 3–5 việc nên thử, ưu tiên, lý do và metric nên theo dõi; không cam kết kết quả. |
| Giới hạn | Metric thiếu/xấp xỉ, dữ liệu Partial, mẫu nhỏ, kỳ không so được và nội dung chưa được xem. |

Ví dụ hợp lệ: “Video A có tỷ lệ chia sẻ cao nhất trong tám video có đủ view/share; nên thử thêm nội dung cùng chủ đề.” Khi chỉ có caption/metrics, nhận xét về hook, dựng phim, âm nhạc hoặc nguyên nhân giảm view chỉ được nêu là giả thuyết cần thử/xem video, không khẳng định đã phân tích media.

Parser kiểm schema/version, độ dài/số mục và evidence ID tồn tại trong snapshot. Số liệu căn cứ trên UI lấy từ snapshot, không dùng số AI viết làm nguồn chính; parser không xác minh được toàn bộ con số hoặc ngữ nghĩa trong văn bản tự do. Không nhận link ngoài do AI tự tạo; chỉ mở link nội dung đã có trong dữ liệu local qua cơ chế hiện hành. Render text an toàn, không raw HTML.

Giới hạn đầu ra (cập nhật 2026-11-08): JSON tối đa 20000 ký tự; summary và mỗi chuỗi nội dung tối đa 1000 ký tự; highlights/improvements mỗi phần 0–6 mục, recommendations 3–5 mục, limitations 1–16 mục, mỗi evidence 1–4 ID hợp lệ. Summary/highlights/improvements không chứa chữ số; số gốc hiển thị qua căn cứ. Prompt `analytics-analysis-v2` công bố các giới hạn này, schema JSON vẫn là 1 và snapshot tác vụ cũ giữ nguyên.

Lưu raw response để chẩn đoán cục bộ, không log toàn bộ dữ liệu/prompt. JSON lỗi, response cắt cụt, evidence bịa hoặc sai phiên bản → `InvalidAnalysisResult`, không báo thành công hay ghi đè báo cáo tốt trước đó. Parser có thể áp dụng lại trên response đã nhận mà không gọi AI, nhưng chưa có thao tác UI để phục hồi báo cáo lỗi theo cách này; Phân tích lại tạo lượt AI mới. Chưa tự gửi prompt sửa lỗi. Parser không chứng minh mọi nhận xét đúng: UI vẫn ghi **Nhận xét do AI tạo**, cho xem căn cứ.

## 7. Queue, lưu bền và contract

### Job Analytics độc lập

Thêm `AnalyticsAnalysis` vào task types/capabilities/settings routing; `Job.ProjectId` chỉ được null cho loại này, `SceneId` luôn null và có `AnalyticsReportId` hợp lệ. Job video giữ Project/Scene validation và quan hệ như cũ. Constraint backend + SQLite phải loại bỏ tổ hợp Analytics kèm Scene/Project và video thiếu Project. Không chỉ nới nullability mà bỏ validation.

Một bảng `AnalyticsAnalysisReport` chứa phạm vi platform/account/Page, source manifest, input JSON/hash, provider và thời điểm tạo. Prompt version nằm trong snapshot của Job. `Job.AnalyticsReportId` là FK nullable, unique tới report: bắt buộc cho AnalyticsAnalysis và null cho job video. Report được tạo trước job, không tạo FK vòng. Raw response lưu trong `Job.ResultText`. Executor kiểm cấu trúc và căn cứ trước khi ghi thành công, bridge diễn giải lại từ dữ liệu đã lưu khi mở báo cáo. Trạng thái chạy lấy từ Job, lỗi chuẩn bị ghi trên report trước khi có job.

Khóa unique cho tự động dựa trên platform + account/Page + source operation/capture ID. Chọn report `PendingEnqueue` làm intent bền vững, ghi cùng transaction ngắn với nguồn; sau commit mới chuẩn hóa đầu vào và tạo job idempotent. Không thêm bảng outbox/hàng đợi khác. Nếu chuẩn bị đầu vào hoặc gọi AI lỗi sau commit, nguồn đã lưu vẫn giữ nguyên. Không có cửa sổ crash khiến sự kiện đã mất mà số liệu không còn cơ hội phân tích: report pending sau restart hiển thị chờ người dùng tiếp tục theo policy hiện hành; không tự gửi lại job Running/Failed/uncertain. Phân tích lại thủ công có request ID riêng, giữ source snapshot, không ghi đè lịch sử. AIA0 xác nhận cách nối transaction nguồn ở cả hai platform trước khi sửa code.

Rà toàn bộ consumers của `ProjectId`/task type: DTO, JobExecutionContext, scheduler/reservation, binding/tab role/capacity, history/events, retry/cancel, host result callback và UI job card. Analytics không được kích hoạt `ProjectWorkflowService` hoặc ghi Script/Scene/Asset. Chung profile AI thì cạnh tranh slot với tác vụ video qua pool hiện hành; browser chạy ẩn, chỉ login thủ công hiện cửa sổ.

Schema triển khai là v12, nâng cấp có backup từ các bản v7–v11. Đây là bổ sung chức năng, đề xuất nâng cấp có backup/rollback giữ dữ liệu Analytics/video đã có; không reset DB vì task này. Kiểm FK/check/index và schema parity sau thay đổi bảng Job, không giữ transaction trong lúc chờ mạng/browser.

### Bridge đề xuất

- `analytics.analysis.list`: platform/accountId/pageId, phân trang lịch sử.
- `analytics.analysis.get`: reportId + phạm vi account/Page để backend xác minh quan hệ.
- `analytics.analysis.start`: platform/accountId/pageId + source ID đã lưu + clientRequestId, dành cho dữ liệu cũ chưa có report; backend đọc và xác minh nguồn, không nhận số liệu từ React.
- `analytics.analysis.retry`: reportId + clientRequestId, đối soát trạng thái gửi trước khi tạo lần mới.
- Hủy dùng job cancellation có kiểm phạm vi; event job/report có correlationId và đúng account/Page. Không lấy platform/account từ một report ID do UI gửi mà bỏ kiểm quan hệ.
- Tự gửi xuất phát từ application service sau lưu nguồn, không phụ thuộc UI còn mở hoặc callback React. UI không được đưa số liệu tự tạo/raw prompt tùy ý vào lệnh tự động.

## 8. UI và Settings

Settings → AI & Prompt thêm **AI phân tích số liệu: ChatGPT Web / Gemini Web / Chưa chọn**, lưu theo revision/Lưu/Hủy hiện hành. Chỉ cho chạy khi task capability thật sự sẵn sàng; không nâng trạng thái VERIFIED chỉ vì Script chạy được.

Trong TikTok Analytics và Facebook → Đăng bài Page → Analytics thêm khối **AI phân tích** dưới số liệu, không thêm màn/sidebar mới. Khối gồm provider, nguồn/kỳ/phạm vi, lần cập nhật, trạng thái thật, báo cáo và lịch sử. Số liệu và AI có loading/error độc lập. Đang chạy có Hủy; Failed/AwaitingUser có CTA đúng nguyên nhân; dữ liệu cũ chưa có report có Phân tích dữ liệu đã lưu, report đã có cho Phân tích lại.

Giữ account/Page/bộ lọc khi chuyển tab. Response đến muộn không hiện vào account/Page mới; có thể báo hoàn tất đúng phạm vi. Capture mới bắt đầu không xóa báo cáo cũ, nhưng phải ghi “Báo cáo của lần cập nhật …” để tránh coi là kết quả mới. Job tiếp tục khi rời tab; tab ẩn không polling, khi mở lại đọc trạng thái bền vững. Empty/Partial/queued/login/failed/canceled có nhãn tiếng Việt; focus/keyboard, Light/Dark/System, layout 800–1440px và DPI125/150 cần kiểm.

## 9. Các lát triển khai và ownership

AIA0–AIA5 đã tích hợp trên workspace. AIA6 đã có kiểm chứng cục bộ và còn phần live/DPI. Checklist theo từng lát dưới đây lưu tiêu chí thiết kế. Các kết quả kiểm chứng thực tế nằm ở mục 10.

### AIA0 — Chốt contract, phạm vi Job và baseline

**Files:** plan này; `docs/agents/STATUS.md`, `docs/agents/TASKS.md`; đọc Jobs/Providers/Persistence/Settings/bridge và consumers.

- [x] Xác nhận ownership/baseline mới nhất, inventory mọi nơi yêu cầu ProjectId/task type, chốt report–job FK, transaction/outbox, keys và giới hạn payload.
- [x] Chốt input/output schema, codes/events/cancellation, provider capability AnalyticsAnalysis và chính sách Partial/restart.
- [x] Chạy baseline Analytics TikTok/Facebook, queue/provider/Settings/workflow liên quan; ghi riêng lỗi sẵn có.
- [ ] Rà evidence và thử khả năng gửi/nhận text-only bằng dữ liệu fixture trên provider định dùng nếu có môi trường; đây chỉ là probe khả thi, chưa chứng minh task AnalyticsAnalysis production. Thiếu login/capability ghi NOT_RUN, không chốt sẵn cả hai; qualification production nằm ở AIA6.

### AIA1 — Tạo snapshot và tính toán có provenance

**Files:** tạo `src/Fraylo.Contracts/Analytics/AnalyticsAnalysisDtos.cs`, `src/Fraylo.Application/Analytics/AnalyticsAnalysisInputBuilder.cs`; chỉnh query trong Analytics/FacebookAnalytics khi cần; tạo test input trong `tests/Fraylo.Desktop.SmokeChecks/AnalyticsAnalysisSmokeTests.cs` và test Facebook liên quan.

- [x] Truy vấn source ID cố định, kiểm account/Page; chuẩn hóa metric/scope/completeness và tính toán xác định.
- [x] Tạo manifest, hash, chọn mẫu đúng budget gồm prompt; không phụ thuộc top15/trang20.
- [ ] Test null/0/approximate, capture Partial, daily/content khác scope, trùng video, kỳ chồng, thiếu tiền tệ, mẫu số0 và limit; kết quả không cộng trùng hoặc gọi lifetime là daily.
- [x] Chạy test đầu vào trước AIA2; dữ liệu chưa hợp lệ không gọi provider.

### AIA2 — Job độc lập và lưu báo cáo

**Files:** Contracts/Jobs; Application/Jobs (`JobService`, `JobModels`, `JobSnapshotFactory`, `JobScheduler` và consumers AIA0 xác định); Infrastructure/Persistence (`Job`, configuration, DbContext, final-schema, bootstrap và script nâng cấp mới); tạo entity/configuration `AnalyticsAnalysisReport`; tests queue/schema/analysis.

- [x] Thêm task AnalyticsAnalysis và liên kết report, snapshot/provider routing; bảo toàn validation job video.
- [x] Lưu bền yêu cầu, chống enqueue trùng; queue dùng cùng slot/account/binding/cancel/history, tách callback kết quả khỏi workflow video.
- [x] Nâng cấp trên DB fixture/bản sao bằng SQLite backup API; giữ dữ liệu cũ, rollback lỗi, schema parity/FK/check/index.
- [ ] Test Project/Scene thiếu/sai, report khác account, hai enqueue đồng thời, crash trước/sau enqueue, restart/cancel/late result và không tự retry; chạy regression queue/workflow/schema trước AIA3.

### AIA3 — Provider, Settings và báo cáo hợp lệ

**Files:** Contracts/Settings; Application/Settings; Providers (`ProviderCapabilities`, `ChatGptWebAdapter`, `GeminiWebAdapter`, text automation nếu cần); tạo `Application/Analytics/AnalyticsAnalysisPrompt.cs`, `AnalyticsAnalysisResultParser.cs`; Settings UI/API/tests; provider/analysis smoke.

- [x] Thêm AnalyticsProvider với validation/revision/save, không fallback ScriptProvider; prompt có version, data delimiter, schema và evidence.
- [x] Text-only adapter nhận AnalyticsAnalysis, sạch conversation/job, không cần ảnh/Scene; giới hạn trước gửi, snapshot cũ giữ nguyên.
- [x] Parse/validate và lưu response trước xử lý tiếp; lỗi parser không gửi AI lần nữa hoặc ghi đè báo cáo cũ.
- [ ] Test routing/thiếu provider/capability/login, prompt injection trong caption, JSON lỗi/cắt cụt/evidence giả, Settings conflict và job video cũ; chạy trước AIA4.

### AIA4 — Tự nối sau sync/import và bridge

**Files:** tạo `Application/Analytics/AnalyticsAnalysisService.cs`; chỉnh `TikTokAnalyticsSyncService`, các đường import trong `AnalyticsService`, `FacebookAnalyticsService`; tạo `Desktop/Bridge/Handlers/AnalyticsAnalysisHandlers.cs`; composition root `MainWindow.xaml.cs`, Contracts/Bridge nếu cần, `Web/src/contracts/bridge.ts`; smoke sync/bridge/analysis.

- [x] Sau commit nguồn, tạo/đánh thức phân tích đúng một lần; không phụ thuộc màn Analytics, không rollback dữ liệu khi AI hỏng.
- [x] Complete/Partial hợp lệ được xử lý; skip Failed/Canceled/không có metric, ghi lý do thiếu cấu hình; bỏ replay tự động đã tồn tại.
- [x] Thêm list/get/start/retry/cancel/event có scope và allow-list, hỗ trợ dữ liệu cũ chưa có report, không lộ secret/raw browser state.
- [ ] Test sync hai nền tảng, import, double callback, đổi account khi đang sync, restart giữa commit/enqueue, queue hết slot và cancellation; chứng minh không gửi khi chỉ reload local.

### AIA5 — Khối báo cáo trong hai màn Analytics

**Files:** tạo `Web/src/features/analytics/AnalyticsAnalysisPanel.tsx`, `analyticsAnalysisApi.ts`, test tương ứng; chỉnh `AnalyticsPage.tsx`, `facebook-analytics/FacebookAnalyticsPage.tsx` và test hai màn; job UI card/details/progress chỉ các consumers cần hỗ trợ Analytics.

- [x] Hiển thị báo cáo/căn cứ/lịch sử và trạng thái theo Job, CTA login/hủy/phân tích lại, provider/phạm vi/Partial/mẫu.
- [x] Giữ bộ lọc, không đặt kết quả cũ lên dữ liệu mới; chống stale response account/Page, không poll tab ẩn hoặc tự tạo job khi mount.
- [x] Kiểm fixture theo hai lớp. WebView2 chạy dữ liệu đã lưu tới báo cáo qua queue và provider giả. Smoke SQLite kiểm tự tạo một job sau sync/import và giữ báo cáo khi AI lỗi. Web kiểm đổi scope/rời màn không tạo job.
- [x] Kiểm bàn phím khi mở tab Analytics, render báo cáo, Light/Dark ở 786/1266 px và trạng thái chưa có báo cáo. TypeScript/Vite và web tests PASS.

### AIA6 — Qualification và bàn giao

**Files:** plan này, evidence Analytics AI mới, STATUS/TASKS, UI_BROWSER_PLAN/SETTINGS_PLAN; tests và fixture theo findings.

- [x] Đối chiếu acceptance trên fixture và chạy solution Release trên source tích hợp.
- [ ] Nghiệm thu WebView2 TikTok với tài khoản thật, từng provider web, lỗi mạng/login live. WebView2 Fanpage fixture và giới hạn input qua smoke đã PASS.
- [x] Hồi quy workflow video, Analytics Facebook/TikTok, schema, queue/Settings, DPI 125% và restart trên fixture. DPI 150% còn mở.
- [x] Tự review, giữ evidence findings chưa đóng, đồng bộ liên kết/spec; ghi PASS/NOT_RUN và giới hạn chính xác, không DONE chỉ vì tạo file/build được.

## 10. Acceptance và trạng thái kiểm chứng

1. Một lần sync/import có dữ liệu hợp lệ tạo đúng một yêu cầu AI cho đúng tài khoản/Page, tự động sau lưu; mở trang không tạo yêu cầu mới.
2. AI nhận số liệu cố định với kỳ/scope/provenance/missing/approximate, không nhận cookie/key/profile hoặc dữ liệu tài khoản khác.
3. Input vừa capability; báo rõ mẫu và độ đầy đủ. Facebook lifetime không bị gọi là tăng trưởng theo ngày; TikTok không cộng trùng các báo cáo/video.
4. Báo cáo có nhận xét, evidence hợp lệ, giới hạn và đề xuất; response không hợp lệ không thành báo cáo thành công.
5. AI chậm/lỗi/cần login không làm mất số liệu; queue/cancel/restart/uncertain tuân policy hiện hành, không tự resend.
6. Báo cáo cũ xem được offline và không bị thay khi sync/đổi provider; đổi account/Page không lẫn báo cáo. Job Analytics không tạo hoặc sửa workflow video.
7. Nâng schema giữ dữ liệu và rollback có kiểm; Settings/bridge/UI hai theme và live qualification có evidence trước công bố capability.

**Kiểm chứng triển khai:** solution Release và TypeScript/Vite PASS. Web 69/69, TikTok Analytics 9/9, Facebook Analytics 27/27 với SQLite và dữ liệu mẫu. Luồng AI fixture qua SQLite → scheduler → adapter giả → bridge PASS, gồm cô lập scope, giới hạn đầu vào, tổng toàn tập, null/0, từ chối evidence sai, khôi phục job chưa gửi và chống enqueue trùng. Hồi quy T15 19/19, retry 9/9, recovery 12/12, provider 8/8, workflow 30/30, project-first và ReferenceVideo PASS. Schema/model parity, backup/nâng v11 lên v12, giữ job/binding/occupancy và rollback PASS. ReferenceVideo cần chạy fixture với TEMP/TMP trong workspace do sandbox chặn thao tác di chuyển tệp ở temp mặc định.

**WebView2 thật:** mở báo cáo Fanpage từ dữ liệu lưu, chạy adapter giả qua React → bridge → SQLite → queue → báo cáo. Dark 1266 px và Light 786 px ở `devicePixelRatio=1.25` đều PASS. Đã xem ảnh và kiểm khối báo cáo không tràn ngang. Fixture và ảnh ở `.tmp/analytics-ai/`.

**Nghiệm thu live:** ChatGPT Web/Gemini Web thật, WebView2 TikTok với tài khoản thật và DPI Windows 150% còn mở. DB cá nhân chưa được mở trong lượt kiểm chứng.

### Sửa `InvalidAnalysisResult` với báo cáo nhiều giới hạn — 2026-11-08

- Phản hồi người dùng cung cấp có 11 mục limitations, trong khi parser cũ chỉ nhận 1–8 và prompt không công bố giới hạn. Phát lại qua `ProductionJobExecutor` với ID căn cứ fixture: bản giữ 8 mục PASS, bản đủ 11 mục FAIL/`InvalidAnalysisResult` trước sửa. Fixture chỉ xác minh cấu trúc với ID đã biết, không thay thế đối chiếu ngữ nghĩa với snapshot cá nhân.
- Nâng tối đa lên 16, giữ nguyên văn phản hồi và công bố đầy đủ các giới hạn trong prompt v2. Không cắt bỏ giới hạn, không tự gọi AI sửa hoặc gửi lại, không đổi schema/DB hay trạng thái job thất bại cũ. Báo cáo cũ chưa tự thành công sau khi cập nhật; người dùng có thể chủ động Phân tích lại.
- Sau sửa, kiểm thử `analytics-ai` PASS: bản 8/11/16 mục được nhận; 17 mục, rỗng, URL ngoài, schema sai, ID căn cứ giả, chữ số trong summary và JSON cắt cụt vẫn bị từ chối; raw response giữ nguyên. Luồng SQLite/queue/bridge, phạm vi account, giữ báo cáo trước và không tự resend PASS. Build Release gồm TypeScript/Vite PASS; cảnh báo nullable cũ ở smoke. CodeGraph trả `Transport closed`, đã đọc source trực tiếp.
- Tự review, giữ `tools/` và sửa đổi Facebook UI xuất hiện đồng thời ngoài phạm vi. Không mở DB/profile cá nhân, không commit/push/phát hành. Chưa chạy ChatGPT/Gemini hoặc WebView2 thật sau sửa.
