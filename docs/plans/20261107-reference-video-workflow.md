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
- Ẩn số cảnh, grid 9 ô, sinh ảnh và audio riêng. Cho chọn thời lượng mục tiêu như mục 4. Một đầu ra, dùng ảnh trực tiếp.
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

## 7. Kế hoạch tách phân tích video mẫu và render — 2026-11-08

**Đã tích hợp cục bộ ở chế độ thử nghiệm.** Mục 1–6 mô tả luồng cơ sở. Trong **Làm theo video mẫu**, bật **AI đọc video mẫu trước khi tạo (thử nghiệm)** rồi bấm **Tạo video theo mẫu**. Bước đọc MP4 và viết prompt dùng Gemini Web hoặc ChatGPT Web theo đúng **Cài đặt → Nhà cung cấp Dịch vụ AI → Tác vụ Kịch bản & Đề xuất (Script)**. MuseAI nhận **ảnh người mẫu + prompt đã chốt + chính MP4 mẫu**. Tùy chọn thử nghiệm lưu theo dự án và tắt mặc định trong khi chờ xác minh live/A/B. Owner Codex coordinator, tuần tự, dựa trên TM1–TM4 và bổ sung evidence TM5.

### 7.1. Phân vai và chọn provider

- Hướng MuseAI làm cả hai bước tận dụng adapter đang có. §4 hiện chỉ yêu cầu trả prompt sau render. Trial 16,56 → 10 giây giữ được chuỗi tạo dáng → quay lưng → đi xa, nhưng nhịp, camera và một số chi tiết thay đổi. Tách bước đọc clip giúp xem ngay timeline và prompt trước khi gửi lệnh render, rồi xác định sai lệch nằm ở phân tích, soạn prompt hay video đầu ra. [Evidence TM](REFERENCE_VIDEO_EVIDENCE.md).
- **Nhà cung cấp phân tích lấy từ cấu hình Script.** Google có tài liệu cho việc tải và phân tích video trên Gemini Web. OpenAI ghi khả năng đính kèm phụ thuộc nền tảng, cách tải và tài khoản, và việc phân tích clip có thể không đầy đủ. Cả hai adapter có nhánh thử nghiệm cho MP4 và đều cần kiểm chứng với profile thật. [Gemini Apps Help](https://support.google.com/gemini/answer/14903178?co=GENIE.Platform%3DDesktop&hl=en), [OpenAI Help Center](https://help.openai.com/en/articles/8400551-chatgpt-image-inputs-faq).
- **MuseAI nhận cả MP4 mẫu** để tham chiếu khi cần và tách/ghép âm thanh gốc. Trong trial hiện có, công cụ render của MuseAI dùng ảnh tĩnh và prompt, còn video đính kèm nằm ở tầng chat. Prompt từ bước 1 là nguồn chỉ dẫn chuyển động đã chốt. Một file đính kèm chưa chứng minh công cụ render có điều khiển chuyển động trực tiếp. [Trial direct và audio](REFERENCE_VIDEO_EVIDENCE.md#kiểm-chứng-sau-đăng-nhập-và-quyết-định-mới).
- Mỗi run dùng đúng một AI phân tích được cấu hình và một MuseAI render. UI nêu rõ MP4 mẫu được gửi tới cả hai dịch vụ, còn ảnh người mẫu gửi tới MuseAI. Khi tài khoản thiếu capability, mất phiên, clip không đọc được hoặc kết quả phân tích sai định dạng, dừng ở bước tương ứng và hiện lý do. Không tự đổi provider hoặc tự chuyển sang prompt chung chung.

### 7.2. Bước 1 — Đọc clip và viết prompt

Gửi MP4 mẫu, thời lượng đích và ghi chú ưu tiên động tác. Provider trả timeline và prompt tiếng Anh trong cùng một phản hồi. Ảnh người mẫu được giữ cho bước render. Phân tích chỉ mô tả chuyển động nhìn thấy, còn Fraylo bổ sung chỉ dẫn giữ người và sản phẩm theo ảnh khi gửi MuseAI.

~~~text
Analyze the attached reference video. Return a motion plan and an English
image-to-video prompt. If you cannot inspect the video, report that explicitly.

Describe up to six major beats in the order seen. For each beat record the source
time range, start pose, visible action, direction relative to the frame, and end
pose. Record framing, camera movement and cuts separately. Mark uncertain
details as unknown and do not infer actions hidden by occlusion.

Map the major beats to about {target_seconds} seconds. Keep the opening and
ending poses and the main action order. Preserve relative pacing where possible.
For a shorter target, shorten idle holds first and simplify minor transitions.
For a longer target, let the actions complete naturally without loops or freezes.

Write a compact English render prompt with ordered target time ranges and the
essential camera cues. Describe the person as the subject of the supplied photo.
Avoid carrying over the reference person's identity, outfit, background or text.
Return the observed beats, mapped beats and render prompt as structured JSON.
~~~

Ví dụ minh họa với mẫu 16,5 giây. Nếu 0–4 giây là giữ dáng, 4–8 giây là xoay lưng và 8–16,5 giây là đi xa, prompt 10 giây có thể phân 0–2, 2–5 và 5–10 giây tương ứng. Ca có động tác khác phải lấy mốc theo clip thực tế.

Fraylo kiểm tra cấu trúc, thứ tự, khoảng thời gian nguồn/đích và prompt không rỗng. Kiểm tra này phát hiện dữ liệu sai định dạng. Độ chính xác của mô tả cần đối chiếu với clip. Lưu timeline, prompt, provider, phiên bản prompt, run và hash video mẫu. Sau khi hợp lệ, chốt snapshot render gồm ảnh, MP4 gốc, prompt, chính sách audio và thời lượng. Ghi chú người dùng có thể đổi trọng tâm hoặc cách trình bày, với ràng buộc giữ người, sản phẩm, thứ tự động tác chính và audio gốc.

### 7.3. Bước 2 — Gửi MuseAI tạo video

Gửi ảnh trước, MP4 mẫu sau như adapter hiện tại, rồi gửi prompt render đã chốt với chỉ dẫn ngắn dưới đây. Lưu riêng nội dung thực tế Fraylo đã gửi và prompt công cụ con nếu MuseAI có báo lại, để biết phần nào có thể kiểm chứng.

~~~text
Create one playable video using the attached PHOTO and the supplied RENDER PROMPT.
Preserve the person, face, hair, clothing, product details and colors from PHOTO.
Use PHOTO for the setting unless the user requests a different setting.
Follow the supplied action order and pacing. Use REFERENCE_VIDEO only as a
supporting reference and as the original audio source. Do not rewrite the action
plan or reuse the reference's visual frames, person, clothing or captions.

Fit and merge the reference audio to the actual output duration. Preserve tune,
voice and natural pitch as much as possible. Keep a silent reference silent.
Do not substitute audio or retime the visuals to fit it. Return one final MP4
with fitted audio, or report the failure. Do not make purchases or publish.
~~~

### 7.4. Các lát triển khai

| Lát | Phạm vi và điều kiện hoàn tất |
|---|---|
| 1. Thử khả năng và đo baseline | Dùng fixture được phép trên profile thử. Kiểm tra riêng Gemini Web và ChatGPT Web nhận MP4 và trả đúng thứ tự/mốc chính trên các clip khác nhau. Sau đó đưa prompt đã ghi cùng ảnh + MP4 vào một chat MuseAI mới để xác nhận luồng render và ghép audio. Lấy kết quả luồng MuseAI hiện tại làm baseline. Lưu kết quả theo từng bước và ghi rõ phần nào chưa xác minh. |
| 2. Nối hai bước | Mở rộng đường upload của adapter được chọn cho riêng ReferenceVideo. Chốt cách dùng tác vụ VideoPrompt và snapshot kết quả riêng theo run, tránh apply vào nhánh prompt/ảnh thường. Dùng queue hiện có, chốt một lần kết quả phân tích trước khi xếp job VideoRender. Sửa ReferenceVideoWorkflow và nhánh tạo lại trong ProjectWorkflowService.ContentItems; cập nhật MuseAiPlaywrightAutomation cho payload render đã chốt. Tạo lại chỉ tái dùng phân tích đã hợp lệ khi ảnh, clip, thời lượng, ghi chú và cấu hình phân tích khớp. Đổi đầu vào thì phân tích lại. Retry/Resume bám snapshot cũ và checkpoint từng dịch vụ, chặn enqueue lặp hoặc kết quả đến muộn từ run cũ. |
| 3. Đo A/B và mở duyệt nếu cần | Chạy smoke reference-video và video-workflow, kiểm cả audio có/không, upload lỗi, timeline hỏng, đổi input, hủy/restart và chống gửi trùng ở hai bước. So sánh v1/v2 theo §7.5. Chỉ thêm **Duyệt động tác trước khi tạo** nếu trial cho thấy người dùng cần sửa timeline trước render. Khi có UI, dùng token hiện có và kiểm WebView2 sáng/tối, cửa sổ hẹp và bàn phím. |

Nếu prompt và timeline đã đúng mà MuseAI vẫn lặp lại cùng sai lệch trên lượt đối chứng, ghi mốc và clip để xác định giới hạn render. Điều khiển chuyển động trực tiếp cần một capability riêng được kiểm chứng.

### 7.5. Đo và nghiệm thu

- **Bộ thử đề xuất gồm 6 cặp ảnh + clip.** Phủ một cảnh, nhiều cảnh cắt, camera di chuyển, người bị che, thời lượng nguồn dài/ngắn hơn đích, có và không có audio. Ưu tiên cặp người dùng báo lệch nếu có đủ ảnh, video mẫu và kết quả cũ. Chạy hai luồng với cùng ảnh, video, ghi chú, thời lượng và cấu hình MuseAI quan sát được. Ghi timeline chuẩn trước, chấm ẩn phiên bản và lặp cả hai biến thể ở ca sát ngưỡng.
- **Chấm 0–2 cho bốn tiêu chí** gồm động tác chính, thứ tự và tư thế đầu/cuối, nhịp tương đối, camera/khung hình. Với mỗi lỗi ghi mốc và bước phát sinh nếu xác định được. Người chấm xem video và nghe audio trực tiếp. So vị trí tương đối của mốc động tác vì hai video có thể khác độ dài.
- **Ngưỡng bật mặc định đề xuất** là v2 cải thiện tổng điểm ở ít nhất 4/6 cặp, đúng thứ tự động tác chính ở ít nhất 5/6, và không có ca sai người, đổi sản phẩm, thay nhạc gốc hoặc thêm nhạc vào clip im lặng. Ghi thời gian và chi phí quan sát được của cả hai bước. Ngân sách mỗi run là một lần gửi phân tích và một lần gửi render.
- **Kiểm tra vận hành** gồm tạo mới từ app tới tải/apply MP4, tạo lại và phục hồi theo hai checkpoint, kiểm tệp/audio như §5 và hồi quy hai workflow ảnh.

### 7.6. Bàn giao bản thử

- Gemini hoặc ChatGPT dùng tác vụ VideoPrompt có snapshot riêng và chỉ nhận MP4. Job phân tích mới chốt nhà cung cấp từ Script. Handoff ghi job MuseAI và dấu đã áp dụng kết quả trong cùng một lần lưu SQLite. Tạo lại có thể dùng phân tích khớp đầu vào, run và nhà cung cấp, với hash được kiểm trước gửi. Job MuseAI giữ liên kết với job phân tích.
- Khi mất xác nhận sau khi bấm gửi, dùng cơ chế đối soát của queue. Adapter phân tích thử nghiệm chỉ bấm gửi một lần. Cần kiểm chứng selector tải MP4 trên cả Gemini và ChatGPT, khả năng đọc clip, WebView2, fresh submit MuseAI và A/B trên fixture thật trước khi bật mặc định.
