> Cập nhật 2026-10-28: Đã bỏ `USER_BRIEF` khỏi contract tạo prompt. Những hướng dẫn mapping hoặc kiểm thử `USER_BRIEF` bên dưới là lịch sử và không còn áp dụng; nội dung được cung cấp qua template đã render.

# Fraylo — thời lượng clip và phân cảnh trong prompt

Ngày 2026-10-27 (UTC+07). **Spec bàn giao; chưa triển khai, chưa nghiệm thu runtime/provider.**

## 1. Quyết định và phạm vi

Sửa form sản xuất video để người dùng tạo một clip có nội dung trọn ý, với các phân cảnh nằm trong prompt chuyển động. Giữ `Scene` hiện có làm đơn vị một clip đầu ra; một project có thể chứa nhiều clip. Không đổi project thành một video thành phẩm duy nhất.

Tài liệu này thay phần nhập số cảnh/thời lượng và cách lập cảnh trong [workflow từ ảnh](PRODUCT_IMAGE_VIDEO_WORKFLOW_PLAN.md) §3–6,8 khi có khác biệt. Các quy tắc ảnh, audio tùy chọn, queue, ownership, snapshot, revision, history và recovery ở plan đó tiếp tục áp dụng. [Prompt bàn giao](CLIP_DURATION_STORYBOARD_PROMPT.md) là nội dung để người dùng giao cho AI triển khai, không phải lệnh chạy trong phiên viết docs.

**Ngoài phạm vi:** ghép/nối/cắt video, timeline dựng phim, FFmpeg, căn audio xuyên clip, tạo video dài thành phẩm, tự chia yêu cầu 30s thành các clip ngắn. Người dùng có thể tải các clip 10–15s để ghép bằng công cụ khác.

### Thuật ngữ

| Thuật ngữ | Ý nghĩa |
|---|---|
| Clip / Scene | Một đơn vị yêu cầu tạo video, có ảnh đầu vào, prompt, thời lượng và lịch sử output riêng |
| Phân cảnh / shot | Một nhịp hành động/góc quay trong prompt của clip; không phải Scene, asset hoặc job video riêng |
| Số clip | Số output clip người dùng chủ động yêu cầu trong lần chạy; không phải số shot |
| Thời lượng mỗi clip | Thời lượng yêu cầu provider tạo cho từng clip, độc lập với số shot |

## 2. UX đích

Giữ ba chế độ ảnh, brief tự nhiên và audio tùy chọn trên form hiện tại. Thay hai trường dưới cùng:

- **Số clip cần tạo:** số nguyên, mặc định 1 cho cấu hình mới. Giữ giới hạn run hiện hành sau khi coordinator kiểm source; không nới giới hạn trong đợt này. Giá trị đã lưu tiếp tục hiển thị đúng.
- **Thời lượng mỗi clip:** bộ chọn các giá trị được capability provider/model xác nhận, thay input số tự do. Với Muse người dùng báo 10s/15s; đây là đầu mối kiểm chứng, chưa phải capability đã xác nhận. Cấu hình mới ưu tiên 15s khi được hỗ trợ, nếu không chọn 10s khi được hỗ trợ, nếu cả hai không có thì yêu cầu chọn rõ một giá trị hợp lệ.
- Helper: “AI tự chia các phân cảnh trong từng clip theo nội dung. Các clip được xuất riêng.”
- Tóm tắt trước Bắt đầu, ví dụ: “Tạo 1 clip × 15 giây” hoặc “Tạo 3 clip riêng × 10 giây”. Tổng thời lượng nếu hiện phải ghi là tổng các clip, không gọi là độ dài video thành phẩm.

Không thêm lựa chọn video ngắn/video dài, mục tiêu tổng phút, hay chế độ ghép. Người muốn chuẩn bị nguyên liệu video dài có thể yêu cầu nhiều clip và mô tả nội dung nối tiếp trong brief; app vẫn xuất từng clip riêng. Nội dung mỗi clip bám mục đích trong brief, không mặc định tạo nhiều bản sao hoặc nhiều biến thể giống nhau.

Ở màn kết quả/editor thuộc luồng này, dùng nhãn **Clip 1, Clip 2…** cho Scene và **Phân cảnh trong clip** cho shot. Giữ identifier `Scene` trong code/DB; tránh đổi tên toàn bộ module. Màn thêm/sửa scene có lựa chọn thời lượng liên quan phải dùng cùng nguồn capability để không còn đường nhập duration tùy ý rồi bỏ qua kiểm tra.

### Trường hợp 30 giây

Chỉ cho chọn 30s nếu đúng provider/model đang dùng có khả năng tạo trực tiếp một clip 30s qua thao tác adapter đã được xác minh. Nếu chế độ 30s yêu cầu ứng dụng điều phối nhiều lần tạo rồi ghép, nó nằm ngoài phạm vi và không được đưa vào bộ chọn. Nếu website trả một clip 30s qua một thao tác được hỗ trợ, cần ghi rõ evidence/cách hoạt động trước khi bật; không suy từ quảng cáo hoặc tên gói.

Khi chưa xác minh capability: hiện lý do chưa thể chạy và giữ draft; có thể lưu thời lượng người dùng muốn dưới trạng thái chưa hợp lệ. Không hardcode danh sách thành VERIFIED, không tự đổi provider/thời lượng, không báo Đang tạo khi chưa enqueue job bền vững.

## 3. Lập nội dung và prompt

Một lần chạy với N clip tạo đúng N kế hoạch clip trong giới hạn được cho phép. Mỗi kế hoạch gồm mục đích, thời lượng, ảnh tham chiếu và prompt chuyển động. AI quyết định nhịp nội dung bên trong clip; người dùng không phải khai báo số shot.

Ví dụ minh họa cho clip 15s, không dùng làm template cứng cho mọi sản phẩm:

```text
Mục tiêu: thể hiện tổng thể bộ đồ, chất vải và dáng mặc.
Thời lượng clip: 15 giây. Dùng ảnh tham chiếu đã chọn.
0–4s: Toàn thân, người mẫu bước chậm; máy quay di chuyển nhẹ.
4–9s: Cận tay áo để thể hiện chất vải quan sát được từ tham chiếu.
9–15s: Trung cảnh, người mẫu xoay nhẹ và dừng tự nhiên.
Giữ nhất quán nhân vật, trang phục, màu sắc và ánh sáng.
Các mốc thời gian là nhịp nội dung mong muốn.
```

Timestamp là hướng dẫn cho model, không bảo đảm điểm cắt chính xác. Bộ chọn duration/options của provider vẫn quyết định thời lượng yêu cầu thực tế; không chỉ nhúng “15 giây” vào prompt rồi bỏ trường duration của request.

Khả năng đọc prompt chuyển động và khả năng tạo nhiều shot cần được phân biệt. Với model đã xác minh multi-shot, có thể dùng góc quay/điểm chuyển cảnh. Với model chỉ hỗ trợ một shot liên tục, viết các nhịp hành động liên tục trong một góc quay; hiển thị giới hạn. Nếu brief bắt buộc cắt nhiều góc mà capability không đáp ứng, dừng báo không tương thích, không âm thầm chuyển thành nhiều job. Khi capability chưa rõ, không tuyên bố hỗ trợ multi-shot.

Phân cảnh có thể lưu dưới cấu trúc nhẹ trong metadata prompt hiện có nếu cần validation; coordinator chọn cách nhỏ nhất sau khảo sát. Không bắt tạo bảng/entity/editor shot mới. Nếu dùng danh sách mốc thời gian: bắt đầu từ 0, mỗi đoạn có end > start, nối tiếp không hở/chồng, kết thúc đúng duration clip; nội dung không rỗng. Sai schema thì báo lỗi/đi theo retry budget hiện hành, không tự sửa duration người dùng đã chọn.

Một clip có ba shot vẫn chỉ có một nhánh tạo ảnh/prompt/video theo workflow hiện hành, không tạo ba Scene hoặc ba job video do số shot. Job ảnh/prompt cần thiết vẫn theo dependency, không suy “một clip” thành chỉ một job cho toàn pipeline. Chỉ dùng số lượng ảnh/tham chiếu capability hỗ trợ; không mặc định sinh ảnh riêng cho từng shot.

### 3.1. Yêu cầu nội dung và template hiện có

**Bổ sung theo yêu cầu người dùng:** ô “Yêu cầu nội dung / Phong cách chuyển động” phải thực sự tham gia tạo prompt, cùng template người dùng đã lưu. Không coi lưu `goalPrompt` vào cấu hình là bằng chứng nó đã đến request AI. Đây là yêu cầu sửa cần kiểm chứng đầu-cuối lúc triển khai; phiên docs chưa tái hiện runtime hoặc kết luận nguyên nhân bug.

Phân biệt ba nội dung:

| Nội dung | Nguồn và vai trò |
|---|---|
| Brief lần tạo | Ô yêu cầu nội dung, lưu ở `goalPrompt`; yêu cầu cụ thể của project/run, không ghi đè template |
| Prompt chuyển động đầu ra | Kết quả AI viết cho từng clip sau khi nhận template, brief và ảnh; đây là phần gửi Muse để tạo video |

**Đích nhận brief:** backend ánh xạ `goalPrompt` sang `userBrief`/vùng `USER_BRIEF` trong request tạo nội dung. Đây là tên vùng app tự bổ sung theo [quy tắc composition AI web](AI_WEB_CONTENT_CREATION_PROMPTS.md#5-cách-ghép-yêu-cầu-trong-ứng-dụng), không phải biến người dùng phải tự khai báo. Cùng brief phải tới bước lập kế hoạch clip và bước viết prompt chuyển động; mỗi clip nhận thêm mục đích riêng, không thay brief chung bằng tên clip. Bước tạo ảnh nếu có chỉ nhận yêu cầu hình ảnh liên quan và provenance nguồn, không dùng yêu cầu chuyển động làm lệnh tạo ảnh một cách máy móc.

Thứ tự composition cho bước viết prompt chuyển động:

1. Resolve đúng template `VideoPrompt` và version hiệu lực; render các biến được template thực sự khai báo bằng engine hiện có.
2. Thêm nhiệm vụ hiện tại: viết prompt cho các clip với keys app cung cấp, shot nằm trong clip, không phát sinh thêm clip.
3. Thêm dữ liệu có ranh giới rõ: `USER_BRIEF`, yêu cầu riêng của clip nếu có, duration/số clip, ảnh tham chiếu thực gửi, continuity và capability đã kiểm chứng.
4. Thêm `OUTPUT_CONTRACT` do app sở hữu theo operation; luồng sản xuất từ ảnh không phát sinh caption/hashtag hoặc tác vụ Script ngoài phạm vi chỉ vì template mặc định có hướng dẫn đó.

Ví dụ yêu cầu cuối gửi AI viết prompt (minh họa vùng nội dung, không phải literal template seed):

```text
[HƯỚNG DẪN TỪ TEMPLATE VIDEO PROMPT ĐÃ CHỌN]
Bạn là chuyên gia viết prompt chuyển động…

[NHIỆM VỤ]
Viết prompt chuyển động cho clip_1, không tạo thêm clip.

[DỮ LIỆU LẦN TẠO — do app serialize]
USER_BRIEF: "Phong cách thanh lịch, người mẫu bước chậm, nhấn mạnh chất vải."
CLIP_CONTEXT: clip_1, thời lượng 15s, mục đích giới thiệu trang phục.
REFERENCE_IMAGES: ảnh thực sự đính kèm, đúng vai trò.
PROVIDER_CAPABILITIES: khả năng đã xác minh của provider/model đích.

[OUTPUT_CONTRACT]
Schema của operation do app cung cấp.
```

Đây là request gửi AI viết prompt, không gửi nguyên hướng dẫn/template/schema này sang Muse. Muse nhận prompt chuyển động đầu ra đã validate cùng ảnh/duration/options của clip.

### 3.2. Tương thích template và nguồn dữ liệu

- Template chỉ có hướng dẫn tự nhiên, không placeholder: app vẫn thêm brief trong vùng dữ liệu một lần; không bắt người dùng sửa template mới dùng được.
- Template có `{USER_BRIEF}`: engine hiện tại dùng một cặp ngoặc cho placeholder, `{{`/`}}` là escape. Coordinator kiểm schema biến và nối giá trị tự động từ cùng brief hiệu lực. Vùng dữ liệu canonical vẫn chứa brief; preview giải thích placeholder là bản tham chiếu cùng dữ liệu, không phải yêu cầu thứ hai. Không nhân thêm khối brief khi composer được gọi lại. Giá trị brief được thay một lần, không render lại nội dung người dùng như template.
- Template/biến cũ dùng tên khác: đối chiếu contract hiện có và chốt mapping tường minh; chưa có mapping thì báo biến thiếu trước enqueue, không đoán mọi biến `prompt`/`content` là brief. Không áp alias âm thầm toàn bộ engine.
- Render template trước, serialize/append context sau. Dấu ngoặc, JSON, tiếng Việt, xuống dòng trong brief không bị parser coi là placeholder hoặc làm hỏng vùng dữ liệu. Nội dung ảnh/tài liệu đính kèm là tư liệu, không phải chỉ dẫn thay schema/nhiệm vụ app.
- Template quyết định phương pháp/phong cách mặc định; brief quyết định nội dung và phong cách yêu cầu riêng của lần tạo. Giới hạn kỹ thuật, số clip, duration và schema do app kiểm soát. Mâu thuẫn rõ giữa template và brief không thể đáp ứng đồng thời phải báo để người dùng sửa, không im lặng bỏ brief; không hứa tự phát hiện mọi mâu thuẫn ngữ nghĩa.
- Brief trống sau trim: báo thiếu yêu cầu nội dung ngay tại ô nhập và từ backend trước chạy, giữ draft. Áp dụng cho lần tạo tự động mới; project cũ vẫn mở/xem/tải output và dùng prompt chuyển động đã lưu qua luồng thủ công hiện có. Không tự điền brief từ placeholder ví dụ trên UI.

### 3.3. Preview, snapshot và sửa brief

Thêm thao tác phụ **Xem yêu cầu gửi AI** trong chi tiết, không thêm bước duyệt bắt buộc. Hiển thị tên/purpose/version của template hiệu lực, brief đang dùng, dữ liệu clip và toàn bộ prompt đã compose. Helper dưới ô brief: “Yêu cầu này được gửi cùng mẫu Prompt video để AI viết nội dung cho từng clip.” Có đường mở template hiện có và giữ dirty guard khi điều hướng.

Preview và enqueue dùng chung logic composition. Bản preview từ draft cần ghi rõ chưa lưu; lúc Bắt đầu lưu thành công rồi tạo snapshot từ đúng revision, không bỏ qua kết quả save hoặc đọc lại config cũ. Snapshot lưu template/version, brief hiệu lực, context, prompt cuối gửi AI và output contract version. Retry/resume dùng nguyên snapshot; đổi template/brief sau enqueue không làm request cũ thay đổi. Manual copy/export dùng cùng prompt cuối, không bỏ brief.

Sửa brief chung làm stale kế hoạch/prompt/video của các clip phụ thuộc; ảnh chỉ stale nếu yêu cầu hình ảnh thực sự thay đổi theo dependency đã lưu. Không tự chạy lại khi gõ/lưu, không xóa clip/output và không ghi đè prompt chỉnh tay. Yêu cầu riêng của clip được lưu tách với brief chung; cả hai xuất hiện trong context với scope rõ. Output muộn từ brief revision cũ giữ history, không thay bản hiện hành.

## 4. Contract, dữ liệu cũ và invalidation

Ưu tiên giữ field hiện có nếu semantics phù hợp: `targetSceneCount` là số clip và `targetDurationPerSceneSeconds` là thời lượng mỗi clip. Đối chiếu DTO/backend/frontend trước khi chốt; tên này được thấy qua CodeGraph lúc viết plan, không thay thế việc kiểm source hiện hành lúc triển khai.

- Snapshot ghi rõ số clip, duration hiệu lực của từng clip, prompt/template version, provider/model/options, capability evidence/version đang dùng và input ảnh/audio theo contract hiện có. Không sửa snapshot job đã enqueue.
- Backend kiểm duration thuộc capability đúng provider/model trước enqueue, kể cả request bỏ qua UI. Provider thay đổi hoặc capability hết hiệu lực phải kiểm lại; không âm thầm chuẩn hóa 5s thành 10s.
- Cấu hình cũ 3 cảnh × 5s vẫn mở và hiển thị “3 clip × 5 giây — cần chọn lại thời lượng được hỗ trợ” khi provider không hỗ trợ 5s. Giữ scenes, prompt, assets, outputs và history; mở project không tạo job hoặc tự tách/gộp scenes. Nếu provider thật sự hỗ trợ 5s thì tiếp tục xem là hợp lệ.
- Thay duration làm prompt/video của clip phụ thuộc thành cần xem lại, giữ ảnh khi yêu cầu hình ảnh không đổi. Sửa shot trong prompt chỉ ảnh hưởng prompt/video clip tương ứng; không tạo lại clip khác. Đổi số clip không xóa scene/output cũ; run trên project đã có scene giữ quy tắc chọn tập scene của workflow gốc.
- Khi lưu duration chưa hỗ trợ cho draft, trạng thái lưu và trạng thái đủ điều kiện chạy phải tách rõ. Request chạy bị từ chối trước submit, thông báo giữ nguyên input.

## 5. Trình tự triển khai và ranh giới

Đây là delta bổ sung cho PV0/PV1/PV3/PV4/PV5, không tự mở task hoặc đổi DONE lịch sử. Coordinator đối chiếu TASKS, ownership và dependency thực tế trước sửa. Phần UI/validator/template chạy độc lập được làm trước; phần provider/pipeline chỉ tích hợp khi owner và dependencies T19–T22 tương ứng đã sẵn sàng. Không lấy một đợt sửa form làm lý do triển khai toàn bộ adapter còn thiếu.

1. **Baseline và contract:** ghi working-tree baseline, khảo sát source qua CodeGraph, chốt file ownership và capability duration/multi-shot cùng cách giữ dữ liệu cũ. Hoàn tất khi DTO, validation, mapping legacy và tập test đã rõ.
2. **Form và validation:** đổi nhãn, default mới, bộ chọn, helper, tóm tắt; nối dữ liệu capability thật hiện có. Backend kiểm cùng quy tắc trước enqueue. Hoàn tất khi request không hợp lệ không submit và draft cũ được bảo toàn.
3. **Kế hoạch/prompt:** sửa template và validator để tách clip/shot; duration thống nhất với request. Giữ pipeline thật/blocker hiện hành, không thay AI còn thiếu bằng chuỗi minh họa. Hoàn tất khi kiểm N clip không tăng số job video theo số shot và invalidation đúng.
4. **Nghiệm thu:** chạy checks phù hợp, cập nhật spec/links bị thay trong phạm vi này, báo phần triển khai được và phần bị dependency chặn. Provider thật/runtime chưa chạy ghi NOT_RUN.

Điểm khảo sát ban đầu: `src/Fraylo.Web/src/features/projects/VideoProductionTab.tsx`, `ProjectPage.tsx`, `SceneForm.tsx`, `videoWorkflowApi.ts`, `src/Fraylo.Contracts/Content/VideoWorkflowDtos.cs`, `src/Fraylo.Application/Content/ProjectWorkflowService.cs`, bridge handlers và test tương ứng. Đây là đường dẫn gợi ý, không cấp ownership hay xác nhận tất cả phải sửa. Giữ nguyên queue/executor/account lifecycle nếu không có lỗi trực tiếp trong delta này.

## 6. Acceptance và bằng chứng

| ID | Hành vi phải kiểm |
|---|---|
| CD01 | Cấu hình mới mặc định 1 clip; duration theo chính sách §2 và capability thật; cấu hình đã lưu không bị ghi đè |
| CD02 | Form không còn duration số tự do; nhãn số clip/tóm tắt không gây hiểu số shot là số output |
| CD03 | 1 clip 15s có ba nhịp vẫn chỉ lập một Scene/nhánh video; chọn 3 clip cho đúng ba kế hoạch, không nhân theo shot |
| CD04 | UI và backend từ chối duration không được provider/model hỗ trợ; không submit, tự đổi duration/provider hoặc tạo Running giả |
| CD05 | 30s cần ghép bị loại khỏi lựa chọn; không có job/chức năng ghép và không tự chia 30s thành 2 × 15s |
| CD06 | Multi-shot không được xác minh thì không quảng bá đã hỗ trợ; yêu cầu bắt buộc không tương thích có lý do rõ |
| CD07 | Timeline có cấu trúc nếu dùng: phát hiện hở/chồng/vượt duration, schema rỗng; duration prompt và request nhất quán |
| CD08 | Legacy 3 × 5s giữ dữ liệu/history qua mở/lưu/restart; cần sửa duration trước run khi không được hỗ trợ; không gửi AI lúc mở |
| CD09 | Đổi duration/shot chỉ stale đúng nhánh; snapshot job cũ bất biến; expected revision/idempotency vẫn bảo vệ output |
| CD10 | Ba chế độ ảnh, audio tùy chọn, scope, dirty guard, Light/Dark, keyboard và kết quả tải riêng tiếp tục hoạt động |
| CD12 | Template không placeholder vẫn nhận brief; `{USER_BRIEF}` được bind nếu dùng; brief có JSON/ngoặc/xuống dòng/tiếng Việt không bị render lần hai; composer gọi lại không nối lặp context; biến legacy chưa mapping báo lỗi rõ |
| CD13 | Preview/manual export và request enqueue cùng revision có cùng prompt cuối; template/brief sửa sau enqueue không đổi snapshot retry; save thất bại không start từ config cũ; trống brief bị chặn ở UI/backend cho run tự động mới |
| CD14 | Thay brief stale đúng nhánh, giữ ảnh khi chỉ đổi chuyển động, giữ prompt chỉnh tay/history; template tùy chỉnh không bị seed ghi đè; Muse chỉ nhận prompt chuyển động output đã validate, không nhận nguyên request AI writer |

Test logic/DOM bằng fake capability/provider chỉ chứng minh logic ứng dụng. Duration và multi-shot trên Muse thật phải có evidence riêng cho đúng website/model, ghi NOT_VERIFIED/NOT_RUN nếu chưa thử. Bàn giao nêu file đổi, baseline/revision, lệnh và kết quả thực chạy, blocker; không báo pipeline tạo được clip chỉ vì form/template đã sửa.
