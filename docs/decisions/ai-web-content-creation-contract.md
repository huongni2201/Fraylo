# Fraylo — Quyết định Kiến trúc & Hợp đồng Tạo nội dung qua AI Web (AC0)


Ngày 2026-10-27 (UTC+07).  
Trạng thái: **CHỐT CONTRACT (AC0 HOÀN TẤT) & HƯỚNG DẪN AI TỰ NHIÊN (AC1 HOÀN TẤT)**.  
Nguồn tham chiếu: [SOURCE](../plans/SOURCE_PLAN.md) §7.4,9; [AI Web Plan](../plans/AI_WEB_CONTENT_CREATION_PLAN.md); [Bộ Prompts](../plans/AI_WEB_CONTENT_CREATION_PROMPTS.md); [ORCHESTRATION](../agents/ORCHESTRATION.md); [ENGINEERING_RULES](../agents/ENGINEERING_RULES.md).

---

## 1. Mục đích và Ranh giới

Tài liệu này xác lập hợp đồng dữ liệu, DTOs, operations, cơ chế kiểm tra tính tuân thủ và ranh giới ownership cho tính năng tạo nội dung hai luồng qua AI Web (ChatGPT Web / Gemini Web):
1. **Tôi có ý tưởng (P0 nền tảng):** Người dùng nhập ý tưởng / nội dung / kịch bản; AI web hoàn thiện kịch bản, chia cảnh và viết prompt video tiếng Anh theo từng cảnh qua các bước duyệt/áp dụng.
2. **AI đề xuất ý tưởng (P1 phân tích):** Gửi analytics snapshot đúng kênh cho AI web phân tích, trả 5 ý tưởng có bằng chứng và bài đăng TikTok; người dùng chọn ý tưởng để chuyển tiếp sang luồng 1.
3. **Tab Prompt video:** Lưu hướng dẫn tự nhiên có versioning đơn điệu. AI tự quyết định góc máy, chuyển động camera, ánh sáng và không khí; không bắt buộc người dùng điền các biến kỹ thuật.
4. **Bài đăng TikTok:** Duy nhất một bài đăng TikTok cấp Project theo [SOURCE](../plans/SOURCE_PLAN.md) §7.4, gồm caption và hashtags; không nhân bản caption theo từng cảnh.

---

## 2. Danh mục Operations và Purpose Binding

Hệ thống giữ nguyên 3 mục đích Prompt cốt lõi: `Script`, `Image`, `VideoPrompt`. Tuyệt đối không tạo purpose thứ 4 gây phá vỡ schema:

| Operation | Purpose áp dụng | Provider cấu hình (Settings) | Đầu vào chính | Đầu ra mong đợi |
|---|---|---|---|---|
| `SuggestIdeas` | `Script` | `ai.providers.script` | `AnalyticsContext`, `ChannelProfile`, `RecentIdeas` | Phân tích kênh + 5 ý tưởng có `evidenceRefs` & `tiktokPost` |
| `GenerateScript` | `Script` | `ai.providers.script` | template đã render hoặc `selectedIdea`, `ChannelProfile` | Kịch bản, danh sách cảnh (`scenes`) & `tiktokPost` |
| `GenerateVideoPrompts` | `VideoPrompt` | `ai.providers.videoPrompt` | Kịch bản đã duyệt, danh sách cảnh, ảnh tham chiếu | Prompt tiếng Anh per-scene (`promptEn`) & `tiktokPost` |

---

## 3. Danh mục DTOs và Schema Contract

Các DTOs đã được định nghĩa tại `src/Fraylo.Contracts/Content/ContentCreationDtos.cs`:

### 3.1. Hợp đồng Bài đăng TikTok (`TikTokPostDto`)
- `caption` (`string`): Đoạn văn bản mô tả video, hook và lời kêu gọi hành động (CTA).
- `hashtags` (`IReadOnlyList<string>`): Danh sách thẻ hashtag liên quan (ví dụ: `["#productivity", "#morningroutine"]`).

### 3.2. Hợp đồng Đề xuất Ý tưởng (`SuggestIdeasOutputDto`)
- `analysis` (`ChannelAnalysisDto`):
  - `observations` (`IReadOnlyList<string>`): Các phát hiện có bằng chứng số liệu.
  - `limitations` (`IReadOnlyList<string>`): Giới hạn dữ liệu kỳ phân tích (nêu rõ dữ liệu thiếu).
  - `analyzedVideoCount` (`int`): Số lượng video thực tế được phân tích.
  - `periodDescription` (`string?`): Mô tả khoảng thời gian phân tích.
- `ideas` (`IReadOnlyList<IdeaSuggestionDto>`):
  - `ideaId` (`string`): Định danh duy nhất và ổn định của ý tưởng trong lần đề xuất.
  - `title` (`string`): Tiêu đề ý tưởng.
  - `hook` (`string`): Câu hook mở đầu video.
  - `summary` (`string`): Tóm tắt nội dung chính.
  - `suggestedDurationSeconds` (`int`): Thời lượng gợi ý (giây).
  - `rationale` (`string`): Lý do lựa chọn phù hợp kênh.
  - `evidenceRefs` (`IReadOnlyList<string>`): Danh sách mã ID bản ghi video có trong snapshot làm bằng chứng.
  - `hypothesis` (`string`): Giả thuyết sáng tạo cần kiểm chứng.
  - `tiktokPost` (`TikTokPostDto`): Phương án bài đăng TikTok đi kèm ý tưởng.

### 3.3. Hợp đồng Kịch bản (`GenerateScriptOutputDto`)
- `title` (`string`): Tiêu đề kịch bản.
- `concept` (`string`): Ý tưởng chủ đạo và thông điệp cốt lõi.
- `assumptions` (`IReadOnlyList<string>`): Các giả định về nhân vật, bối cảnh, đối tượng người xem.
- `continuityNotes` (`string?`): Ghi chú tính liền mạch trang phục, đạo cụ, ánh sáng.
- `scenes` (`IReadOnlyList<ScriptSceneDto>`):
  - `sceneKey` (`string`): Khóa định danh cảnh quay (`scene_1`, `scene_2`,...).
  - `order` (`int`): Thứ tự tuần tự của cảnh.
  - `name` (`string`): Tên cảnh ngắn gọn.
  - `description` (`string`): Mô tả chi tiết hình ảnh và hành động diễn ra trong cảnh.
  - `targetDurationSeconds` (`int`): Thời lượng mục tiêu của cảnh.
  - `imagePrompt` (`string`): Prompt tạo ảnh đại diện/tham chiếu bằng tiếng Anh.
- `tiktokPost` (`TikTokPostDto`): Phương án bài đăng TikTok cho toàn bộ video.

### 3.4. Hợp đồng Video Prompts (`GenerateVideoPromptsOutputDto`)
- `scenes` (`IReadOnlyList<VideoPromptSceneDto>`):
  - `sceneKey` (`string`): Khóa cảnh khớp với kịch bản được cung cấp.
  - `promptEn` (`string`): Prompt video tiếng Anh hoàn chỉnh mô tả hành động, góc máy, camera movement, ánh sáng và không khí.
  - `continuityNotes` (`string?`): Ghi chú tính liền mạch chuyển động.
- `tiktokPost` (`TikTokPostDto`): Phương án bài đăng TikTok cập nhật theo prompt video.

### 3.5. Hợp đồng Analytics Snapshot (`AnalyticsContextDto`)
- `capturedAtUtc`, `periodStartUtc`, `periodEndUtc`, `timezone`.
- `isComplete` (`bool`): Đánh dấu snapshot có đầy đủ dữ liệu kỳ hay bị khuyết.
- `metrics` (`IReadOnlyDictionary<string, double?>`): Bắt buộc phân biệt `null` (chưa biết/thiếu dữ liệu) với `0.0` (giá trị thực sự bằng không).
- `videoRecordIds` (`IReadOnlyList<string>`), `selectionManifestJson` (`string?`).

### 3.6. Snapshot Job Bất biến (`ContentJobSnapshotDto`)
Lưu trữ đầy đủ tại thời điểm enqueue trong SQLite:

---

## 4. Danh mục Lệnh Bridge (Allow-list)

Đã bổ sung vào `BridgeContract.cs` và `src/Fraylo.Web/src/contracts/bridge.ts`:

```typescript
  // Content Creation (AI Web — ChatGPT/Gemini Web)
  "content.prepare_context",
  "content.preview_request",
  "content.select_idea",
  "content.apply_script",
  "content.apply_video_prompts",
```

---

## 5. Parser và Xử lý Khả năng Chống chịu (Resilience)

Triển khai tại `src/Fraylo.Application/Content/ContentCandidateParser.cs`:
1. **Trích xuất JSON:** Tự động phát hiện và bóc tách khối mã markdown ````json ... ```` hoặc chuỗi JSON thuần.
2. **Không crash khi lỗi:** Khi JSON lỗi cú pháp, thiếu trường bắt buộc hoặc rỗng, parser trả về `isCompliant = false`, danh sách `Errors` chi tiết và bảo toàn nguyên vẹn `rawText`.

---

## 6. Hướng dẫn AI Tự nhiên (AC1)

Triển khai tại `PromptTemplateService.cs`, `PromptRenderer.cs` và `PromptTemplateEditor.tsx`:
1. **0 biến bắt buộc:** Template tự nhiên hoàn toàn không chứa placeholder `{variable}` được thẩm định hợp lệ 100%, render nguyên vẹn nội dung.
2. **Phân biệt 3 thao tác UI:**
   - **Lưu phiên bản hướng dẫn:** Chỉ lưu phiên bản mới của template vào DB SQLite.
   - **Xem yêu cầu gửi AI (Render xem trước):** Chỉ render xem trước văn bản yêu cầu ghép với dữ liệu; hoàn toàn không gọi AI và không tốn quota.
   - **Gửi AI thật:** Thực hiện từ luồng Tạo nội dung trong Dự án khi người dùng bấm nút hành động.
3. **Tab Prompt video:** Nhãn hiển thị **"Hướng dẫn AI tạo prompt video"** kèm thông điệp rõ ràng: *"AI tự quyết định chi tiết còn thiếu về góc máy, chuyển động camera, ánh sáng và không khí. Bạn không cần điền biến kỹ thuật."*

---

## 7. Ranh giới Ownership & Trạng thái Dependency

1. **Gói đã hoàn tất:**
   - **AC0 (Chốt Contract & DTO/Allow-list):** HOÀN TẤT (100% build PASS, 8/8 smoke checks PASS).
   - **AC1 (Hướng dẫn AI tự nhiên & UI Prompt Video):** HOÀN TẤT (100% DOM tests PASS, frontend build PASS).
2. **Gói bị chặn (BLOCKED):**
   - **AC2 (Từ brief đến prompt — Workflows & UI pipeline):** BỊ CHẶN bởi T16 (4 findings R1–R4 đang mở), T17 (READY), T18 (READY), T19/T20/T22 (TODO).
   - **AC3 (Analytics context — CSV/JSON import):** BỊ CHẶN bởi T19/T25 (P1).
   - **AC4 (Analytics → Ý tưởng):** BỊ CHẶN bởi AC2 và AC3.
   - **AC5 (Tích hợp & Nghiệm thu trên website thật):** BỊ CHẶN cho đến khi các gói trước hoàn thành.
3. **Quy tắc Gate G2:** Không sửa production phụ thuộc của T22/T20 khi Gate G2 chưa đóng (T16 remediation chưa nghiệm thu).
