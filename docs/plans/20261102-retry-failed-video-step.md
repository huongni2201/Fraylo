# Retry thủ công từ bước sản xuất video bị lỗi

Ngày: 2026-11-02 (UTC+07). Trạng thái: ĐÃ TRIỂN KHAI cục bộ (chưa commit) — khác biệt và kết quả kiểm chứng ở §8.
Owner dự kiến: Codex coordinator, thực hiện tuần tự. Dependency: VF1/VF3 và VS1 đã triển khai cục bộ; giữ regression đồng bộ trạng thái của VS1.

## 1. Vấn đề và mục tiêu

**Vấn đề người dùng gặp (ảnh):** Script ✓ → ảnh 3×3 ✓ → prompt chuyển động ✗ "phản hồi chưa có prompt video hợp lệ". Màn hình vẫn quay spinner như đang chạy, không có cách nào chạy lại riêng bước đó; lối thoát duy nhất là quay về form và bắt đầu lại toàn bộ, mất cả storyboard và ảnh đã tạo.

**Mục tiêu:**

1. Khi một bước lỗi, workflow **dừng rõ ràng** tại bước đó (không spinner giả) và hiện lý do.
2. Người dùng bấm **Thử lại bước này** → chỉ bước đó chạy lại, từ cùng đầu vào đã lưu; các bước sau tự tiếp tục như luồng thường.
3. Kết quả các bước trước (storyboard, ảnh ghép, ảnh 3×3, prompt của cảnh khác) **giữ nguyên ID**.
4. Lỗi lần nữa → dừng lại đúng bước đó, lý do mới, lịch sử các lần thử vẫn xem được.
5. Trạng thái đúng sau khi mở lại app; không bao giờ tự chạy lại (giữ NR1).

**Ngoài phạm vi:** đăng TikTok; retry tự động; retry hàng loạt nhiều bước một lần; đổi cấu hình/template khi retry (đó là "Bắt đầu lại", giữ nguyên như hiện tại).

## 2. Nguyên nhân gốc (đã đối chiếu mã nguồn)

| # | Sự thật trong code | Vì sao gây ra vấn đề |
|---|---|---|
| R1 | `Pipeline.cs` apply VideoPrompt: prompt không đạt `ParseVideoContent` → đánh dấu scene stale + ContentItem `NeedsReview`, **không đổi RunState**, job vẫn `Succeeded`. | Run giữ `Running` mãi → spinner kẹt. Đây là lỗi trong ảnh. |
| R2 | `Config.cs` `GetWorkflowConfigAsync` chỉ suy `Failed` khi mọi job terminal và có job `Failed/Canceled`. | Không biết khái niệm "thành công nhưng bị loại" → không nhận ra R1. |
| R3 | Cùng hàm đó group job theo chuỗi `WorkflowStepKey`. Key của Image/Prompt chứa `scene.UpdatedAtUtc`; key render có hậu tố số lần (`BuildRenderStepKeyAsync`). | Cùng một bước logic có thể ra nhiều nhóm → một job lỗi cũ có thể giữ run ở Failed dù bước đã chạy lại thành công. |
| R4 | `JobService` chống trùng enqueue bằng hash(ProjectId, RunId, WorkflowStepKey). | Enqueue lại với key cũ trả về đúng job đã bị loại → không thể chạy lại. |
| R5 | `VideoProductionTab` khi `Failed` đưa về form chuẩn bị; `StoryboardGridProgress` suy mốc từ asset/prompt, không có dữ liệu lỗi theo bước. | UI không có chỗ đặt nút retry, và prompt cũ còn trong bảng làm mốc trông như đã xong. |
| R6 | `Pipeline.cs` apply Image không kiểm `ResultReference`: rỗng vẫn gán `scene.ImageAssetId = job.ResultReference`, bỏ qua marker grid/compose rồi vẫn enqueue bước sau (ảnh 3×3 hoặc VideoPrompt). Chưa thấy executor bảo đảm job Image Succeeded luôn có ảnh. | Lỗi ảnh 3×3/ghép ảnh bị che: mốc ảnh không báo lỗi, ảnh cảnh bị xóa, prompt/render chạy trên ảnh sai. |

Đã kiểm: **Script** (storyboard parse lỗi — nhánh này đã tự set `RunState = Failed`) và **VideoPrompt** có kiểu "Succeeded nhưng bị loại". **Image** (compose/grid/image) bình thường lỗi qua job `Failed`, nhưng có thể Succeeded mà không có ảnh (R6) — plan xử lý trường hợp này như "bị loại". **VideoRender** chỉ lỗi qua job `Failed`. `JobService.RetryAsync` đã đủ điều kiện cho job Failed (ExpectedRevision, reason không retry được, `Attempts < MaxAttempts = 3`).

## 3. Mô hình trạng thái

Toàn bộ thiết kế xoay quanh hai khái niệm. Mọi chỗ (backend, UI, test) dùng chung định nghĩa này.

### 3.1 Bước logic

Một bước logic được xác định bằng `(Kind, SceneId)`, **không** bằng chuỗi stepKey:

| Kind | TaskType | Nhận diện | Mốc UI |
|---|---|---|---|
| `script` | Script | — (SceneId null) | Viết cảnh / storyboard |
| `compose` | Image | grid mode, stepKey không bắt đầu `grid.` | Ghép ảnh mẫu + sản phẩm |
| `grid` | Image | stepKey bắt đầu `grid.` | Tạo ảnh 3×3 |
| `image` | Image | không ở grid mode | Tạo ảnh cảnh |
| `prompt` | VideoPrompt | — | Viết prompt chuyển động |
| `render` | VideoRender | — | Tạo video |

`compose` và `image` có cùng tiền tố stepKey `image.`, nên chỉ phân biệt được bằng grid mode của cấu hình run (`VideoWorkflowPromptComposer.IsGridMode`). Giả định: cấu hình không đổi trong một run — đổi cấu hình đi qua "Bắt đầu lại" và sinh `CurrentRunId` mới. Test phải khóa giả định này.

**Lần thử hiện hành** của một bước = job mới nhất (theo `CreatedAtUtc`) của bước đó trong `CurrentRunId`. Job cũ hơn chỉ là lịch sử, không ảnh hưởng trạng thái. Giải quyết R3.

### 3.2 Kết quả của lần thử

| Outcome | Điều kiện | Có retry? |
|---|---|---|
| `Pending` | job chưa terminal (Queued/Running/WaitingForCheckpoint…), **hoặc** job Succeeded nhưng chưa có `workflow.applied.{jobId}` (kết quả chưa được apply, bước sau chưa sinh ra) | — |
| `Succeeded` | job Succeeded, không có marker bị loại | — |
| `Rejected` | job Succeeded **và** có marker `workflow.rejected.{jobId}` | Có — tạo job mới (§4.2) |
| `Failed` | job Failed | Có nếu `RetryAsync` cho phép; không thì hiện lý do chặn |
| `Canceled` | job Canceled | Không — người dùng đã chủ động hủy; dùng "Bắt đầu lại" (xem hệ quả ở §7) |

Marker `workflow.rejected.{jobId}` (AppSetting, value = lý do hiển thị) được ghi **trong cùng commit apply** với kết quả bị loại, theo đúng mẫu `workflow.applied.{jobId}` đã có. Một nguồn sự thật cho Script, Image (R6) và VideoPrompt, bền qua restart, không phải suy từ scene meta hay "chưa có cảnh". Giải quyết R1/R2.

### 3.3 Hai loại trạng thái run: điều khiển pipeline và hiển thị

Hiện `GetWorkflowConfigAsync` vừa phục vụ UI vừa được pipeline gọi nội bộ (apply `Pipeline.cs:77`, continuation `Pipeline.cs:993`, auto-render `Pipeline.cs:702`), và nó **suy** `Failed` khi mọi job terminal và có job lỗi. Khi trạng thái suy ra là `Failed`, continuation đang chờ bị xóa và auto-render bị bỏ — nên một cảnh lỗi có thể chặn oan cảnh khác (vd: cảnh A prompt bị loại, cảnh B ảnh vừa Succeeded nhưng intent VideoPrompt chưa enqueue → "mọi job terminal" → Failed → intent của B bị xóa). Plan tách rõ:

| | Trạng thái điều khiển (`RunState` lưu trong config) | Trạng thái hiển thị (`DisplayRunState`) |
|---|---|---|
| Ai dùng | Pipeline: apply, continuation, auto-render, recovery, retry | Chỉ UI qua bridge |
| Nguồn | Giá trị đã lưu, **không suy** | Tính từ `Steps` (§3.2) |
| Thay đổi khi | Start/Pause/Resume/Complete như hiện tại; Script parse lỗi → `Failed` (đã có); retry Script → `Running` | Mỗi lần UI đọc |

Quy tắc `DisplayRunState` (khi lưu là `Running`/`Paused`/`Failed`):

1. Có lần thử hiện hành `Pending`, hoặc còn `workflow.continuation.*` của run → `Running` (hoặc `Paused` nếu đang lưu Paused).
2. Không còn gì đang chờ, có ít nhất một `Failed/Rejected/Canceled` → `Failed` ("dừng tại bước lỗi").
3. Ngược lại = giá trị đã lưu.

Hệ quả:

- Một bước lỗi không bao giờ làm pipeline bỏ việc của bước/cảnh khác; các cảnh còn lại chạy hết rồi UI mới hiện "dừng tại bước lỗi".
- Prompt bị loại không cần ghi `RunState = Failed`: pipeline vẫn `Running` nhưng không còn việc (scene stale bị loại khỏi auto-render), UI hiện dừng nhờ quy tắc 2.
- Có thể trễ một nhịp ngắn giữa lúc apply prompt hợp lệ và lúc auto-render enqueue (không phải intent); chỉ ảnh hưởng hiển thị trong một lần refresh, không ảnh hưởng pipeline.
- Không cần commit job và config chung transaction: crash giữa hai lần ghi vẫn hiển thị đúng vì `DisplayRunState` tính từ queue.

## 4. Thiết kế

### 4.1 Contract

- `ProjectWorkflowConfigDto` thêm `Steps` (danh sách lần thử hiện hành của mỗi bước logic trong run — `kind`, `sceneId?`, `jobId`, `jobRevision`, `outcome`, `reasonMessage?`, `canRetry`, `blockedReason?`, `attemptCount`) và `DisplayRunState`. Tính lúc đọc; không lưu thêm.
- Tách đường đọc: pipeline dùng hàm đọc cấu hình đã lưu (không suy, không tính `Steps`); chỉ handler bridge `workflow.get_config` gọi bản đầy đủ có `Steps` + `DisplayRunState`. Lý do: pipeline gọi đọc config nhiều lần cho mỗi job; tính `Steps` (đọc job, marker, scene meta) ở đó vừa tốn vừa làm lại lỗi §3.3. Khi triển khai, rà mọi caller hiện có của `GetWorkflowConfigAsync` và xếp vào một trong hai nhóm.
- Command `workflow.retry_step`, request: `projectId`, `jobId`, `expectedJobRevision`. Run, kind, scene lấy từ snapshot job — backend không tin định danh từ UI.
- Response: `jobId` (job đang chạy lại), `jobRevision`, `runState`.

### 4.2 Use case `RetryStepAsync`

Chạy dưới `RunUnderProjectGateAsync`. Kiểm theo thứ tự, lỗi nào trả thông điệp tiếng Việt cụ thể:

1. Job thuộc project và `CurrentRunId`; là lần thử hiện hành của bước; outcome là `Failed` hoặc `Rejected`; run không ở `Idle/Completed`. Run `Paused` **được** retry: job chạy lại, nhưng apply thấy `isPaused` nên không sinh bước sau; người dùng bấm Tiếp tục thì luồng đi tiếp. Lý do: nếu bắt bấm Tiếp tục trước, `ResumeWorkflowAsync` có thể enqueue lại bước lỗi với stepKey cũ và bị idempotency trả về đúng job đã lỗi (R4) — người dùng kẹt.
2. Đầu vào trong snapshot còn tồn tại: storyboard, asset ảnh và file thực, media role. Thiếu thì báo chặn, **không** tự tạo lại bước trước.
3. Thực hiện theo outcome:

| Outcome | Hành động | Ngân sách |
|---|---|---|
| `Failed` | `JobService.RetryAsync(jobId, expectedRevision)` — cùng job, cùng snapshot. Mọi guard hiện có (login, storage, capability, render đã submit chưa đối soát) giữ nguyên và trả `blockedReason`. | `Attempts < MaxAttempts` của job (đã có). |
| `Rejected` | `EnqueueAsync` job mới: cùng TaskType/Scene/Run; prompt = `CustomPromptText` gốc lấy ra từ `PromptSnapshotText` của job nguồn (trường này là JSON đã serialize, không gửi nguyên chuỗi); MediaSnapshot sao chép từ `ConfigSnapshotJson` nguồn; `WorkflowStepKey = "{stepKey nguồn}#retry.{jobId nguồn}"`. **Provider và thư mục lưu lấy theo cài đặt hiện tại** — `JobSnapshotFactory` luôn phân giải từ settings, plan không thêm đường bypass. | Tối đa 3 job cho một bước logic trong một run; vượt thì `blockedReason` "đã thử 3 lần, hãy sửa mẫu/cấu hình rồi bắt đầu lại". |

4. Nếu `RunState` lưu là `Failed` (chỉ xảy ra với Script parse lỗi) thì lưu lại `Running`; `Paused` giữ nguyên; `Running` không đổi. `JobService` tự wakeup worker; worker apply cần cùng Project gate nên chỉ chạy sau khi use case xong. `JobService` không lấy Project gate → không deadlock.

**Vì sao stepKey `#retry.{jobId nguồn}`:** giải quyết R4 và tự chống trùng — bấm đúp, hai request song song hay gửi lại sau crash từ cùng job nguồn đều ra cùng idempotency key → cùng một job. Lần retry sau dựa trên job mới nên ra key mới. Vì nhận diện bước bằng `(Kind, SceneId)` (§3.1), hậu tố này không ảnh hưởng việc group; tiền tố `grid.` vẫn giữ nên nhận diện grid không đổi.

### 4.3 Apply và continuation

- Apply VideoPrompt bị loại: ghi marker `workflow.rejected.{jobId}` (lý do = `promptRejectReason`) trong cùng commit; phần còn lại giữ như hiện tại (stale, NeedsReview, không render).
- Apply Script parse lỗi: ghi thêm marker cùng commit với `RunState = Failed` đã có.
- Apply Image (compose/grid/image) có `ResultReference` rỗng: ghi marker (lý do "Provider báo xong nhưng không trả về ảnh"), **không** gán `scene.ImageAssetId`, không ghi marker grid/compose, không tạo continuation. Ảnh và kết quả bước trước giữ nguyên. Retry tạo job mới cùng bước như mọi trường hợp `Rejected` — với ảnh 3×3 nghĩa là chạy lại đúng ảnh 3×3 từ ảnh ghép đã có, không ghép lại.
- Apply prompt hợp lệ của job retry: đã xóa stale của scene như hiện tại → auto-render guard cho đi tiếp, enqueue render đúng một lần (key render đã có chống trùng theo số lần).
- Job thuộc run cũ apply muộn: đã bị chặn theo RunId — chỉ thêm test, không sửa.

### 4.4 UI

| Trạng thái | Hiển thị |
|---|---|
| `DisplayRunState = Failed` | Giữ thẻ tiến trình, ảnh, nhật ký; không tự về form. Nút "Bắt đầu lại" vẫn có cho trường hợp muốn đổi cấu hình. |
| Run `Paused` có bước lỗi | Nút retry vẫn hiện; sau khi bước chạy xong hiện "Đã tạm dừng — bấm Tiếp tục để chạy bước sau". |
| Bước `Failed/Rejected`, `canRetry` | Icon lỗi + `reasonMessage` + nút **Thử lại bước này**. |
| Bước `Failed/Rejected`, không `canRetry` | Icon lỗi + `reasonMessage` + `blockedReason` (vd: cần đăng nhập lại, đã thử 3 lần). |
| Bước `Canceled` | "Đã hủy", không nút retry. |
| Đang gửi command | Nút khóa, "Đang đưa vào hàng đợi". |
| Bước `Pending` | "Chờ chạy"; spinner chỉ khi job Running thật. |

- Mốc của `StoryboardGridProgress` lấy từ `Steps` thay vì suy từ asset/prompt → prompt cũ còn trong bảng không làm mốc hiện "Hoàn tất" (R5).
- Nhiều bước lỗi (nhiều cảnh): mỗi bước một nút, không chọn ngầm.
- UI dùng `DisplayRunState` cho spinner/thẻ dừng, không dùng `RunState` lưu.
- Sau command: refresh config qua đường hiện có; bỏ qua response của Project khác. Lỗi command → hiện lý do, mở lại nút. Riêng lỗi concurrency (`JobConcurrencyConflict`, vd: bấm đúp với job Failed — lần hai thấy revision đã tăng) **không** hiện là lỗi: coi là "đã được nhận", chỉ refresh.
- Component/token sẵn có, `aria-label` cho nút, không hộp xác nhận.

## 5. Các bước triển khai

### Bước 1 — Mô hình trạng thái (backend)

Files: `Contracts/Content/VideoWorkflowDtos.cs`, `ProjectWorkflowService.Config.cs`, `.Pipeline.cs`; test `tests/Fraylo.Desktop.SmokeChecks/VideoWorkflowSmokeTests.cs`.

- [ ] Helper xác định `(Kind, SceneId)` từ job; group lần thử hiện hành theo bước logic.
- [ ] Ghi marker `workflow.rejected.{jobId}` ở ba nhánh bị loại: Script parse lỗi, VideoPrompt bị loại, Image không có ảnh (chặn gán ảnh rỗng và continuation, §4.3).
- [ ] Tách đường đọc config (§4.1): rà mọi caller của `GetWorkflowConfigAsync`; pipeline dùng bản đã lưu, bỏ phép suy `Failed` hiện có khỏi đường pipeline.
- [ ] Tính `Steps` và `DisplayRunState` theo §3.2–3.3, chỉ ở đường đọc của bridge.
- [ ] Test SQLite: nhiều cảnh — cảnh A prompt bị loại trong khi cảnh B có ảnh Succeeded chưa apply/intent chưa enqueue → cảnh B vẫn đi hết tới render, sau đó mới `DisplayRunState = Failed`; prompt bị loại → `DisplayRunState = Failed`, step `prompt` = `Rejected` có lý do; restart (đọc lại DbContext mới) vẫn đúng; job lỗi cũ + job mới thành công cùng bước → không `Failed`; compose và grid tách đúng (grid mode) và job Image ở chế độ thường là `image`; job grid Succeeded với `ResultReference` rỗng → step `grid` = `Rejected`, ảnh cảnh và ảnh ghép không đổi, không có job VideoPrompt.

### Bước 2 — Retry (backend)

Files: `ProjectWorkflowService.Lifecycle.cs` (hoặc partial mới `.Retry.cs`); test `VideoWorkflowSmokeTests.cs`, `JobRetryAndCancelSmokeTests.cs`, `PvRemediationRegressionTests.cs`.

- [ ] `RetryStepAsync` theo §4.2.
- [ ] Test theo kịch bản:
  - Ảnh của người dùng: prompt bị loại → retry → prompt hợp lệ → đúng một render; ID storyboard, ảnh ghép, ảnh 3×3 không đổi.
  - Retry → bị loại lần nữa → `Rejected` với lý do mới, `attemptCount = 2`; lần thứ 3 xong thì chặn.
  - Rejected: gọi hai lần cùng job nguồn → một job. Failed: gọi hai lần → lần hai `JobConcurrencyConflict`, job chỉ được requeue một lần.
  - Rejected retry: prompt gửi đi bằng đúng `CustomPromptText` gốc (không phải chuỗi JSON snapshot); đổi provider trong settings trước khi retry → job mới dùng provider mới.
  - Run `Paused`: retry chạy bước lỗi, không sinh bước sau; bấm Tiếp tục → đi tiếp.
  - Script parse lỗi → retry → có cảnh.
  - Ảnh 3×3 `Failed` → `RetryAsync` cùng job; ảnh ghép giữ ID, không có job compose mới; xong thì đi tiếp prompt → render. Hết attempts → `canRetry = false`.
  - Ảnh 3×3 Succeeded không có ảnh → retry tạo job grid mới từ ảnh ghép cũ → có ảnh → đi tiếp.
  - Render đã submit chưa đối soát → không gửi lại.
  - Job của run cũ / project khác / run Idle hoặc Completed / job không phải lần thử hiện hành → từ chối.
  - Thiếu asset đầu vào → chặn, không tạo lại bước trước.

### Bước 3 — Bridge và UI

Files: `Desktop/Bridge/Handlers/VideoWorkflowHandlers.cs` (allow-list + handler), `Web/src/features/projects/videoWorkflowApi.ts`, `VideoProductionTab.tsx`, `StoryboardGridProgress.tsx`; test `videoProductionTab.test.ts`.

- [ ] Đăng ký command, ánh xạ lỗi validation/concurrency.
- [ ] Hiển thị theo bảng §4.4; mốc lấy từ `Steps`.
- [ ] DOM test: mô phỏng đúng ảnh (Script + grid ✓, prompt Rejected) → nút đúng bước → bấm → khóa nút → Pending → Succeeded; `canRetry = false` hiện lý do; Canceled không có nút; command lỗi mở lại nút; lỗi concurrency chỉ refresh, không hiện lỗi; Paused vẫn có nút retry.

### Bước 4 — Regression và tài liệu

- [ ] Smoke `workflow`, `retry`, `remediation`, `recovery`; `pnpm test`, `pnpm build` tại `src/Fraylo.Web`.
- [ ] Build Desktop Release (output riêng nếu app đang khóa file); không dừng app cá nhân.
- [ ] Kiểm thủ công Light/Dark và focus bàn phím.
- [ ] Cập nhật STATUS/TASKS và tài liệu workflow; ghi rõ PASS/NOT_RUN; bỏ hướng dẫn "sửa cấu hình rồi bắt đầu lại" cho lỗi retry được.

Mỗi bước xong cùng test của nó mới sang bước sau. Không mở provider thật trong regression tự động; không chỉnh dữ liệu/profile cá nhân.

## 6. Tiêu chí nghiệm thu

| # | Kịch bản | Kỳ vọng |
|---|---|---|
| A1 | Ảnh của người dùng | Run dừng tại prompt, không spinner; retry chỉ chạy lại prompt; storyboard/ảnh giữ ID; prompt hợp lệ → đúng một render. |
| A2 | Retry bị loại tiếp | Dừng lại đúng bước, lý do mới; lịch sử có cả hai job; lần thứ 3 xong thì chặn với lý do rõ. |
| A3 | Mỗi kind (script/compose/grid/image/prompt/render) lỗi | Có nút retry hoặc lý do không retry được; không có trạng thái "lỗi mà không biết làm gì". |
| A3b | Ảnh 3×3 lỗi (Failed hoặc không trả ảnh) | Dừng tại mốc "Tạo ảnh 3×3"; retry chỉ chạy lại ảnh 3×3 từ ảnh ghép cũ; không chạy prompt/render trên ảnh thiếu. |
| A3c | Nhiều cảnh, một cảnh lỗi | Các cảnh khác chạy hết tới render; UI chỉ hiện "dừng tại bước lỗi" khi không còn việc đang chờ. |
| A4 | Bấm đúp / request trùng | Một thao tác queue; UI không báo lỗi giả. |
| A4b | Retry khi Paused | Bước lỗi chạy lại; bước sau chỉ chạy khi bấm Tiếp tục. |
| A5 | Bước đã phục hồi | Job lỗi cũ không giữ run ở Failed. |
| A6 | Mở lại app giữa các lần thử | Trạng thái và nút giống trước khi đóng; không tự chạy lại. |
| A7 | Render đã được provider nhận | Không gửi lại; checkpoint chưa giải quyết vẫn chặn. |
| A8 | Guard hiện có | Cancel/pause/attempts/login/storage/capability giữ nguyên; không phục hồi tự retry (NR1). |

## 7. Quyết định và đánh đổi

- **Không đổi job Succeeded thành Failed giả** cho kết quả bị loại: provider thực sự trả về; giữ đúng để log/lịch sử và attempts của job phản ánh thật. Thay vào đó dùng outcome `Rejected` riêng.
- **Rejected tạo job mới thay vì requeue job cũ**: job cũ đã có kết quả và marker applied; ghi đè sẽ mất lịch sử và phá idempotency của apply.
- **Pipeline dùng trạng thái đã lưu, UI dùng trạng thái suy ra** (§3.3): một bước lỗi không được phép làm pipeline bỏ việc của bước/cảnh khác; "dừng tại bước lỗi" là khái niệm hiển thị.
- **Retry giữ prompt và đầu vào đã lưu; provider khác nhau theo nhánh**:
  - `Failed` → `RetryAsync` chạy lại đúng job, giữ cả provider cũ trong snapshot.
  - `Rejected` → job mới qua `EnqueueAsync`, provider/thư mục lưu theo cài đặt **hiện tại** (đó là cách `JobSnapshotFactory` hoạt động). Chấp nhận vì kết quả bị loại thường do provider trả lời kém; người dùng có thể đổi provider rồi retry. Không thêm đường bypass snapshot.
  - Template/cấu hình workflow không đọc lại ở cả hai nhánh; muốn đổi thì "Bắt đầu lại".
- **Cho retry khi Paused**: tránh kẹt do Tiếp tục enqueue lại bước lỗi với stepKey cũ (R4); Pause vẫn có nghĩa là không sinh bước sau.
- **Canceled không retry**: người dùng đã chủ động dừng; tránh bấm nhầm khởi động lại thứ họ vừa hủy. **Hệ quả đã chấp nhận:** hủy một job bị treo thì chỉ còn "Bắt đầu lại" toàn bộ, mất storyboard/ảnh của run. Nếu sau này cần, mở rộng bằng cách coi `Canceled` như `Rejected` (tạo job mới) — `RetryAsync` từ chối `UserCanceled` nên không dùng được nhánh Failed.

## 8. Kết quả triển khai và khác biệt so với plan

**Khác biệt:**

- Không có trường `DisplayRunState` riêng. `GetWorkflowConfigAsync` và `GetWorkflowConfigWithStepsAsync` trả `RunState` **đã là trạng thái hiển thị**; `GetStoredWorkflowConfigAsync` (private) trả trạng thái điều khiển cho pipeline và các thao tác ghi (Pause/Resume/Cancel/Retry). Các caller còn lại (Start, tạo video từng cảnh, sửa đầu vào cảnh) giữ hành vi cũ vì vốn dùng bản đã suy.
- `Steps` có thêm `reasonCode` (UI dùng `formatReasonCode` sẵn có) bên cạnh `reasonMessage`.
- Kiểm đầu vào khi retry dừng ở mức Asset còn `Linked` trong CSDL; không kiểm tệp vật lý (adapter provider kiểm lúc chạy và báo lỗi job).
- Script trả về rỗng cũng được ghi `Rejected`. Script parse lỗi vẫn lưu `RunState = Failed` như cũ; retry lưu lại `Running`.
- Cần rút điều kiện retry của `JobService.RetryAsync` thành `EvaluateRetryEligibilityAsync` dùng chung với `GetRetryBlockReasonAsync` để hiển thị `canRetry` trước khi bấm.
- Bấm lặp một job đã có lần thử mới hơn trả `CONCURRENCY_CONFLICT` (cả nhánh Failed lẫn Rejected); UI chỉ tải lại.

**Kiểm chứng (2026-11-02):** smoke `workflow` 28/28 (ca 23–28 mới), `retry` 9/9, `remediation` 14/14, `recovery` 12/12, `project-first` PASS; `npm test` 46/46 (3 DOM test mới); `tsc` và `vite build` PASS; Desktop Release build vào thư mục riêng 0 warning, 0 error.

**NOT_RUN:** provider/WebView2 thật; kiểm thủ công Light/Dark và focus bàn phím của nút retry; test riêng cho retry VideoRender đã submit (dùng nguyên guard của `RetryAsync`); build vào `src/Fraylo.Desktop/bin/Release` chính.

## 9. Giới hạn của plan (lúc lập, trước khi triển khai)

Lập từ đọc source và ảnh người dùng; chưa sửa code, chưa build/test, chưa tái hiện với provider thật. Chưa kiểm: `ResumeWorkflowAsync` xử lý bước lỗi cụ thể thế nào (lý do cho phép retry khi Paused dựa trên rủi ro, chưa tái hiện); job render ở chế độ một video có `SceneId` hay không (ảnh hưởng nhận diện bước `render`). Kịch bản thủ công sau khi code xong: profile thử riêng, cố ý nhận prompt sai định dạng, retry, xác nhận không tạo lại ảnh và render một lần; thử lỗi lần hai và mở lại app giữa các lần thử. Không thêm dependency, migration hay tự retry; không commit/push hoặc đăng video.
