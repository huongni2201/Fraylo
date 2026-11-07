# Fraylo — Làm theo video mẫu

Cập nhật 2026-11-07 (UTC+07). Owner: Codex coordinator, triển khai tuần tự, tự review. Checkpoint/kiểm chứng tại [STATUS](../agents/STATUS.md), [TASKS](../agents/TASKS.md) và [evidence](REFERENCE_VIDEO_EVIDENCE.md).

## 1. Yêu cầu đã chốt mới nhất

- Chỉ thay **Chỉ sản phẩm** bằng **Làm theo video mẫu**. Giữ nguyên **Mẫu + sản phẩm** và **Ảnh đã hoàn thiện**.
- Đầu vào: một ảnh người mẫu mặc sản phẩm và một video mẫu, gồm âm thanh có sẵn trong video.
- Giữ người/bộ đồ trong ảnh, mô phỏng động tác từ video. Người dùng đã chọn **giữ MuseAI, chấp nhận chuyển động gần đúng**.
- **Thời lượng kết quả có thể khác mẫu.** Không kéo dài/lặp/đóng băng hình chỉ để khớp thời lượng.
- **MuseAI tự lấy audio từ video mẫu, ghép và co giãn theo độ dài video mới.** Được phép xử lý tốc độ/re-encode âm thanh, ưu tiên giữ giai điệu/giọng và cao độ tự nhiên; không thay bằng bản nhạc khác. Nếu mẫu không có audio thì không thêm nhạc.
- Người dùng nghe/xem lại để duyệt. Không cam kết giống từng khung hình hoặc giữ nguyên byte âm thanh sau co giãn.

Quyết định này thay cả hai phương án thử trước đó: bắt giữ thời lượng/audio giống hoàn toàn, và chỉ cắt audio từ đầu bằng stream-copy. Các trial cũ vẫn giữ làm evidence lịch sử, không dùng làm acceptance hiện tại.

## 2. Cơ sở và khả năng đã kiểm chứng

WFS đã tách các bộ xử lý theo mode. Source có Project/Scene, job snapshot, durable queue, provider account/profile riêng và Library. Config mở rộng trong AppSettings JSON; **không đổi schema hoặc reset DB**. CodeGraph được gọi trước khảo sát nhưng transport đóng/không trả kết quả; đã đọc source trực tiếp.

- MuseAI không có direct motion transfer trong công cụ đang dùng; không công bố capability này.
- Trial ảnh + video → phân tích prompt → video gần đúng đã trả file10s/736×1056. Nguồn16.56s; đối chiếu khung hình thấy nhận diện chính và chuỗi tạo dáng→quay lưng→đi xa, khác nhịp và chi tiết.
- Trial cắt/ghép audio3s đã trả file3.008s; sample audio gốc được đối chiếu trong lượt stream-copy trước. Đây chỉ là evidence lịch sử.
- Trial mới **AI co toàn bộ audio5.055s vào hình3s** đã trả MP4 mới. Provider báo `atempo=1.685`, re-encode AAC. Host đọc file3s, có luồng audio khớp thời lượng trong dung sai codec. Không dùng metadata để tự chứng minh đúng bài nhạc/cao độ; nghe duyệt vẫn cần thiết.
- `SupportsReferenceVideoApproximation` và `SupportsReferenceVideoAudioFit` tách riêng khỏi capability VideoRender chung. Cấu hình/provider chưa hỗ trợ bị chặn ở backend, không fallback provider ngầm.

## 3. Luồng người dùng

Ba thẻ: **Mẫu + sản phẩm / Ảnh đã hoàn thiện / Làm theo video mẫu**. Mục mới có hai ô:

1. Ảnh người mẫu mặc sản phẩm: tải từ máy hoặc chọn Library.
2. Video mẫu chuyển động và âm thanh: nhập MP4 bằng picker host hoặc chọn Library, xem player có controls, không tự phát có tiếng.

Thêm yêu cầu tùy chọn, CTA **Tạo video theo mẫu**. Nhãn **MuseAI · Chuyển động gần đúng**, giải thích rõ thời lượng có thể khác và AI tự ghép/co giãn audio. Không bắt người dùng tách MP3/WAV hoặc thao tác ghép nhạc sau khi tạo.

- Video mẫu tối đa50MiB/30s/4096px; đây là giới hạn sản phẩm. Ảnh JPEG/PNG/WebP tối đa20MiB trước dispatch.
- Không đưa video lớn qua Base64 trong React; host chọn/kiểm file và lưu vào videos root bằng staging hiện có.
- Cho xem/lưu nháp thiếu input; Start bắt đủ hai tệp hợp lệ, capability và không có run xung đột.
- Ẩn số cảnh, grid9ô, sinh ảnh, audio riêng và chọn thời lượng cứng trong mục này; một logical output, Direct.
- Chuyển trang/mở lại giữ nháp. Đổi input run đang chạy bị từ chối. Response upload cũ không áp sang Project mới; gỡ input không xóa file còn dùng.
- ProductOnly cũ đọc được nhưng không nhận run mới. Chọn một mode hiện hành rồi lưu; không tự coi ảnh sản phẩm cũ là ảnh mẫu mặc sản phẩm. ReadyImage không bị yêu cầu video mới.

## 4. Tổ chức mã và snapshot

`ModelAndProductWorkflow`, `ReadyImageWorkflow`, `ReferenceVideoWorkflow` qua `IVideoWorkflow`/`VideoWorkflows`. `WorkflowSceneJobs` dùng chung các request ảnh/grid/prompt; `ProjectWorkflowService` giữ queue/run/project gate/transaction/apply/retry. Không tạo queue/provider mới.

- Config thêm `referenceVideoAssetId`, availability và lý do lỗi được suy khi đọc, không tin flag client gửi.
- Job có ID/hash hai input, `ReferenceVideoMode=PromptApproximation`, `ReferenceAudioPolicy=FitOriginalAudioByAI`, metadata thời lượng/kích thước nguồn, flag nguồn có audio. ConfigVersion3 cho snapshot mới; ảnh-only giữ version1.
- Metadata nguồn giữ riêng. Người dùng chọn mục tiêu 10, 15, 20 hoặc 30 giây (mặc định 10 giây khi chọn chế độ). Cấu hình lưu cùng dự án và được chốt trong snapshot khi tạo tác vụ. MuseAI nhận thời lượng mục tiêu trong prompt, còn độ dài tệp xuất có thể chênh lệch.
- Kiểm đúng Linked/type/Project hoặc app scope, path/junction/root, file/hash trước gửi. Video không nằm trong ImageAssetIds hoặc AudioAssetId. Snapshot/retry không lấy input mới từ form.
- Một job VideoRender: gửi lần lượt ảnh và video kèm audio → MuseAI phân tích động tác → viết prompt → sinh video từ ảnh → tự tách/ghép/co giãn audio trong môi trường MuseAI → trả một MP4 hoàn chỉnh.
- Không thêm FFmpeg/TTS/mixer/transcode vào Fraylo. Backend chỉ đọc/kiểm metadata và lưu file; việc xử lý audio thuộc MuseAI theo yêu cầu người dùng.
- Lưu phản hồi/prompt thật và caption nếu có, provenance chỉ rõ approximate motion và AI audio fitting, `AudioIdentityVerification=ProviderReported_UserReview`. Không tự dựng prompt hoặc tỷ lệ giống nhau giả.

## 5. Chống gửi trùng và kiểm đầu ra

- Checkpoint trước click; lưu side chat URL sau gửi. Retry/restart mở đúng chat đã gửi, không resend mù khi chưa rõ. Browser ẩn, profile có một owner; chỉ login mới hiện theo policy app.
- Đính kèm ảnh/video lần lượt, xác nhận đủ preview; không coi request nền lưu account là upload. Chỉ nhận video trong message assistant ở đúng side chat, không nhầm avatar hoặc kết quả từ lượt thử cũ.
- MP4 phải có track video, thời lượng dương trong giới hạn đầu vào/đầu ra sản phẩm. **Không so bằng thời lượng nguồn.**
- Nếu nguồn có audio thì output phải có audio; nguồn không audio thì output không được tự thêm audio. Thời lượng track audio/output lệch tối đa0.25s cho metadata/codec rounding; đây là kiểm cấu trúc, không phải bằng chứng đúng bài nhạc.
- Không so hash sample audio, không cấm re-encode/time-stretch. Người dùng nghe để duyệt chất lượng, bài nhạc và nhịp. File không đáp ứng cấu trúc trả lỗi thật, không chọn làm output thành công.
- Provider từ chối/không tạo được thì báo rõ; không dùng ảnh/slideshow/fake làm output. Hủy sau submit không hứa hủy được tác vụ/chi phí từ dịch vụ.
- Chỉ chọn revision sau khi file đã lưu/kiểm và còn đúng run/revision/Project. Lịch sử candidate được giữ; generation hoàn tất không tự đăng.

## 6. Lát triển khai và qualification

| Lát | Phạm vi |
|---|---|
| TM0 | Khảo sát provider, trial gần đúng và AI audio fitting; evidence lưu cả giới hạn/trial cũ bị thay thế. |
| TM1 | Contracts Content/Jobs/Providers/Assets, mode/capability/policy và JSON; không đổi schema. |
| TM2 | Application Assets/Jobs, host AssetHandlers, Library: MP4 import, metadata/hash/scope, ảnh+video shared. |
| TM3 | Workflow riêng, snapshot, adapter MuseAI, checkpoint/recovery/apply/provenance. |
| TM4 | Thay đúng thẻ, hai input/preview/nháp, nhãn gần đúng và AI xử lý audio; giữ hai luồng cũ. |
| TM5 | Smoke SQLite/file thật, regression workflow/provider/UI, build Release, visual Light/Dark/hẹp; tách evidence harness khỏi full app live. |

Owner Codex coordinator, tuần tự, tự review. Giữ mọi sửa đổi có sẵn, không commit/push hoặc nâng DB cá nhân trong task này. Chỉ kiểm app/provider thật khi có quyền, profile và cặp mẫu rõ ràng; trường hợp chưa chạy ghi NOT_RUN.

Acceptance sản phẩm: ba mục đúng phạm vi; không bắt cùng thời lượng; AI tự ghép/co giãn audio; một output thật có preview/provenance; chặn file/scope/hash sai, bấm lặp/cancel/restart không gửi trùng; hai workflow cũ giữ hành vi. Nghiệm thu UI WebView2/DPI và nhiều cặp mẫu thật còn theo checkpoint/evidence, không tự gọi DONE từ build hoặc một trial.

Đọc cùng [quy tắc kỹ thuật](../agents/ENGINEERING_RULES.md), [workflow từ ảnh](PRODUCT_IMAGE_VIDEO_WORKFLOW_PLAN.md), [hai luồng video](VIDEO_TWO_FLOW_SPLIT.md), [workflow tự động](AUTOMATIC_VIDEO_WORKFLOW_PLAN.md), [UI/browser](UI_BROWSER_PLAN.md), [theme](UI_THEME_PLAN.md), [Settings](SETTINGS_PLAN.md) và [video-first](VIDEO_FIRST_ARCHITECTURE_PLAN.md).
