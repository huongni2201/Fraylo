# Nhóm dự án và prompt phân tầng

Cập nhật 2026-10-29. Trạng thái: **ĐÃ TRIỂN KHAI P1 và P2; P3 một phần** — xem mục 9 cho sai khác so với kế hoạch gốc. Liên quan: [VIDEO_FIRST_ARCHITECTURE_PLAN](VIDEO_FIRST_ARCHITECTURE_PLAN.md), [VIDEO_TWO_FLOW_SPLIT](VIDEO_TWO_FLOW_SPLIT.md), [STATUS](../agents/STATUS.md).

## 1. Yêu cầu

1. Người dùng làm nhiều video cùng một ngành hàng (ví dụ quần áo). Mỗi video là một Project nên danh sách dài và phải cấu hình lại từng Project. Cần cách **gom nhóm** và dùng chung cấu hình.
2. Mỗi ngành hàng cần hướng dẫn prompt riêng (chất vải, form dáng, ánh sáng…). Nếu không tuỳ chỉnh thì dùng mặc định chung. Cần cơ chế không làm lệch bản và không làm hỏng khuôn đầu ra.

Ngoài phạm vi: Project chứa nhiều video. [VIDEO_FIRST_ARCHITECTURE_PLAN](VIDEO_FIRST_ARCHITECTURE_PLAN.md) §1–§4 chốt Project là aggregate root của **đúng một video đích**; trạng thái, video hiện hành và publish record đều gắn theo đó. Giữ nguyên. Nhóm là lớp phía trên Project, không thay đổi bất biến này.

"Nhiều video" trong VIDEO_TWO_FLOW_SPLIT (mỗi phân cảnh một video trong một run) là trục khác; hai kế hoạch không xung đột.

## 2. Hiện trạng (đã đối chiếu code)

| Điểm | Vị trí | Thực tế |
|---|---|---|
| Project không có nhóm | [Project.cs](../../src/TiToAI.Infrastructure/Persistence/Entities/Project.cs), bảng `"Project"` trong [final-schema.sql](../../src/TiToAI.Infrastructure/Persistence/Sql/final-schema.sql) dòng 194 | Chỉ có `Id, Name, Brief, Status, CurrentVideoRevisionId, Revision`. |
| Cấu hình workflow lưu JSON | `project.workflow.config.<projectId>` trong AppSettings; [ProjectWorkflowService.Config.cs](../../src/TiToAI.Application/Content/ProjectWorkflowService.Config.cs) | Thêm trường vào `ProjectWorkflowConfigDto` không cần đổi schema SQLite. |
| Prompt mẫu toàn cục | `PromptTemplate` theo `Purpose` (Script/Image/VideoPrompt), một mẫu `IsActive` mỗi purpose | [PromptTemplateService.cs](../../src/TiToAI.Application/Prompts/PromptTemplateService.cs) và [PromptTemplateManager.tsx](../../src/TiToAI.Web/src/features/prompts/PromptTemplateManager.tsx). |
| Prompt cấp Project là bản copy | [VideoProductionTab.tsx](../../src/TiToAI.Web/src/features/projects/VideoProductionTab.tsx) dòng 226–267 | Client render mẫu đang dùng (điền biến) rồi lưu **toàn văn đã render** vào `scriptPromptText`/`videoPromptText`. Mỗi Project giữ một bản copy; mẫu chung sửa sau đó không lan tới Project cũ. |
| Override thay thế hoàn toàn | [VideoWorkflowPromptComposer.cs](../../src/TiToAI.Application/Content/VideoWorkflowPromptComposer.cs) `ResolveRenderedTemplate` dòng 183 | Có text cấp Project (không rỗng) thì bỏ hẳn mẫu chung, rồi chặn nếu còn biến chưa điền. |
| Khuôn đầu ra tách khỏi mẫu | Cùng file, các khối `[OUTPUT_CONTRACT]` | Composer tự nối khuôn JSON sau phần hướng dẫn, nên phần hướng dẫn sửa tự do không làm đổi khuôn đầu ra. Mẫu `ComposeContentAndPromptAiRequestAsync` dùng `config.ScriptPromptText` làm "HƯỚNG DẪN NỘI DUNG". |
| Job đã snapshot prompt | STATUS: "Job snapshot giữ nội dung đã render trước khi chạy" | Đổi prompt không ảnh hưởng job đang chạy hay đã chạy. |

Kết luận: cơ chế override cấp Project đã tồn tại nhưng ở dạng **copy toàn văn**. Plan này không thêm hệ thống prompt thứ ba; nó chuyển override sang dạng kế thừa và thêm tầng Nhóm trên cùng cơ chế đó.

## 3. Thiết kế

### 3.1 Nhóm

Thực thể `ProjectGroup` (tên hiển thị tiếng Việt: **Nhóm**):

| Trường | Ghi chú |
|---|---|
| `Id`, `Name`, `Description` | `Name` duy nhất không phân biệt hoa thường. |
| `DefaultsJson` | Mặc định kế thừa, cùng hình dạng một tập con của `ProjectWorkflowConfigDto` (xem dưới). |
| `Revision`, `CreatedAtUtc`, `UpdatedAtUtc` | Theo mẫu Project (kiểm tra xung đột ghi). |

`Project.GroupId` nullable. Project không có nhóm hoạt động đúng như hiện nay.

Mặc định của nhóm (tất cả tuỳ chọn; thiếu nghĩa là không đặt):
`workflowMode`, `productAssetId`, `modelAssetId`, `readyImageAssetId`, `audioOption`/`audioAssetId`, `targetDurationPerSceneSeconds`, và hai khối hướng dẫn prompt (§3.2).

Quy tắc kế thừa khi **tạo Project mới** trong nhóm: sao chép mặc định nhóm vào cấu hình Project một lần (giá trị thật, không liên kết sống). Với asset thì sao chép tham chiếu, không sao chép file. Sau đó Project độc lập; đổi mặc định nhóm không sửa Project cũ. Chỉ **prompt** là kế thừa sống (§3.2), vì đó là phần cần lan khi cải tiến.

Xóa nhóm: Project trong nhóm chuyển về "không nhóm" (`GroupId = null`), không xóa Project. Xóa Project không ảnh hưởng nhóm.

Nhân bản Project (D3): tạo Project mới cùng nhóm, sao chép cấu hình workflow và hướng dẫn prompt, **không** sao chép scene, job, video, caption.

### 3.2 Prompt phân tầng

Giữ nguyên bốn thứ đã đúng: mẫu toàn cục theo purpose, khuôn `[OUTPUT_CONTRACT]` do composer tự nối, kiểm tra biến/cú pháp bằng `PromptRenderer`, job snapshot.

Thay đổi: override không còn là bản thay thế toàn văn mà là **khối hướng dẫn bổ sung**.

```text
Prompt gửi AI =
  [mẫu toàn cục đang dùng, đã điền biến]          (tầng 1: luôn có)
  [HƯỚNG DẪN BỔ SUNG CỦA NHÓM]                    (tầng 2: tuỳ chọn)
  [HƯỚNG DẪN BỔ SUNG CỦA DỰ ÁN]                   (tầng 3: tuỳ chọn, hiếm)
  [OUTPUT_CONTRACT do app nối]                    (không sửa được)
```

- Hai tầng bổ sung là văn bản thuần, **không chứa biến**. Từ khoá `{...}` trong đó bị từ chối khi lưu (validation tại `SaveWorkflowConfigAsync` và khi lưu nhóm). Nhờ vậy chúng không phá cơ chế biến và không thể gây lỗi "còn biến chưa điền".
- Rỗng nghĩa là kế thừa. Không bao giờ lưu bản copy mẫu chung.
- Tách theo purpose: `scriptGuidance` (dùng cho Script) và `videoGuidance` (dùng cho VideoPrompt). Image dùng lại `scriptGuidance` vì `imagePrompt` được sinh trong bước Script.
- Giới hạn độ dài mỗi khối: 4000 ký tự (hằng số cạnh các giới hạn hiện có; đề xuất, chốt khi làm).
- Phần ghép đặt trong `VideoWorkflowPromptComposer`, ngay trước `[OUTPUT_CONTRACT]`. Chỉ composer biết thứ tự; không rải logic sang nơi khác.
- Snapshot job lưu **prompt đã ghép hoàn chỉnh** (đã đúng như vậy), nên không cần lưu riêng nguồn từng tầng. Nhật ký/UI hiển thị nguồn bằng cách ghi nhãn tầng nào có nội dung.

Nhóm lưu hướng dẫn của mình trong `DefaultsJson`; Project lưu hướng dẫn riêng trong `ProjectWorkflowConfigDto` (trường mới `scriptGuidance`, `videoGuidance`). Hai trường cũ `scriptPromptText`/`videoPromptText` bị **xóa khỏi contract** và khỏi UI (AGENTS.md cho phép xóa mạnh tay contract cũ; dữ liệu cũ không cần giữ).

### 3.3 Hành vi lưu cấu hình ở UI

`saveConfigRequest` (VideoProductionTab dòng 255) hiện gửi văn bản đã render của mẫu. Sau thay đổi nó chỉ gửi `scriptGuidance`/`videoGuidance` của Project (có thể rỗng). Các biến của mẫu toàn cục (`promptValues`) vẫn do UI điền; backend render mẫu lúc ghép prompt, nên `renderPromptTemplateClient` chỉ còn dùng để **xem trước**, không còn là nguồn dữ liệu lưu.

Điều này cần quyết định D1 (nơi giữ giá trị biến), vì hiện giá trị biến không được lưu riêng mà chỉ tồn tại trong văn bản đã render.

## 4. Quyết định mở

| # | Câu hỏi | Đề xuất | Lý do |
|---|---|---|---|
| D1 | Giá trị biến của mẫu (ví dụ thời lượng, tên sản phẩm) lưu ở đâu khi không còn lưu văn bản đã render? | Lưu `promptVariables` (từ điển chuỗi) trong `ProjectWorkflowConfigDto`, backend render lúc ghép prompt. Nhóm có thể đặt giá trị mặc định cho các biến. | Cần để mẫu chung cập nhật là Project cũ hưởng ngay. Khác với hiện trạng nên phải làm cùng đợt. |
| D2 | Giữ tầng Project hay chỉ có Nhóm? | Làm Nhóm trước; tầng Project dùng cùng trường nên gần như miễn phí về backend, UI chỉ cần một ô "Hướng dẫn riêng cho video này". | Giảm số màn hình ở bản đầu mà vẫn đủ cho ca hiếm. |
| D3 | Có làm "Nhân bản dự án" ngay không? | Có, trong đợt 2. | Giải quyết "30 video giống nhau" hiệu quả hơn bất kỳ cấu hình nào khác. |
| D4 | Gán Project hiện có vào nhóm? | Cho phép đổi nhóm qua `project.update`; không đổi cấu hình hiện có. | Dữ liệu có thể reset theo AGENTS.md, nhưng thao tác này đơn giản. |

## 5. Phạm vi thay đổi

Contract và schema (coordinator chốt trước):
- `Contracts/Projects/ProjectDtos.cs`: thêm `groupId`, `groupName` vào `ProjectSummaryDto`/`ProjectDetailDto`; `groupId` vào create/update request. Thêm DTO nhóm và request list/create/update/delete.
- `Contracts/Content/VideoWorkflowDtos.cs`: thêm `scriptGuidance`, `videoGuidance`, `promptVariables`; xóa `scriptPromptText`, `videoPromptText` (cả `ProjectWorkflowConfigDto` và `SaveProjectWorkflowConfigRequest`).
- `final-schema.sql`: bảng `"ProjectGroup"`, cột `"GroupId"` trên `"Project"`, chỉ mục theo `GroupId`. Tăng `CurrentSchemaVersion` lên 4. Theo AGENTS.md, reset schema thay vì viết migration dữ liệu.

Backend:
- Entity `ProjectGroup`, cấu hình EF, `ProjectGroupService` (CRUD, kiểm tra tên trùng, kiểm tra revision).
- `ProjectService`: lọc theo `GroupId`, đổi nhóm, khi tạo Project trong nhóm áp mặc định (§3.1).
- `VideoWorkflowPromptComposer`: ghép 3 tầng, render biến ở backend (D1), bỏ nhánh `ResolveRenderedTemplate(config.…PromptText …)`.
- `ProjectWorkflowService.Config.cs`: lưu/đọc trường mới, validation không chứa biến, đánh dấu `staleTasks` khi `scriptGuidance`/`videoGuidance` đổi (giống cách `ScriptPromptText`/`VideoPromptText` đang được so sánh ở dòng 157–158).
- Bridge: lệnh `projectgroup.list/get/create/update/delete` trong handler mới, đăng ký allow-list như `ProjectHandlers`.

Frontend:
- `ProjectListPage.tsx`: bộ lọc/thẻ nhóm, chọn nhóm khi tạo Project, hiển thị đếm video theo nhóm.
- Trang quản lý Nhóm (tên, mặc định, hướng dẫn script/video) và `projectApi.ts`/`dtos.ts` tương ứng.
- `VideoProductionTab.tsx`: bỏ lưu văn bản đã render; thêm hiển thị nguồn prompt ("Mặc định / Nhóm / Dự án") và nút "Khôi phục mặc định" cho hướng dẫn Project.

## 6. Lộ trình

| Bước | Nội dung | Phụ thuộc |
|---|---|---|
| P1 | Contract + schema nhóm; `ProjectGroupService`; gán `GroupId`; bridge; UI lọc/tạo nhóm. | Coordinator chốt DTO. |
| P2 | Composer ghép 3 tầng; trường `scriptGuidance`/`videoGuidance`/`promptVariables`; bỏ trường cũ; cập nhật `VideoProductionTab`. | P1 (mặc định nhóm chứa hướng dẫn). |
| P3 | Nhân bản Project (D3); áp mặc định nhóm khi tạo mới; nhãn nguồn prompt trên UI. | P2. |

P1 và P2 chạm các file khác nhau (Projects/UI danh sách so với Content/Composer/VideoProductionTab), nhưng P2 cần DTO của P1 nên chạy tuần tự, hoặc song song sau khi DTO chốt.

## 7. Kiểm tra cần có

- Composer: thứ tự ghép 3 tầng; tầng rỗng không sinh nhãn; khối bổ sung chứa `{x}` bị từ chối khi lưu; `[OUTPUT_CONTRACT]` luôn nằm cuối và không đổi theo tầng.
- Mẫu toàn cục đổi → prompt của Project cũ phản ánh mẫu mới (chứng minh hết lệch bản). Job đã chạy giữ nguyên snapshot.
- Nhóm: tên trùng bị từ chối; xóa nhóm đưa Project về không nhóm; tạo Project trong nhóm nhận mặc định; đổi mặc định nhóm không sửa Project cũ.
- Đổi hướng dẫn Project/Nhóm sau khi có kết quả → `staleTasks` đánh dấu đúng (theo hành vi hiện có ở Config.cs dòng 157–158, 253).
- Frontend: lọc theo nhóm; lưu cấu hình không còn gửi văn bản đã render; `tsc`, `vite build` và test hiện có của `VideoProductionTab`.

## 8. Rủi ro

- Đổi từ "lưu văn bản đã render" sang "backend render lúc ghép" (D1) là phần dễ sai nhất: biến thiếu giá trị phải báo lỗi rõ ở UI trước khi chạy, không đợi tới lúc job fail. Giữ kiểm tra `missingPromptVariables` hiện có làm cổng.
- Hướng dẫn nhóm viết dài có thể lấn át mẫu chung và làm AI lệch yêu cầu định dạng. Khuôn đầu ra nằm cuối và do app nối nên ít bị ảnh hưởng, nhưng vẫn cần thử trên provider thật; plan này chưa có bằng chứng chất lượng.
- Schema tăng lên 4 sẽ reset dữ liệu cũ theo cơ chế bootstrap hiện có (backup rồi tạo lại). Người dùng đã chấp nhận ở AGENTS.md; xác nhận lại trước khi chạy lần đầu trên máy có dữ liệu thật.

## 9. Triển khai và sai khác so với kế hoạch

Đã làm (2026-10-29):

- **Dữ liệu:** bảng `ProjectGroup` (tên duy nhất không phân biệt hoa thường), cột `Project.group_id` (xóa nhóm đặt về null qua `ON DELETE SET NULL`). Schema lên `user_version=4` bằng **nâng cấp cộng thêm** từ 3 có sao lưu `pre-upgrade`, không reset. Nâng cấp này thay cho "tăng version + reset" ở §5 vì dữ liệu hiện có không nên mất khi chưa có quyết định của người dùng.
- **Hướng dẫn và biến:** `scriptGuidance`/`videoGuidance` (hướng dẫn riêng dự án) và `promptVariables` thay cho `scriptPromptText`/`videoPromptText`. Backend ghép theo thứ tự mẫu chung (đã điền biến) → hướng dẫn nhóm → hướng dẫn dự án → khuôn `[OUTPUT_CONTRACT]`. Biến lấy theo thứ tự: biến nhóm, biến dự án (ghi đè nhóm), biến hệ thống `duration`/`thoi_luong` (không bị ghi đè). Hướng dẫn có `{...}` bị từ chối khi lưu.
- **Biến không còn trong localStorage:** giá trị biến riêng của dự án lưu trong cấu hình workflow; ô trống nghĩa là dùng giá trị nhóm, và ô hiển thị giá trị nhóm làm gợi ý.
- **Giao diện:** lọc theo nhóm và đếm dự án trong danh sách; "Tạo nhóm" ở danh sách chỉ nhập tên và mô tả. Trong trang nhóm có tab "Cấu hình nhóm" (hướng dẫn prompt, biến mặc định, thông tin nhóm, tệp dùng chung, xóa nhóm) với thanh "Lưu cấu hình"/"Hoàn tác" hiện khi có thay đổi; biến nhập thành từng dòng tên/giá trị. Chọn nhóm khi tạo/sửa dự án; trong VideoProductionTab có nhóm đang áp dụng, nhãn nguồn hướng dẫn ("Mẫu chung + Nhóm + Dự án") và hai ô hướng dẫn riêng.
- **Bridge:** `projectgroup.list`, `projectgroup.save`, `projectgroup.delete` (đã vào allow-list cả hai phía).

Chưa làm và lý do (mục asset đã được làm ở mục 10):

- **Nhân bản dự án (D3):** chưa làm. Sao chép cấu hình workflow cùng tệp riêng của dự án vẫn gặp ràng buộc phạm vi tệp; với tệp dùng chung trong nhóm thì không còn bị chặn, nên có thể làm tiếp khi cần.
- **Mẫu Image:** composer không có bước ghép mẫu Image (ảnh tĩnh sinh từ bước Script), nên không có tầng Image riêng.

Rủi ro cần biết:

- Cấu hình cũ có `scriptPromptText`/`videoPromptText` (bản sao toàn văn của mẫu) bị bỏ qua khi đọc. Dự án nào từng chỉnh tay nội dung mẫu sẽ mất phần chỉnh đó và quay về mẫu chung.
- Hướng dẫn nhóm dài có thể lấn át mẫu chung. Chất lượng chưa được kiểm chứng với provider thật.

Kiểm chứng: `TiToAI.FinalSchema.SmokeChecks` PASS (fresh, legacy reset, nâng cấp v2 và v3 giữ dữ liệu, backup); `TiToAI.Infrastructure.SmokeChecks` PASS; Desktop smoke `project-first` (gồm `ProjectGroupSmokeTests`: CRUD, trùng tên, hướng dẫn/biến không hợp lệ, gán nhóm, ghép prompt nhóm + dự án, biến thiếu, xóa nhóm giữ dự án, JSON từ bridge), `workflow` 16/16, `remediation` 14/14 PASS. Web: `tsc` và `vite build` PASS; `videoProductionTab.test.ts` 8/8 PASS. NOT_RUN: giao diện trong WebView2 và render thật qua provider.

## 10. Tệp dùng chung trong nhóm (2026-10-30)

Người dùng chọn chia sẻ ảnh và âm thanh **trong nhóm** (không toàn app). Schema lên `user_version=5` bằng nâng cấp cộng thêm từ 3 hoặc 4 (một bản sao lưu `pre-upgrade` cho cả chuỗi).

Quy tắc:

- **Phạm vi tệp:** mỗi `Asset` thuộc đúng một trong ba loại: tệp của Project (`ProjectId`), tệp của Nhóm (`GroupId`), hoặc thư viện chung (cả hai rỗng). Tệp của Nhóm dùng được trong mọi Project thuộc Nhóm.
- **Tải lên trong nhóm:** `asset.upload` nhận `groupId` (không đi cùng `projectId`); tệp lưu dưới `groups/{groupId}/…`.
- **Đặt tệp mặc định của nhóm:** bốn ô (ảnh mẫu người, ảnh sản phẩm, ảnh có sẵn, âm thanh) lưu trong `ProjectGroup`. Chỉ nhận tệp đúng loại, và chỉ khi tệp đã thuộc Nhóm hoặc là tệp của một Project trong Nhóm. Khi đó tệp của Project được chuyển thành tệp của Nhóm (`ProjectId` về rỗng, `GroupId` là Nhóm). Tệp thư viện chung và tệp của Project ngoài Nhóm bị từ chối.
- **Kế thừa trực tiếp:** Project để trống một ô thì đọc ra tệp mặc định của Nhóm; đổi mặc định của Nhóm thì Project đang kế thừa đổi theo. Âm thanh của Nhóm chỉ áp dụng khi Project dùng âm thanh tùy chọn.
- **Không sao chép khi lưu:** cấu hình lưu không ghi tệp trùng mặc định của Nhóm (lưu rỗng). Mọi nơi ghi lại cấu hình (lưu, bắt đầu, tạm dừng, tiếp tục, hủy, cập nhật run) đều đi qua `ToStoredConfig`. Các trường tính toán (video cuối, nhóm, ô kế thừa) không được lưu.
- **Đọc hiệu lực:** `GetWorkflowConfigAsync` trả về tệp hiệu lực, cộng `inheritedAssetSlots` để giao diện đánh dấu "Từ nhóm". Quá trình chạy, tạo job và kiểm tra đầu vào dùng tệp hiệu lực.
- **Phạm vi khi xem:** `asset.list` theo Project gồm tệp riêng và tệp của Nhóm chứa Project. Theo Nhóm gồm tệp của Nhóm và tệp của các Project trong Nhóm (để chọn làm mặc định).
- **Xóa nhóm:** các tệp của Nhóm trở thành thư viện chung (không bị xóa khỏi đĩa); các Project chuyển về không nhóm.

Hạn chế đã biết:

- Đổi Nhóm của một Project không tự đánh dấu cảnh đã tạo là cần làm lại; tệp hiệu lực đổi ngay ở lần đọc sau.
- Thư viện chung sau khi xóa nhóm vẫn dùng được ở mọi Project. Nếu muốn giới hạn, cần một quyết định riêng.
- `ENGINEERING_RULES` vẫn ghi "asset dùng chung toàn app"; quy tắc này nay chỉ đúng với thư viện chung, còn tệp của nhóm chỉ dùng trong nhóm.

Kiểm chứng (2026-10-30):

- `TiToAI.FinalSchema.SmokeChecks` PASS: fresh, legacy reset, nâng cấp v2, v3, v4 giữ dữ liệu, backup một lần cho chuỗi.
- `TiToAI.Infrastructure.SmokeChecks` PASS, gồm `T09-9`: tải lên theo nhóm và phạm vi theo Project/Nhóm.
- Desktop smoke: `project-first` PASS (`ProjectGroupSmokeTests` thêm: đặt tệp mặc định và chuyển tệp của Project, từ chối tệp sai loại/ngoài nhóm/thư viện, kế thừa và ghi đè riêng, đổi mặc định theo thời gian thực, âm thanh tùy chọn, dự án ngoài nhóm bị chặn, xóa nhóm bỏ liên kết tệp); `workflow` 16/16; `remediation` 14/14; `t15` 19/19.
- Web: `tsc` và `vite build` PASS; `videoProductionTab.test.ts` 8/8 PASS. `appNavigation.test.ts` và `settingsPage.test.ts` FAIL với cùng lỗi trên HEAD (đã đối chiếu bằng worktree), không liên quan thay đổi này.
- NOT_RUN: giao diện chọn/tải tệp trong WebView2; render thật qua provider.
