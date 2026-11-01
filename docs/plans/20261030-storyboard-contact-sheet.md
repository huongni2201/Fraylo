# Tạo ảnh storyboard 9 ô cho một video 15 giây

Ngày: 2026-10-30. Trạng thái: **ĐANG TRIỂN KHAI** — backend (P1, phần lớn P2) và tùy chọn UI (P3) đã làm trên working tree, chưa commit; chưa có kiểm chứng provider thật. Chi tiết đã làm / chưa làm ở mục 9.

## 1. Yêu cầu đã chốt

Người dùng đưa ảnh sản phẩm → AI tạo **một ảnh storyboard gồm 9 ô, bố cục 3×3** → gửi nguyên ảnh đó cùng prompt chuyển động cho AI → nhận **một video 15 giây**.

- Không tách ô, không tạo 9 clip, không ghép video cục bộ.
- Thứ tự đọc trái sang phải, trên xuống dưới; mỗi ô đại diện một cảnh toàn màn hình.
- Mục tiêu nội dung: thu hút ở đầu, cho thấy sản phẩm và chi tiết, kết thúc bằng lời mời xem sản phẩm trong giỏ hàng. Không cam kết giữ chân hay chuyển đổi chỉ vì có storyboard.
- Phạm vi yêu cầu hiện tại là lập kế hoạch, không bắt đầu triển khai.

## 2. Quan hệ với kiến trúc hiện hành

Đọc cùng [STATUS](../agents/STATUS.md), [TASKS](../agents/TASKS.md), [VIDEO_TWO_FLOW_SPLIT](VIDEO_TWO_FLOW_SPLIT.md), [video-first](VIDEO_FIRST_ARCHITECTURE_PLAN.md), [workflow tự động](AUTOMATIC_VIDEO_WORKFLOW_PLAN.md), [quy tắc kỹ thuật](../agents/ENGINEERING_RULES.md).

Plan này bổ sung **cách tạo ảnh tham chiếu** trong `SingleVideo`, không thêm chiến lược video thứ ba. Luồng `MultiVideo` tiếp tục hoạt động riêng.

Thay đổi có chủ đích so với VIDEO_TWO_FLOW_SPLIT §3.3 và D2: khi bật storyboard 9 ô, ảnh gửi render là **ảnh storyboard sinh ra**, thay cho ảnh sản phẩm gốc. Vẫn không có ảnh/job theo từng đoạn; toàn run chỉ sinh một ảnh ghép và một video.

Hiện trạng đã đối chiếu:

- `ProjectWorkflowConfigDto` có `VideoStrategy`, cấu hình được lưu JSON; có thể bổ sung tùy chọn mà không thêm bảng SQLite.
- `StoryboardContentService` hiện lưu storyboard văn bản gồm `durationSeconds` và `shots`; parser chấp nhận 1–30 đoạn và dung sai thời lượng ±2 giây. Không coi dữ liệu văn bản này là ảnh storyboard.
- `ProjectWorkflowService.Pipeline.cs` áp dụng kết quả Script/Image/VideoPrompt; `ProjectWorkflowService.ContentItems.cs` tạo yêu cầu render với `ImageAssetIds`.
- `VideoProductionTab.tsx` là màn chọn chiến lược và theo dõi kết quả.
- Provider thật chưa được nghiệm thu cho việc hiểu ảnh 3×3 thành chuỗi cảnh. Việc upload ảnh thành công không chứng minh capability này.

CodeGraph đã được gọi trước khi dò source nhưng kết nối trả `Transport closed`; dùng đọc source trực tiếp để lập plan. Không thay dependency hoặc đề xuất API thư viện mới.

## 3. Phạm vi MVP và quyết định thiết kế đề xuất

### 3.1 Cấu hình

- Thêm `referenceImageMode`: `Direct` / `StoryboardGrid9` vào cấu hình và snapshot run. Đây là tên contract đề xuất, cần chốt trước code.
- Cấu hình cũ thiếu trường này tiếp tục là `Direct`; không tự chuyển run cũ.
- UI chọn `SingleVideo` có tùy chọn **Tạo ảnh storyboard 9 ô**. Mặc định cũ được giữ; người dùng bật cho dự án cần tính năng.
- MVP của `StoryboardGrid9` hỗ trợ thời lượng cấu hình 15 giây, số ô cố định 9. `TargetSceneCount` không quyết định số ô và không bị ghi đè để gây ảnh hưởng luồng nhiều video.
- Mốc storyboard yêu cầu kết thúc đúng 15 giây trong chế độ này; không đổi dung sai ±2 giây của `SingleVideo` thông thường. Video thật có đúng thời lượng yêu cầu hay không phải kiểm chứng riêng qua provider.
- Nguồn của ảnh 3×3 phụ thuộc chế độ đầu vào (xem §3.3):
  - `ReadyImage`: ảnh hoàn chỉnh có sẵn là nguồn; **không có bước Ghép ảnh**, bước tạo 3×3 là bước ảnh đầu tiên. Vẫn sinh một ảnh 3×3 khi người dùng bật tùy chọn này; UI giải thích ngoại lệ này rõ ràng.
  - `ModelAndProduct`: thêm **bước Ghép ảnh** chạy trước (ảnh mẫu người + ảnh sản phẩm → một ảnh hoàn chỉnh). Ảnh ghép đó là nguồn của bước tạo 3×3, nên bước 3×3 là bước ảnh thứ hai.
  - `ProductOnly`: dùng ảnh sản phẩm làm nguồn, không có bước Ghép ảnh (giả định theo cùng nguyên tắc với ReadyImage; chưa được người dùng xác nhận riêng).
- Kế thừa tài nguyên Nhóm → Dự án theo cơ chế hiện có; chốt ID thực tế vào snapshot, không đọc lại mặc định nhóm trong lúc retry.

### 3.2 Một nguồn nội dung cho ảnh và video

Mở rộng contract Script riêng cho chế độ grid, tái sử dụng cấu trúc `shots` hiện hành:

- Chính xác 9 shot; thứ tự mảng ánh xạ ô 1–9, không thêm bộ chỉ số cạnh tranh.
- Mỗi shot có `startSeconds`, `endSeconds`, `description`, `imagePrompt`, `motionPrompt`.
- Thời gian liên tiếp từ 0 đến 15; tận dụng số giây nguyên hiện tại, không chia đều 15/9 và không cần đổi sang số thập phân.
- Mỗi đoạn có mô tả hình và một chuyển động rõ. Không lấy 9 ô từ một Script rồi để job VideoPrompt tự sáng tác chuỗi cảnh khác.
- Áp validator grid riêng; không bắt luồng Direct/MultiVideo có 9 đoạn hoặc đủ trường grid.
- Kết quả sai contract giữ lỗi/NeedsReview theo cơ chế hiện có, không âm thầm dùng ảnh gốc để render.

### 3.3 Quy trình chạy

1. Resolve đầu vào và prompt phân tầng hiện hành; lưu snapshot cấu hình, tham chiếu sản phẩm/người mẫu/ảnh sẵn sàng và provider.
2. Job Script tạo 9 shot; parse và validate trước khi đi tiếp. Script không phụ thuộc ảnh ghép; chạy trước để cảnh duy nhất của run và 9 shot được tạo một lần (đường `Direct` hiện có tạo cảnh trước nên cần Script ở đầu mới tái dùng được `EnqueueJobsForExistingScenesAsync`).
3. **Ghép ảnh (chỉ `ModelAndProduct`)**: enqueue một job Image với purpose `ComposeImage` (template ghép ảnh hiện hành), gửi ảnh mẫu người + ảnh sản phẩm qua adapter. Tải và lưu một asset **ảnh ghép**; chỉ apply sau khi file/metadata hợp lệ. Prompt ghép ảnh chỉ nói về mẫu + sản phẩm, không chứa nội dung 9 shot. Ảnh ghép chỉ là nguồn cho bước 3×3, không phải ảnh gửi render. `ReadyImage` và `ProductOnly` bỏ qua bước này; nguồn lần lượt là ảnh sẵn sàng và ảnh sản phẩm.
4. Từ 9 shot tạo một prompt ảnh toàn bảng. Enqueue **một job Image** cho Scene đại diện của run, gửi **ảnh nguồn của bước trước** (ảnh ghép / ảnh sẵn sàng / ảnh sản phẩm) qua adapter.
5. Tải và lưu một asset ảnh 3×3; chỉ apply sau khi file/metadata hợp lệ. Lưu liên kết ảnh với run và revision nội dung, không chỉ dựa vào app-setting cấp Project đang thay đổi.
6. Job VideoPrompt nhận cùng danh sách shot, thông tin bố cục và thời lượng; bắt buộc giữ ánh xạ ô/mốc. Lưu snapshot kết quả đã validate.
7. Enqueue **một job VideoRender**, `ImageAssetIds` có đúng **một ID ảnh 3×3 của revision hiện hành**. Không gửi ảnh ghép, ảnh sản phẩm gốc hay ảnh sẵn sàng thay thế hoặc thêm thành ảnh render thứ hai trong MVP.
8. Lưu video cuối bằng đường hiện hành; hoàn tất khi asset video đã được ghi hợp lệ.

Số bước ảnh: `ModelAndProduct` có hai (Ghép ảnh → 3×3); `ReadyImage`/`ProductOnly` chỉ có một (3×3). Bước Script là bước văn bản, không tính là bước ảnh. Một ảnh 3×3 được coi là sẵn sàng khi marker `scene.grid.<sceneId>` trỏ đúng asset ảnh hiện tại của cảnh; job 3×3 có step key `grid.<sceneId>.<revision>` để apply phân biệt với job Ghép ảnh.

Thứ tự tuyến tính giúp kiểm soát dependency. Không thêm bước người dùng duyệt bắt buộc, không thay quy định workflow chạy tự động. Preview và tạo lại là thao tác tùy chọn.

Khác biệt có chủ đích so với đường Direct hiện có: ở `SingleVideo` Direct + `ModelAndProduct`, mã đang làm (`composeFirst` trong `ProjectWorkflowService.Lifecycle.cs`, chưa commit) bỏ Script và ghép ảnh trước. Ở chế độ `StoryboardGrid9`, `composeFirst` tắt: Script chạy trước, bước Ghép ảnh được tái sử dụng sau Script, và render dùng ảnh 3×3, không dùng ảnh ghép. Đường Direct không đổi. (Thứ tự Ghép ảnh → Script của bản plan đầu đã đổi thành Script → Ghép ảnh khi triển khai; hai bước độc lập nên kết quả không đổi.)

### 3.4 Prompt tạo ảnh ghép

- Một ảnh duy nhất, đúng 3 cột × 3 hàng; ô dọc 9:16 và toàn ảnh dọc 9:16, kích thước thực tế theo capability provider.
- Nền, ánh sáng, người mẫu và chi tiết sản phẩm nhất quán; góc quay đủ khác nhau, viền mảnh; không làm sai màu, đường may, họa tiết hay phụ kiện.
- MVP không yêu cầu chữ hoặc số trong ảnh để tránh chúng xuất hiện trong video; thứ tự ô được giải thích trong prompt.
- Không tạo giá, giảm giá, chứng nhận, công dụng hoặc khan hiếm chưa có trong dữ liệu đầu vào. Không tự sinh màu/biến thể khác nếu ảnh gốc không cung cấp.
- Prompt ảnh do backend tổng hợp từ shot và template Image đang chọn; không bỏ qua cơ chế prompt hiện hành hoặc hardcode nội dung mẫu thời trang vào mọi ngành hàng.

### 3.5 Prompt tạo video từ nguyên bảng

Phần hướng dẫn bắt buộc do backend bổ sung, còn nội dung quảng cáo lấy từ prompt người dùng:

> Ảnh tham chiếu là storyboard 3×3, không phải bố cục video đầu ra. Đọc trái sang phải, trên xuống dưới. Mỗi ô thành một cảnh toàn màn hình theo mốc đã cho. Tạo một video dọc 9:16 dài 15 giây. Không hiển thị bảng, viền hoặc số ô; không chia màn hình. Cắt dựng giữa các cảnh, không morph trang phục hay trộn các ô. Giữ đúng sản phẩm và cùng người mẫu. Chỉ thực hiện chuyển động được mô tả trong từng cảnh.

Nhịp nội dung đề xuất: ô 1 tạo chú ý; ô 2–3 làm rõ dáng; ô 4–6 cho thấy chi tiết/cách sử dụng; ô 7–8 giải đáp lựa chọn thực sự có; ô 9 kết thúc bằng CTA phù hợp. Không bắt mọi sản phẩm phải có người mẫu, đổi màu hay màn cởi áo.

CTA trong video không tạo nút mua có thể bấm. Giỏ hàng thật phụ thuộc bước gắn sản phẩm trên nền tảng đăng; không mở rộng publishing trong task này.

## 4. UI và trạng thái

- Hiện tùy chọn storyboard bên trong `SingleVideo`, giải thích “Tạo một ảnh 9 ô rồi gửi nguyên ảnh để tạo một video 15 giây”. Không hiển thị 9 hàng video độc lập.
- Khi bật: hiển thị 9 cảnh cố định và thời lượng 15 giây; khi tắt/đổi chiến lược khôi phục cấu hình phù hợp, không làm mất draft.
- Các mốc tiến trình: viết 9 cảnh → [ghép ảnh mẫu + sản phẩm, chỉ ModelAndProduct] → tạo ảnh storyboard → viết prompt chuyển động → tạo video → hoàn tất. Mốc ghép ảnh ẩn với ReadyImage/ProductOnly. Trạng thái lấy từ job thật.
- Preview có phóng to, prompt ảnh, prompt video và video kết quả; phân biệt rõ ảnh đầu vào (sản phẩm/mẫu/ảnh sẵn sàng), ảnh ghép trung gian (nếu có) và ảnh storyboard 3×3 đầu ra.
- Tạo lại ảnh ghép làm ảnh 3×3, prompt video và video phụ thuộc bị stale. Tạo lại storyboard làm prompt/video phụ thuộc bị stale. Không tự render từ ảnh cũ trong thời gian ảnh mới đang chạy/lỗi.
- Đổi cấu hình khi run đang chạy theo khóa hiện hành. Sau khi render đã submit, tạo lại là revision/run mới; không thay snapshot job đang chờ kết quả.
- Loading, lỗi, retry, canceled, file thiếu và kết quả chưa chắc chắn có thông báo đúng nguyên nhân. Giữ Light/Dark, keyboard và DPI theo UI_THEME_PLAN/UI_BROWSER_PLAN.

## 5. Lưu trữ, retry và capability

- Ưu tiên tái dùng Scene đại diện, asset, job snapshot và JSON cấu hình. Thêm vai trò `StoryboardGrid` nếu cần phân biệt; không thêm bảng hoặc migration khi cấu trúc hiện tại đáp ứng.
- Snapshot phải chứa mode, 9 shot, revision/hash nội dung, asset nguồn, asset ảnh ghép khi có, asset grid khi đã có, provider và prompt đã chốt cho từng job. Job 3×3 ghi rõ asset nguồn đã dùng; ảnh ghép là một revision riêng, đổi ảnh ghép thì revision 3×3 mới.
- Retry/hủy/restart của bước Ghép ảnh không được tạo hai ảnh ghép hoặc hai job 3×3 kế tiếp; kết quả muộn của ảnh ghép cũ không được dùng làm nguồn cho run mới. Không nhận kết quả muộn của run/revision cũ vào run mới.
- Job Image chỉ thành công sau khi lưu file và metadata đúng Project. Kiểm tra quyền tài nguyên cấp Nhóm/Dự án, trạng thái asset và file tồn tại trước render.
- Retry trước submit có thể thử theo cơ chế hiện có; sau submit phải đối soát conversation/checkpoint. Không tạo thêm yêu cầu ảnh/video chỉ vì timeout.
- Hủy, crash/restart và phản hồi lặp không được tạo hai job kế tiếp hoặc dùng ảnh sai revision. Tệp cũ được giữ theo chính sách tài nguyên hiện hành.
- Phân biệt khả năng kỹ thuật “upload một ảnh” với khả năng đã kiểm chứng “hiểu ảnh storyboard”. Mode có nhãn thử nghiệm cho đến khi đạt kiểm chứng live; capability thiếu phải báo rõ và chặn bước không hỗ trợ, không đổi provider hoặc cách làm ngầm.

## 6. Thứ tự triển khai và ownership

Owner đề xuất: Codex coordinator, làm tuần tự. Plan không tạo task đang chạy và không sửa trạng thái chung. Trước triển khai, đối chiếu TASKS/STATUS và baseline; ghi task/dependency thực tế rồi mới sửa.

Working tree lúc lập plan có thay đổi tại `ProjectWorkflowService.Pipeline.cs`, `VideoProductionTab.tsx`, `JobProgressPage.tsx`, `TikTokPostPreview.tsx` và `.playwright-mcp/`. Khi cập nhật plan (bước Ghép ảnh), working tree còn thay đổi chưa commit ở `ProjectWorkflowService.Config.cs`, `.Lifecycle.cs`, `.Scenes.cs`, `VideoWorkflowPromptComposer.cs` (có `ComposeImagePurpose` và `composeFirst`). Giữ nguyên; các file workflow/UI có vùng giao nhau cần đọc lại diff trước khi sửa. Không yêu cầu commit hay hoàn tác công việc có sẵn.

### P0 — Baseline và xác minh đường upload

File đối chiếu: các file trong P1–P4; adapter ảnh/video hiện hành tìm từ composition root khi triển khai.

- [ ] Chạy baseline build/test phù hợp trên cây hiện hành, ghi lỗi có sẵn; không dùng app/DB/profile cá nhân làm fixture.
- [ ] Xác minh adapter Image đính kèm ảnh nguồn và tải được ảnh kết quả; adapter render đính kèm đúng một ảnh grid.
- [ ] Đọc diff `composeFirst`/`ComposeImagePurpose` chưa commit và xác minh bước Ghép ảnh hiện có (mẫu + sản phẩm → một ảnh) lưu được asset để tái dùng làm nguồn 3×3; chưa đạt thì ghi blocker, không giả định.
- [ ] Kiểm tra capability 15 giây, giới hạn kích thước ảnh và một lần thử storyboard live khi tài khoản/quyền cho phép. Chưa chạy thì ghi NOT_RUN, không hứa chất lượng.
- [ ] Chốt contract và phạm vi file; không phụ thuộc vào các mục VF chưa nghiệm thu mà không ghi rõ.

### P1 — Contract và nội dung 9 shot

Files dự kiến: `src/Fraylo.Contracts/Content/VideoWorkflowDtos.cs`, `src/Fraylo.Application/Content/ProjectWorkflowService.Config.cs`, `ProjectWorkflowService.Lifecycle.cs`, `StoryboardContent.cs`, `VideoWorkflowPromptComposer.cs` trong cùng thư mục Content.

- [ ] Thêm mode, default/validation và snapshot; mode grid chỉ hợp lệ với SingleVideo/15 giây.
- [ ] Xác định nguồn ảnh 3×3 theo workflow mode (ảnh ghép / ảnh sẵn sàng / ảnh sản phẩm) và bước Ghép ảnh chỉ áp cho `ModelAndProduct`; yêu cầu đủ đầu vào theo mode như validation hiện có.
- [ ] Thêm contract 9 shot và validator riêng; giữ parser Direct hiện có.
- [ ] Soạn prompt Script/Image/VideoPrompt dùng chung một nội dung đã chốt.
- [ ] Test đủ/thiếu/thừa shot, mốc hở/chồng/sai thứ tự, thiếu prompt, JSON hỏng, cấu hình cũ và cấu hình không hợp lệ.

### P2 — Một ảnh ghép và một render

Files dự kiến: `ProjectWorkflowService.Pipeline.cs`, `ProjectWorkflowService.Scenes.cs`, `ProjectWorkflowService.ContentItems.cs`, `ProjectWorkflowService.Lifecycle.cs` trong `src/Fraylo.Application/Content/`; adapter thực tế chỉ sửa nếu P0 chứng minh thiếu khả năng đính kèm/tải ảnh.

- [x] Với `ModelAndProduct`: nối Script → Ghép ảnh → Image 3×3; với `ReadyImage`/`ProductOnly`: Script → Image 3×3 dùng thẳng ảnh có sẵn. Rồi VideoPrompt → Render theo dependency và revision. (smoke `workflow` ca 20, 21)
- [ ] Thay nhánh dùng ảnh gốc bằng một job Image tạo grid từ ảnh nguồn của bước trước; không để đường `composeFirst` Direct render bằng ảnh ghép khi mode grid bật.
- [ ] Lưu asset grid; render chỉ nhận ID grid hiện hành; không sinh job theo từng ô.
- [ ] Hoàn thiện invalidation, cancel, retry/reconcile và chặn kết quả muộn/khác Project.
- [ ] Test đầu-cuối bằng fake provider: ModelAndProduct có đúng một job Ghép ảnh + một job 3×3, ReadyImage/ProductOnly chỉ một job 3×3; một prompt video, một render. Kiểm tra payload: Ghép ảnh nhận mẫu + sản phẩm, 3×3 nhận ảnh ghép (hoặc ảnh có sẵn), Render chỉ nhận ảnh 3×3.
- [ ] Test hủy/retry/tạo lại ảnh ghép: không trùng job, ảnh 3×3 và video cũ bị stale, kết quả muộn bị bỏ.
- [ ] Test lỗi tải/lưu ảnh, file thiếu, submit timeout, restart, callback lặp, hủy và tạo lại; không tự fallback ảnh gốc.

### P3 — Giao diện cấu hình và preview

Files: `src/Fraylo.Web/src/features/projects/VideoProductionTab.tsx`, `videoWorkflowApi.ts`, `videoProductionTab.test.ts` trong cùng thư mục; component preview hiện có nếu cần.

- [ ] Thêm lựa chọn và validation; một preview grid, chín đoạn nội dung, một video cuối.
- [ ] Hiển thị tiến trình và lỗi thật, nhãn thử nghiệm và tạo lại theo cơ chế hiện hành; mốc Ghép ảnh chỉ hiện với `ModelAndProduct`, kèm preview ảnh ghép trung gian.
- [ ] Test lưu/đọc mode, cấu hình cũ, đổi chiến lược, bảo toàn draft, pending/stale/error, và không render khi grid chưa sẵn sàng.
- [ ] Kiểm tra Light/Dark, bàn phím, phóng to và DPI trên WebView2; ghi NOT_RUN nếu chưa chạy.

### P4 — Regression, live và tài liệu

Files: `tests/Fraylo.Storyboard.SmokeChecks/`, `tests/Fraylo.Desktop.SmokeChecks/VideoWorkflowSmokeTests.cs`, các test workflow/recovery liên quan; plan này và VIDEO_TWO_FLOW_SPLIT, STATUS/TASKS khi có kết quả triển khai.

- [ ] Chạy Storyboard smoke, Desktop workflow/remediation và web tests/typecheck/build; build backend/solution theo baseline. App đang mở khóa DLL thì dùng thư mục output riêng hoặc ghi blocker.
- [ ] Regression: MultiVideo vẫn render theo cảnh; SingleVideo Direct vẫn dùng đường cũ; mode grid không đổi semantics của cả hai.
- [ ] Live một ảnh sản phẩm → ảnh grid → một video 15 giây; lưu job IDs, prompt snapshots, ID/hash file ảnh gửi đi và video nhận về.
- [ ] Kiểm tra video không còn bảng/viền, đúng thứ tự cảnh, sản phẩm/người mẫu ổn định và chuyển cảnh chấp nhận được. Provider bỏ cảnh hoặc biến dạng phải ghi rõ; không đánh dấu đạt chỉ vì có file video.
- [ ] Đồng bộ tài liệu theo kết quả; giữ các findings chưa đóng, bỏ hướng dẫn mâu thuẫn trong phạm vi mới.

## 7. Acceptance và giới hạn

Đạt chức năng khi một run grid hợp lệ tự đi từ ảnh sản phẩm đến đúng một ảnh 3×3 và một render dùng chính ảnh đó; dữ liệu truy vết được, phục hồi không gửi trùng, UI hiển thị đúng, regression Direct/MultiVideo đạt.

Đạt nghiệm thu provider khi video live thể hiện chuỗi cảnh toàn màn hình theo storyboard và thời lượng 15 giây trong khả năng thực tế đã đo. Không dùng test giả để kết luận khả năng này. Nếu provider không hiểu bảng, ghi chưa đạt và giữ tính năng thử nghiệm/không khả dụng cho provider đó; không tự chuyển sang tách ô hay ghép clip trái yêu cầu.

Không thêm crop/split ảnh, 9 lượt render, FFmpeg, TTS, provider mới hoặc phân tích thị giác tự động trong MVP. Kiểm tra file ảnh không thể tự chứng minh đúng 9 ô hoặc đúng sản phẩm; phần chất lượng hình cần kiểm tra trực quan khi nghiệm thu.

## 8. Bằng chứng ở bước lập plan

Đã đọc trạng thái, dependency, quy tắc dự án, các plan liên quan và source tại các điểm tích hợp nêu trên. Chưa chạy build/test hoặc provider thật vì chưa sửa code. Plan này không phải bằng chứng feature đã hoạt động.

## 9. Tiến độ triển khai (2026-10-30)

Đã làm, trên working tree (không commit):

- Contract: `ReferenceImageModes` (`Direct`/`StoryboardGrid9`), trường `referenceImageMode` trong `ProjectWorkflowConfigDto` và `SaveProjectWorkflowConfigRequest`. Cấu hình cũ thiếu trường này đọc là `Direct`. Lưu cấu hình từ chối mode lạ, và `StoryboardGrid9` khi không phải `SingleVideo`/15 giây; `Start` kiểm lại và yêu cầu mẫu Image đang chọn.
- `StoryboardContentService.ParseGrid`: đúng 9 shot, đúng thời lượng yêu cầu (không dung sai), mỗi shot có `imagePrompt` và `motionPrompt`; `StoryboardShot` có thêm `MotionPrompt` tùy chọn. Parser Direct không đổi.
- Prompt: Script có schema 9 shot riêng; prompt ảnh 3×3 do backend ghép từ mẫu Image + bố cục + 9 ô (`ComposeGridImagePrompt`); prompt video thêm `GRID_REFERENCE` (§3.5) và mốc từng ô kèm chuyển động.
- Pipeline: Script apply dùng `ParseGrid` (sai contract → run Failed + log, không tạo cảnh). Cảnh có ảnh nguồn mà marker `scene.grid.*` chưa khớp thì xếp job Image 3×3 (đính kèm duy nhất ảnh nguồn); `ModelAndProduct` đi Ghép ảnh trước rồi 3×3. Apply 3×3 ghi marker rồi mới xếp VideoPrompt; VideoPrompt và render nhận ảnh 3×3. Auto-render và nút "Tạo video" bị chặn khi ảnh 3×3 chưa sẵn sàng.
- Adapter ChatGPT/Gemini: job Image không có mẫu/sản phẩm mà có `SourceImageAssetId` thì đính kèm ảnh nguồn đó; job Ghép ảnh vẫn bắt buộc đủ mẫu + sản phẩm.
- UI `VideoProductionTab`: ô chọn "Tạo ảnh storyboard 9 ô (thử nghiệm)" trong `SingleVideo`, khóa thời lượng 15 giây, tắt khi đổi sang nhiều video.

Kiểm chứng (Release): `Application` build 0 warning/0 error; `Desktop.SmokeChecks` `workflow` 20 PASS/1 FAIL (ca 20, 21 mới PASS; ca 18 FAIL trước đó vì mặc định `VideoStrategy` đã đổi sang `SingleVideo` trong thay đổi chưa commit của việc khác, không do task này), `remediation` 14/14, `project-first` và `t15` 19/19 PASS; `Storyboard.SmokeChecks` PASS (thêm ca `ParseGrid`); web `tsc` PASS, `videoProductionTab.test.ts` 11/11.

Chưa làm / NOT_RUN:

- Live: ảnh sản phẩm → ảnh 3×3 → video 15 giây trên ChatGPT/Gemini/Muse; chưa xác minh provider hiểu ảnh 3×3, tải ảnh kết quả, hay Muse giữ đúng 15 giây. Tùy chọn giữ nhãn thử nghiệm.
- Adapter đính kèm đúng ảnh nguồn cho job 3×3 mới chỉ được kiểm bằng đọc code, chưa chạy trên website thật.
- UI: mốc tiến trình và preview đã làm (xem bên dưới); chưa kiểm Light/Dark, bàn phím, DPI trong WebView2.
- Snapshot run (`WorkflowRunRecord`) chưa lưu `referenceImageMode`; job đã lưu prompt/media snapshot riêng. Tạo lại ảnh ghép/3×3 sau khi render đã gửi chưa có luồng revision riêng ngoài cơ chế stale hiện có (đổi cấu hình đặt lại ảnh cảnh để sinh lại).
- Chưa có test hủy/restart/callback lặp riêng cho bước Ghép ảnh → 3×3 (dùng cơ chế continuation intent hiện có).

### 9.1 Mốc tiến trình và preview UI (đã làm)

- `SceneWorkflowDetailDto` có thêm `gridImageAssetId` (chỉ điền khi marker 3×3 khớp ảnh hiện tại của cảnh) và `composedImageAssetId` (ảnh ghép trung gian, lưu ở `scene.compose.<sceneId>` khi job Ghép ảnh apply); điền trong `list_scenes`.
- `StoryboardGridProgress.tsx`: mốc viết 9 cảnh → [ghép ảnh, chỉ Mẫu + sản phẩm] → ảnh 3×3 → prompt chuyển động → video. Trạng thái suy ra từ dữ liệu cảnh thật; mốc đầu tiên chưa xong là "đang xử lý", run Failed thì là "lỗi". Ba ô ảnh riêng: đầu vào, ảnh ghép trung gian, ảnh 3×3 (gửi render), có nút Phóng to (`AssetPreviewDialog`). Hiện ở màn đang chạy và màn hoàn tất khi bật chế độ 9 ô.
- Kiểm chứng: web `tsc` PASS, `videoProductionTab.test.ts` 12/12; smoke `workflow` ca 20 kiểm thêm hai trường mới (PASS), `remediation` 14/14.
