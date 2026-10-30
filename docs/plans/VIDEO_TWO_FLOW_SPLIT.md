# Tách luồng tạo video: nhiều video và một video nhiều phân cảnh

> **Bổ sung đang lập kế hoạch 2026-10-30:** [Ảnh storyboard 9 ô](20261030-storyboard-contact-sheet.md) thêm lựa chọn cho `SingleVideo`: tạo một ảnh 3×3 rồi gửi nguyên ảnh để render một video 15 giây. Khi lựa chọn này được triển khai, nó thay quy định dùng ảnh sản phẩm gốc tại §3.3/D2 trong phạm vi đó; luồng Direct giữ hành vi hiện tại. Chưa phải tính năng đã triển khai.

Cập nhật 2026-10-30. Trạng thái: **ĐÃ TRIỂN KHAI backend và web (chưa kiểm chứng render thật)**; chi tiết và phần còn lại ở [STATUS](../agents/STATUS.md) mục 2026-10-30. Liên quan: [MULTISCENE_SINGLE_VIDEO](MULTISCENE_SINGLE_VIDEO.md), [VIDEO_FIRST_ARCHITECTURE_PLAN](VIDEO_FIRST_ARCHITECTURE_PLAN.md), [STATUS](../agents/STATUS.md).

## 1. Yêu cầu

Người dùng muốn một run có thể chọn một trong hai luồng:

- **Luồng A — Nhiều video:** mỗi phân cảnh là một video độc lập. Giữ hành vi như thiết kế ban đầu (mỗi cảnh: ảnh → prompt video → render → video).
- **Luồng B — Một video, nhiều phân cảnh:** AI viết một prompt duy nhất gồm nhiều đoạn có mốc thời gian (ví dụ `0–3s`, `3–8s`, `8–15s`). Mốc do AI tự chọn. Tất cả đoạn nằm trong **một** prompt và cho **một** video hoàn chỉnh.

Vấn đề cần sửa trong luồng nhiều phân cảnh hiện tại: **phân cảnh không có ảnh riêng, chỉ có prompt**.

## 2. Hiện trạng trong code (đã đối chiếu)

| Điểm | Vị trí | Thực tế |
|---|---|---|
| Nhiều cảnh bị gom thành một render | [ProjectWorkflowService.ContentItems.cs](../../src/TiToAI.Application/Content/ProjectWorkflowService.ContentItems.cs) `CreateSceneVideoAsync` (dòng 311–314) | Khi Project có >1 Scene, luôn gọi `EnqueueProjectRenderAsync`: một job render cho cả Project. |
| Chặn luồng nhiều video | Cùng file, dòng 330–347 | Khi đã có video khác hoàn thành hoặc đang render, báo "Mỗi dự án chỉ có một video đầu ra". Vì vậy luồng A **không chạy được** với >1 cảnh trong code hiện tại. "Giữ nguyên" thực tế nghĩa là khôi phục đường này cho chế độ A. |
| Ảnh riêng chỉ có ở ModelAndProduct | [ProjectWorkflowService.Scenes.cs](../../src/TiToAI.Application/Content/ProjectWorkflowService.Scenes.cs) dòng 344–345; [ProjectWorkflowService.Config.cs](../../src/TiToAI.Application/Content/ProjectWorkflowService.Config.cs) `CanGenerateImage` dòng 303–304 | Job Image chỉ được enqueue khi `WorkflowMode == ModelAndProduct` và cảnh chưa có ảnh. |
| Cảnh storyboard không có ảnh riêng | [ProjectWorkflowService.Pipeline.cs](../../src/TiToAI.Application/Content/ProjectWorkflowService.Pipeline.cs) dòng 163–164 | `ImageAssetId` = ảnh chung của ReadyImage/ProductOnly, hoặc `null` với ModelAndProduct. Với hai mode còn lại, mọi cảnh dùng chung một ảnh, không có ảnh theo cảnh. |
| Prompt ảnh theo cảnh | Pipeline.cs dòng 919 và Scenes.cs dòng 347 | `imagePrompt` của mỗi shot được lưu làm `scene.plan.<id>` và dùng làm prompt job Image. Đường này chưa được xác nhận chạy trên provider thật (MULTISCENE doc ghi "chưa xác nhận Image job tạo ảnh cho từng shot"). |
| Storyboard bắt buộc `imagePrompt` | [StoryboardContent.cs](../../src/TiToAI.Application/Content/StoryboardContent.cs) dòng 52 | Luồng B không có ảnh theo đoạn nên không nên bắt buộc trường này. |
| Auto render gom cả Project | Pipeline.cs `TryAutoEnqueueVideoRenderForProjectAsync` dòng 603–661 | Chỉ render khi **mọi** cảnh đã có ảnh và prompt, rồi gọi `CreateSceneVideoAsync` (gom một render). |
| Cấu hình lưu JSON | `project.workflow.config.<projectId>` trong AppSettings ([VideoWorkflowDtos.cs](../../src/TiToAI.Contracts/Content/VideoWorkflowDtos.cs) `ProjectWorkflowConfigDto`, dòng 98) | Thêm trường cấu hình không cần đổi schema SQLite (đang `user_version=3`). |
| Thời lượng không khớp | Backend [ProviderCapabilities.cs](../../src/TiToAI.Application/Providers/ProviderCapabilities.cs) dòng 171: Muse `10, 15, 30`; web [videoWorkflowApi.ts](../../src/TiToAI.Web/src/features/projects/videoWorkflowApi.ts) dòng 170 và [SceneForm.tsx](../../src/TiToAI.Web/src/features/projects/SceneForm.tsx) dòng 87, 280: `10, 15` | Cần đồng bộ khi làm luồng B (tối đa 30s). |

Kết luận: hai luồng hiện đang trộn trong cùng một đường gom render. Tách cần làm **ở mức run**, không thêm nhánh rải rác trong từng hàm.

## 3. Thiết kế đề xuất

### 3.1 Chế độ theo run

Thêm `VideoStrategy` vào `ProjectWorkflowConfigDto` (chọn khi tạo/bắt đầu run, đóng băng trong snapshot của run):

- `MultiVideo` — luồng A; cấu hình cũ thiếu trường chiến lược tiếp tục dùng luồng này.
- `SingleVideo` — mặc định cho cấu hình mới, luồng B.

Đổi chế độ khi run đang chạy bị từ chối. Hằng số đặt cạnh `WorkflowModes` trong `VideoWorkflowDtos.cs`, đúng kiểu hiện có.

### 3.2 Luồng A — Nhiều video (giữ kiến trúc hiện tại, bỏ gom)

1. Script trả danh sách cảnh, mỗi cảnh có đủ hai prompt: `imagePrompt` (tạo ảnh) và `videoPrompt` (tạo video từ ảnh đó). Số cảnh = `TargetSceneCount`.
2. Mỗi cảnh: job Image dùng `imagePrompt` và **gửi kèm ảnh sản phẩm gốc** (`ProductAssetId`) làm tham chiếu để giữ nhất quán sản phẩm giữa các cảnh. Áp dụng cho ProductOnly và ModelAndProduct. ProductOnly không có ảnh mẫu người, nên prompt không dùng nhãn "người mẫu" như hiện tại.
3. Cảnh có ảnh → render riêng cho cảnh đó với `videoPrompt` của cảnh và **ảnh do chính cảnh sinh ra** (`ImageAssetId` của cảnh). Không dùng ảnh sản phẩm gốc làm nguồn video. Job VideoPrompt AI không còn bắt buộc vì Script đã trả `videoPrompt`; giữ lại làm đường tạo lại khi người dùng yêu cầu.
4. Không gom, không dùng `ImageAssetIds` nhiều ảnh. Mỗi cảnh một video. Run hoàn tất khi mọi cảnh có video hợp lệ.
5. Video lưu vào `Scene.VideoAssetId`. Run hoàn tất khi mọi cảnh có video hợp lệ.
6. Bỏ chặn "Mỗi dự án chỉ có một video đầu ra" khi `VideoStrategy == MultiVideo`. Giữ chặn "một render đang chạy cho cùng cảnh".

Với ReadyImage: không có bước sinh ảnh, mọi cảnh dùng ảnh sẵn sàng đã chọn (xem D1).

### 3.3 Luồng B — Một video, nhiều phân cảnh

1. Script trả **storyboard theo đoạn** (schema mới, không có `imagePrompt` bắt buộc):
   `{"durationSeconds": 15, "segments": [{"startSeconds": 0, "endSeconds": 3, "description": "..."}, ...]}`.
2. Validate: đoạn bắt đầu từ 0, liên tiếp không hở/chồng, đoạn cuối kết thúc tại `durationSeconds` do AI đặt, và `durationSeconds` nằm trong dung sai quanh thời lượng cấu hình (D3). Mỗi đoạn từ 1 giây trở lên. Đoạn dài hơn 10 giây chỉ cảnh báo trong nhật ký (D4).
3. Lưu storyboard qua `StoryboardContentService` (kiểu mới hoặc mở rộng, không đè dữ liệu luồng A).
4. **Không** tạo job Image theo đoạn (D2). Ảnh tham chiếu là ảnh chung của run (model + product, hoặc ảnh sẵn sàng).
5. Job VideoPrompt một lần: composer nhận toàn bộ storyboard và trả một `video_prompt` có mốc thời gian (`0–3 giây: ...`). Dùng cùng contract JSON `video_prompt` đã có để thẩm định.
6. Khi prompt hợp lệ → **một job render** cho run, một Scene đại diện, `DurationSeconds` = thời lượng cấu hình (không ép đúng từng giây, xem D3), `ImageAssetIds` = ảnh sản phẩm gốc (ảnh tham chiếu duy nhất).
7. Video kết quả là `FinalVideoAssetId` của run.

Gợi ý kỹ thuật: có thể tái dùng `ProjectWorkflowService.BuildProjectRenderPrompt`, nhưng nó đang ghép theo cảnh. Với luồng B, nên dùng prompt do AI viết (đã có mốc thời gian) thay vì ghép lại.

### 3.4 Chung cho cả hai luồng

- **Lifecycle** (`ProjectWorkflowService.Lifecycle.cs` dòng 90–127, 150): kiểm tra đầu vào theo `VideoStrategy` + `WorkflowMode`.
- **Auto render** (Pipeline.cs 603–661): nhánh A render theo từng cảnh khi cảnh đó sẵn sàng; nhánh B render một lần sau khi VideoPrompt của run được áp dụng. Khóa `workflow.auto_render.<project>.<run>` đổi thành khóa theo cảnh (A) hoặc theo run (B).
- **UI** `VideoProductionTab.tsx`: chọn chế độ bằng `RadioGroup` (có sẵn trong `components/ui`). Chế độ B hiển thị một hàng tổng + danh sách đoạn chỉ đọc; ẩn cột ảnh theo đoạn.
- **Thời lượng**: một nguồn sự thật từ `ProviderCapabilities`; web lấy qua API thay vì hằng số `[10, 15]`.

## 4. File dự kiến chạm

Các file sau đang có thay đổi chưa commit trong `git status` (14 file, gồm `ProjectWorkflowService.*`, `VideoWorkflowPromptComposer.cs`, `JobService.cs`). **Cần commit hoặc xác nhận trạng thái trước khi bắt đầu**, để không trộn thay đổi của nhiều việc.

- `src/TiToAI.Contracts/Content/VideoWorkflowDtos.cs` — `VideoStrategy`, `ProjectWorkflowConfigDto`
- `src/TiToAI.Contracts/Content/ContentCreationDtos.cs` — DTO request/response
- `src/TiToAI.Application/Content/ProjectWorkflowService.Config.cs` — mặc định, đổi chế độ, `CanGenerateImage`
- `src/TiToAI.Application/Content/ProjectWorkflowService.Lifecycle.cs` — kiểm tra khi bắt đầu run
- `src/TiToAI.Application/Content/ProjectWorkflowService.Pipeline.cs` — áp dụng Script/Image/VideoPrompt, auto render
- `src/TiToAI.Application/Content/ProjectWorkflowService.ContentItems.cs` — `CreateSceneVideoAsync`, `EnqueueProjectRenderAsync`
- `src/TiToAI.Application/Content/ProjectWorkflowService.Scenes.cs` — `EnqueueJobsForExistingScenesAsync`
- `src/TiToAI.Application/Content/StoryboardContent.cs` — parser storyboard theo đoạn
- `src/TiToAI.Application/Content/VideoWorkflowPromptComposer.cs` — prompt cho luồng B
- `src/TiToAI.Application/Providers/ProviderCapabilities.cs` — nguồn thời lượng duy nhất
- `src/TiToAI.Web/src/features/projects/VideoProductionTab.tsx`, `videoWorkflowApi.ts`, `SceneForm.tsx`
- Test: `tests/TiToAI.Desktop.SmokeChecks/VideoWorkflowSmokeTests.cs`, `tests/TiToAI.Storyboard.SmokeChecks/*`, `src/TiToAI.Web/src/features/projects/videoProductionTab.test.ts`

Schema SQLite: không đổi. Dữ liệu run cũ không cần chuyển đổi (AGENTS cho phép reset khi đổi kiến trúc).

## 5. Thứ tự triển khai

1. Chốt contract: `VideoStrategy`, schema storyboard theo đoạn, thời lượng từ `ProviderCapabilities`. Coordinator duyệt trước khi các phần sau đi tiếp.
2. Sửa lỗi ảnh luồng A: reproduce bằng một run ModelAndProduct có 3 cảnh (đếm job Image theo cảnh sau khi Script apply). Sửa nguyên nhân đã tìm thấy, rồi khôi phục render theo cảnh.
3. Luồng B backend: parser, composer, một render cho run.
4. Chặn đầu vào theo chế độ trong Lifecycle; auto render tách nhánh.
5. UI: chọn chế độ, hiển thị đoạn, đồng bộ thời lượng.
6. Test và cập nhật [STATUS](../agents/STATUS.md) sau khi có bằng chứng.

Bước 2 và 3 không chạm cùng vùng code nên có thể chạy song song nếu contract ở bước 1 đã chốt.

## 6. Kiểm chứng tối thiểu

- Parser luồng B: đoạn liên tiếp, tổng đúng, thời lượng không hỗ trợ bị chặn, JSON hỏng bị chặn.
- Luồng A: mỗi cảnh thiếu ảnh có job Image riêng (ModelAndProduct); render theo cảnh không gom; không bị chặn "một video đầu ra".
- Luồng B: một job VideoPrompt, một job render, không có job Image theo đoạn, `ImageAssetIds` đúng thứ tự.
- Recovery sau submit không gửi trùng (giữ nguyên ràng buộc đã có).
- Smoke `workflow`: ca 13 (CD06) đang kỳ vọng 30 giây bị chặn, mâu thuẫn quyết định cho phép 30 giây. Cần sửa theo chế độ mới, không giữ kỳ vọng cũ.
- Muse thật: **NOT_RUN**. Test chỉ dùng provider giả.

## 7. Rủi ro

- Muse tự báo một clip AI tối đa 10 giây. Mốc do AI chọn có thể không khớp điểm cắt thực tế của Muse. Video 15 giây trước đó đã thấy vết cắt quanh giây thứ 10.
- Prompt nhiều mốc dài có thể làm Muse hiểu sai thứ tự. Cần thử thật trước khi coi là ổn định.
- Thay đổi đồng thời ở `ProjectWorkflowService.*` có nguy cơ đè công việc đang làm. Xem mục 4.

## 8. Quyết định đã chốt (người dùng xác nhận 2026-10-29)

- **D1 — Luồng A tạo ảnh theo cảnh từ ảnh sản phẩm gốc:** Script sinh các cảnh, mỗi cảnh có `imagePrompt` và `videoPrompt`. Job Image của mỗi cảnh gửi kèm ảnh sản phẩm gốc để giữ nhất quán sản phẩm. Sau đó mỗi ảnh của cảnh được dùng làm nguồn cho video của cảnh đó. **Không** dùng một ảnh ban đầu để tạo mọi video, vì như vậy các video gần như giống nhau. Áp dụng cho ProductOnly và ModelAndProduct. ReadyImage không sinh ảnh.
- **D2 — Luồng B không có ảnh theo đoạn:** đã chốt. Chỉ dùng ảnh sản phẩm gốc làm tham chiếu.
- **D3 — Thời lượng là cấu hình, không ép cứng:** AI chia đoạn và được phép lệch quanh thời lượng cấu hình (ví dụ cấu hình 15 giây thì chấp nhận khoảng 14–16 giây). Dung sai cụ thể là hằng số trong code, khuyến nghị ±2 giây, để chỉnh sau khi thử Muse thật. Không để render bị khóa cứng vào đúng một con số.
- **D4 — Đoạn dài hơn 10 giây:** chỉ cảnh báo, không chặn (đã chốt).

## 8.1 Hệ quả của D1 cần làm rõ khi triển khai

- Prompt Image hiện tại cho ModelAndProduct ghi "người mẫu" và yêu cầu hai ảnh đính kèm. ProductOnly cần prompt riêng, chỉ có ảnh sản phẩm.
- Script phải trả `imagePrompt` và `videoPrompt` cho từng cảnh. Đây là thay đổi contract của Script, nên cần đổi cả parser `ExtractScenePlanItems` và template Script.
- Nếu Script trả thiếu `videoPrompt`, cảnh đó đi qua job VideoPrompt AI như hiện tại (làm dự phòng).
- Cần kiểm tra Muse nhận ảnh sinh ra theo cảnh khác nhau và giữ được sản phẩm; đây là điểm chưa kiểm chứng.

## 9. Ghi chú

- Plan này không cấp quyền thực hiện thay đổi. Triển khai cần bước xác nhận D1–D4 và trạng thái working tree.
- Không sửa `STATUS.md` ở bước plan. Cập nhật khi có bằng chứng kiểm chứng.
