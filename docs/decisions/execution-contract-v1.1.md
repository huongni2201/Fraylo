# Fraylo — contract bổ sung browser, pool tài khoản, job và pipeline v1.1




- Ngày: 2026-10-26 (UTC+07). **Baseline thiết kế v1.1**: tài liệu kỹ thuật lịch sử; implementation/gate hiện hành theo [STATUS](../agents/STATUS.md).
- Coordinator review/chốt DTO/schema và migration tại T02a trước code phụ thuộc. [Settings](../plans/SETTINGS_PLAN.md) là spec thao tác, tài liệu này là nguồn schema/lifecycle; source plan cũ giữ để đối chiếu.
- **Delta nội dung ngày 2026-10-26, cần chốt trước triển khai:** [SOURCE §7.4](../plans/SOURCE_PLAN.md#74-tạo-ý-tưởngprompt-kèm-nội-dung-đăng-tiktok--p0) yêu cầu text tạo ý tưởng/kịch bản/prompt kèm `tiktokPost` và lưu bài đăng cấp project. Script/VideoPrompt schemaVersion 1 ở §4 là baseline; coordinator phải version hợp đồng mới, bổ sung snapshot ngữ cảnh, validation, lưu/review/apply/export và compatibility cho job cũ. Yêu cầu này chưa phải wire/schema đã nghiệm thu; không đổi nghĩa payload lịch sử hoặc thêm purpose/provider chỉ để viết caption.

## 1. Pool account và browser profile

| Provider key | Kind | Scope tài khoản/profile | Cấp phát |
|---|---|---|---|
| gemini-web | SharedAI | Global, một hoặc nhiều account | Tương tự ChatGPT, session/profile riêng |


### 1.1. Schema và ownership

- **Quy tắc Giới hạn đồng thời (Cập nhật 1.2):** Mỗi job chạy trên một tab riêng biệt, vì vậy cấu hình tài khoản chỉ cần một giới hạn duy nhất ($N$) lấy từ `maxTabs` (nhãn hiển thị: "Số tác vụ chạy đồng thời"). Loại bỏ ô "Tối đa tác vụ đồng thời (Max Jobs)" khỏi form UI thêm và sửa tài khoản. Backend/Scheduler đồng bộ `max_concurrent_jobs = N` để $N$ giới hạn đồng thời cả số job chạy và số tab automation. Với dữ liệu cũ, giữ nguyên giá trị `max_tabs` làm $N$, không lấy $\min(max_concurrent_jobs, max_tabs)$. Giảm $N$ không hủy job đang chạy; chỉ ngừng nhận thêm đến khi còn capacity.
- **Quy tắc Vòng đời Browser Automation (Cập nhật 1.2):** Không tự khởi chạy browser khi mở app, tải danh sách tài khoản hoặc khi bật nhận việc. Chỉ mở browser automation khi đã reserve được job đủ điều kiện (chạy ẩn). Mỗi job 1 tab riêng; cùng tài khoản dùng chung browser/profile. Khi hết job (idle), đóng browser và giải phóng tài nguyên; không giữ browser chạy nền. Đóng browser không làm mất phiên đăng nhập đã lưu trong `userDataDir`. An toàn khi job mới đến trong lúc browser đang đóng (Closing semaphore lock theo profile). Không đóng browser khi còn job active hoặc uncertain.
- Unique (providerKey, verifiedIdentity) khi identity đã xác minh và Active; nhiều identity null được phép. Một ProviderAccount có đúng một profile và một active owner; không tạo thêm profile cùng account để vượt quota.
- Probe provider xác minh identity, UI chỉ nhập nhãn. Login trùng account đã có trả ACCOUNT_ALREADY_LINKED; profile hiện có probe ra identity khác giữ wrong_account, không đổi identity hay chuyển job sang account mới.

### 1.2. DTO và UI quản lý



| Lệnh | Request | Response / quy tắc |
|---|---|---|
| provider_account.list | {providerKey?} | {accounts:ProviderAccountDto[]} global, gồm state/capacity/revision, không secret/path |
| provider_account.update | {providerAccountId,expectedRevision,enabled?,maxConcurrentJobs?,maxTabs?,perTaskLimits?} | {account}; giảm giới hạn không hủy job đã chạy, chỉ chặn cấp slot mới đến khi dưới cap |
| browser.request_login | {profileId} | DTO baseline; host chọn URL provider đã xác minh, không nhận URL tùy ý từ React |
| browser.check_session | {profileId} | {sessionStatus,identityMatches,profileRevision}; thiếu probe trả Unverified |



### 1.3. Slot, login và đóng/mở browser

- Một job/tab; mỗi profile chỉ một context owner. ChatGPT/Gemini default maxTabs=3, maxConcurrentJobs=3, perTaskLimits Script=1/Image=1/VideoPrompt=1. Có thể điều chỉnh số tab cùng role khi provider/account được xác minh cho phép; không hardcode role chỉ có một page.
- Validate app cap: maxTabs/maxConcurrentJobs 1–10 và maxConcurrentJobs ≤ maxTabs; perTaskLimits mỗi task 0–maxTabs, ít nhất một task dương tương thích provider. Đây là bounds cấu hình MVP để review, không là cam kết quota. maxOpenBrowsers 1–16, maxConcurrentJobs toàn app 1–64; reject sai kiểu/âm/vượt bounds trước save, giữ config cũ.
- MuseAI default maxConcurrentJobs=1, maxTabs=1. Capacity hiệu lực là nhỏ nhất của cấu hình, capability/account quota đã xác minh và global browser/job budget. Giá trị chưa xác minh không bật automation. Nhiều tab/browser không tăng quota dịch vụ.
- Một slot giữ đến khi job hoàn thành nhận/lưu kết quả, cancel được đối soát hoặc checkpoint cho phép release an toàn; không nhả ngay sau click Generate. Job uncertain chiếm/quarantine slot account đó đến khi đối soát, tránh nhận job mới làm mất ngữ cảnh.
- Cho phép người dùng chủ động mở đăng nhập đồng thời cho nhiều profile độc lập khi bấm Đăng nhập (thay thế giới hạn một login toàn app). Không xếp hàng VisibleWaiting toàn app. Quiesce chỉ profile cần login; Gemini không pause ChatGPT, MuseAI A không pause B. Giữ single-instance lock theo từng profile (mỗi userDataDir có 1 owner, không mở trùng khi bấm nhiều lần) và ngân sách tài nguyên maxOpenBrowsers. Đóng login trước verified giữ AwaitingUser/auth_closed. Sau login đóng visible, về Hidden, xác minh đúng identity trước resume.
- Theo yêu cầu user, browser được mở ẩn khi dispatch và đóng khi profile không còn job/tab/checkpoint đang dùng. MuseAI xong một job thì finalize/persist kết quả → đóng context và release owner → hoàn tất release slot → job sau mở lại đúng userDataDir. Chỉ Idle/Stopped khi đã đóng thật; session persisted không đồng nghĩa Connected sau relaunch, phải probe lại.
- ChatGPT có nhiều tab: một tab xong nhả tab cho job sau, chỉ đóng context khi toàn profile idle; không đóng browser để ngắt job khác. close/reopen phối hợp cùng profile lock, không launch context thứ hai trong khoảng đang đóng.
- Close lỗi: giữ profile Error/quarantined, không cấp job mới trên cùng profile; tài khoản khác tiếp tục. Browser có request uncertain không tự đóng theo idle policy cho đến checkpoint/reconcile an toàn; ưu tiên không mất kết quả/không resend.


1. T13a preflight DB/profile fixture riêng, dừng dispatch/writers liên quan, lấy upgrade/profile locks và backup SQLite nhất quán; chưa phụ thuộc T26. Một persistence owner sửa schema/migration, không nâng DB/profile cá nhân trong task thử.
2. shared_ai gộp được ArchivedLegacy; tạo profile ChatGPT/Gemini mới riêng nếu legacy upgrade cần placeholder, identity null, enabled=false cho tới user login/enable. Không copy cookie/session, không đọc profile browser cá nhân. Fresh DB có pool rỗng và CTA thêm account.
4. Trùng identity qua nhiều legacy profile: không tự gộp/copy cookie. Giữ chúng archived/quarantined với evidence; yêu cầu user chọn profile canonical sau khi không còn unresolved job, hoặc đăng nhập lại một profile mới. Chỉ một Active global account cho identity.
5. Job cũ đã có account/profile snapshot giữ nguyên binding/provenance, không load-balance lại. Job cũ trỏ shared_ai chưa submit giữ Queued nhưng blocked profile_migration_required, user hủy/tạo mới; uncertain giữ AwaitingUser/reconcile_required và reference. Không biết side-effect status thì xem uncertain; terminal/history giữ nguyên.
6. Migration journal/version và deterministic mapping idempotent: restart không tạo account/profile trùng, không mất checkpoint, không sửa prompt/root snapshot và không submit. DB migration không giữ transaction trong khi tạo context/network; thư mục host tạo sau DB commit, có thể thử lại.

## 3. Routing và scheduler pool dùng chung

### 3.1. Chốt dữ liệu ở hai thời điểm

- Lần reserve đầu tiên: atomic chọn ProviderAccount global eligible + profile/tab slot, tạo ExecutionBinding bất biến gồm jobId/providerAccountId/profileId/identity/capacityConfigRevision/tabSlotId, lease owner/token và assignment timestamp. Cùng transaction chuyển Queued→Reserved, tăng attempt và khóa slot. Không sửa input snapshot để điền account.
- Retry/resume/reconcile giữ binding đã cấp, đặc biệt sau submit. Account lỗi/disabled/mất phiên không chuyển job đã bind sang account khác; hướng dẫn login/reconcile hoặc hủy/tạo mới nếu an toàn. Job chưa bind vẫn có thể lấy account rảnh khác của cùng provider, không được chuyển Gemini↔ChatGPT ngầm.
- Run giữ workflow/brief snapshot; từng job chốt Settings lúc enqueue bước đó. Một batch enqueue cùng Settings snapshot, không trộn config giữa scene; đổi Settings chỉ tác động job chưa tạo.

### 3.2. Cấp phát và giới hạn

1. Persist Queued trước wake Channel; khi hết slot giữ Queued/waiting_for_capacity, không busy retry và không tăng attempts. Chưa có account pool: Queued/provider_account_required; thiếu setting provider thì chặn enqueue với PROVIDER_NOT_CONFIGURED.
3. Trong cùng write transaction, chọn eligible account cùng provider có slot, ít activeJobs nhất rồi lastAssignedAt/id để phân phối ổn định. Effective capacity và lease/slot uniqueness kiểm lại atomically; hai scheduler không cùng chiếm một slot.
   Profile Stopped sau idle-close vẫn eligible nếu account đã verified/enabled, có phiên lưu và không đang bị đánh dấu login_required/expired; reserve rồi relaunch/probe trước submit. Không bắt user kiểm tra phiên lại cho mỗi job chỉ vì context đã đóng. Profile lần đầu chưa verified hoặc đã biết mất phiên không nhận automation.
4. Chỉ launch browser sau reservation; launch/probe fail trước side effect giải phóng runtime slot theo recovery có fencing, giữ binding cho retry. Full pool không phải lỗi generation. Khi job kết thúc phải finalize/checkpoint, close nếu profile idle, release slot rồi wake scheduler ngay; restart quét DB không lệ thuộc wakeup RAM.
5. Giới hạn browser/job toàn app là setting kỹ thuật có version; maxOpenBrowsers mặc định 4 (tính cả login/closing), maxConcurrentJobs mặc định 8, mỗi account giới hạn như §1.3; thay đổi không kill job đang chạy. Login đã yêu cầu có ưu tiên lấy browser budget khi slot trống; không tự đóng profile bận để mở login.
6. Một request uncertain không làm slot tự hết hạn thành free; lease expiry chuyển sang reconcile/quarantine. Không dùng thêm browser account đó để chạy generation khác rồi làm mất reference.

### 3.3. Ví dụ và nghiệm thu capacity

- 10 VideoRender + 4 MuseAI, mỗi account cap=1 và global budget đủ: bốn job chạy, sáu Queued. Thứ tự hoàn tất tùy thời gian provider; một job xong lưu kết quả và đóng browser account đó, release slot, job kế mở lại cùng profile hoặc account rảnh khác chưa được bind. Không bắt mỗi đợt chờ cả bốn xong.

### 3.4. Capability và Settings


NOT_VERIFIED không bật automation; manualSupported cho AwaitingUser/manual_required; thiếu mode trả PROVIDER_CAPABILITY_UNAVAILABLE. URL login/identity probe/limits chưa xác minh không đoán. Session/account chưa sẵn sàng không làm tự đổi provider hoặc xóa job.

SettingsDto thêm revision, maxOpenBrowsers/maxConcurrentJobs; settings.save expectedRevision và roots/providers/budgets commit cùng transaction. Giới hạn account sửa bằng provider_account.update expectedRevision, chỉ ảnh hưởng cấp phát mới. Theme vẫn áp/lưu riêng ngay. Khi giảm cap/budget dưới số đang chạy, job hiện tại hoàn tất rồi mới cấp tiếp, không kill browser.

## 4. Job scope, snapshot và kết quả text

### 4.1. Schema đích và ownership


Migration backfill project từ scene cũ trước siết constraint; Script cũ có scene được lưu scene cũ trong snapshot provenance rồi chuyển scope project, không xóa scene. JobResult liên kết job không mất khi đổi scope. Dữ liệu không tìm được project/ownership làm migration fail với bản backup còn nguyên, không tự tạo scene/project giả.

Thêm `revision` cho Job, `lease_owner`, `lease_token` tăng mỗi reservation; JobAttempt giữ attempt index/state/timestamps/reason. ExecutionBinding jobId unique tham chiếu ProviderAccount/Profile; ProviderSlot giữ providerAccountId/tabSlotId unique và occupiedByJobId, phase (Reserved/Running/Closing/Uncertain), leaseToken. Reserve Job + Binding + Slot trong cùng write transaction, không chỉ khóa RAM. Mọi heartbeat/progress/result commit compare owner/token/state/expiry; worker cũ không được hoàn tất sau khi mất lease. Mốc 45s lease/15s heartbeat/60s recovery giữ baseline; slot Uncertain không tự free khi lease hết.

### 4.2. Snapshot v1


VideoPrompt mặc định dùng brief/scene/ảnh đã chọn nếu capability hỗ trợ ảnh; nếu capability yêu cầu ảnh thì phải có ảnh. VideoRender có selected image + saved video prompt; scene audio MP3/WAV tùy chọn, snapshot ghi rõ có/không gửi audio. MVP dùng audio ở scene; project audio fallback chưa mở, tránh một job đọc audio khác sau enqueue. Ảnh/prompt có thể do người dùng nhập, không buộc sinh AI trước.

### 4.3. JobResult và ranh giới thành công

JobResult bất biến gồm resultId/jobId/outputIndex/kind/payload/schemaVersion/hash/createdAt; unique `(job_id, output_index)`. kind Script/VideoPrompt lưu JSON/text trong DB; Image/VideoRender giữ assetId đã finalize. Provider trả nhiều ảnh: giữ từng candidate hợp lệ, chỉ thay ảnh được chọn khi user áp dụng. Mỗi job chỉ có một logical result set, finalize lặp trả kết quả cũ.

- Script payload: `{schemaVersion:1, title, scenes:[{key, order, name, description, targetDurationSeconds, imagePrompt}]}`; 1–100 scenes, key unique trong output, order liên tiếp từ 1, name 1–200 ký tự, description/imagePrompt tối đa 20.000 ký tự, duration >0 và ≤600 giây. Đây là giới hạn app MVP, không phải giới hạn website. Từ chối thiếu trường/sai kiểu; không eval text AI.
- VideoPrompt payload: `{schemaVersion:1, text}`; text trim không rỗng, tối đa 20.000 ký tự. Editor lưu `imagePromptText` và `videoPromptText` riêng, revision hiện hành; template purpose Image/VideoPrompt tương ứng, không ghi đè hai mục đích vào một field.
- Text job Succeeded khi validated JobResult + state commit cùng transaction; không đòi Asset/file hay tự tạo scene. Script apply là bước user riêng. Media job Succeeded sau file finalize/hash + Asset + JobResult + state commit; liên kết scene chỉ khi revision còn khớp và workflow cho phép. Stale output giữ kết quả để review, không ghi đè editor.
- Không có transaction atomic giữa filesystem và SQLite; dùng staging/finalize + unique output key + startup reconciliation để khép crash window. Lỗi đĩa giữ result reference, AwaitingUser/STORAGE_ROOT_UNAVAILABLE; thử lưu lại không gọi generate.

## 5. Wire commands và tính lặp an toàn

Envelope/error/origin/scope/cancellation giữ v1.0.4; fields bên dưới là data bên trong envelope. IDs mới là GUID (legacy IDs giữ lịch sử); `clientRequestId` GUID do UI giữ khi retry transport. Cùng key+request payload trả kết quả cũ, cùng key khác payload trả CONCURRENCY_CONFLICT. Tra idempotency trước resolve Settings/input snapshot lần nữa; Settings đổi giữa retry transport không được tạo job hoặc conflict giả. Hash snapshot lưu riêng để kiểm integrity.

| Lệnh | Request | Response/hành vi |
|---|---|---|
| `job.cancel`/`job.reconcile` | Payload baseline thêm expectedRevision | Trả state/revision/cancelRequested/reasonCode; stale request trả conflict, không tạo job khác |


Resume chỉ đưa về Queued khi chưa submit, prerequisites hợp lệ và không yêu cầu user manual khác; profile migration_required không được resume bằng profile mới. Manual import sau cancel giữ output Unlinked và job Canceled; không tự áp vào scene. Import kết quả của request uncertain lưu provenance user-confirmed cùng reference, không coi đó là quyền resend. Chỉ đánh dấu side effect resolved khi có kết quả hoặc bằng chứng provider chưa nhận/từ chối; thiếu bằng chứng giữ unresolved.

### 5.1. Library và chọn input

| Lệnh | Request | Response |
|---|---|---|

Giữ asset.import_audio/get_metadata/open_folder baseline với scope guard. Bỏ chọn chỉ detach input; MVP không có lệnh xóa vật lý hoặc xóa file đang được snapshot tham chiếu. Preview token bind phiên app và scope đã xác minh, backend chặn token đoán/rò sang kênh khác. T09/T12 dùng giới hạn app 50 MiB/ảnh, 200 MiB/audio, 2 GiB/video, TXT/JSON tối đa 2 MiB; capability provider có thể thấp hơn. Đọc theo stream, không đưa media lớn vào bridge; kiểm định dạng thực tế bằng parser/metadata thích hợp, không chỉ đuôi file. Đây là cấu hình giới hạn app ban đầu, chưa là cam kết website nhận được.

## 6. Workflow/run/review

Workflow MVP global, tuyến tính Script → Image → VideoPrompt → VideoRender; audio là input user của scene, không job AI. Chỉnh template/version và requiresReview trong các bước được hỗ trợ; Script và bước áp output stale luôn bắt buộc review. Không thêm step lạ, chu kỳ hay code/binding tùy ý. Provider routing lấy Settings, không lưu provider override trong definition.

| Lệnh | Request | Response |
|---|---|---|
| `workflow.get_active` | `{}` | `{definition:{workflowId, version, steps}, revision}` |
| `workflow.save_version` | `{expectedRevision, steps, clientRequestId}` | Definition version mới bất biến, chưa activate; template scope/capability validation |
| `workflow.activate` | `{workflowId, version, expectedRevision}` | `{workflowId, version, revision}`; rollback cũng dùng activate phiên bản đã có |


Script apply lần đầu chỉ vào project chưa có scene; project đã có scene trả conflict và hướng dẫn tạo project mới cho MVP, không âm thầm xóa/thay scene. Khi input scene thay sau output cũ, run giữ lịch sử, bước sau cần user xác nhận lại; việc chạy lại bước là generation mới với request ID mới do user chủ động. Job đang chạy giữ snapshot; event thành công không tự sinh bước sau khi cần review.

## 7. Errors, dependency và nghiệm thu

Giữ ErrorDto baseline; bổ sung `PROFILE_MIGRATION_REQUIRED`, `ACCOUNT_ALREADY_LINKED`, `PROVIDER_CAPABILITY_UNAVAILABLE`, `INPUT_REQUIRED`, `OUTPUT_VALIDATION_FAILED`, `PROVIDER_NOT_CONFIGURED`. Chờ pool là Queued/waiting_for_capacity hoặc provider_account_required, không error retry/attempt. Reason lưu DB snake_case, error code wire UPPER_SNAKE_CASE; mapping tường minh, không lowercase message.

T02a bàn giao field/constraint/command catalog này với review; mọi command mới chỉ được mở allow-list khi có DTO + handler + client + negative test cùng lần tích hợp. Chưa có handler trả UNKNOWN_COMMAND; UI disable có lý do. Wire version v1.1 được nâng đồng bộ host/client khi tích hợp; không nhận payload v1.1 dưới nhãn 1.0.4. Schema version và snapshotVersion là version riêng; test fixture compatibility trước đổi marker.

T13a triển khai profile/global account/migration trước khi tích hợp scheduler pool T15. T15 baseline đang do Coordinator 2 thực hiện; giữ owner và bàn giao lại brief/version trước phần v1.1. T15 dùng provider resolver fake chỉ trong tests; T18 nối registry thật, T20/T21 nối adapter. T12/T15 cần schema scope/result/scene inputs từ T02a trước code phụ thuộc. Coordinator freeze exact file scope, không ghi chồng source owner đang giữ hoặc tuyên bố worker đã nhận brief nếu chưa dispatch thật.

| Nhóm evidence | Ca bắt buộc |
|---|---|
| Profile | ChatGPT/Gemini/MuseAI global, một account một profile; TikTok riêng kênh; duplicate identity/sai provider bị chặn, không cấm cùng AI account phục vụ nhiều kênh |
| Login | Gemini cần login không dừng ChatGPT; MuseAI A không dừng B; hai login xếp hàng, đóng/sai account không false success |
| Routing | 8 tổ hợp ba AI tasks; thiếu từng setting; đảo Gemini/ChatGPT; job cũ không đổi snapshot |
| Migration | Legacy profile/jobs/checkpoints, crash từng mốc, chạy lại idempotent; không copy session, mất evidence hoặc resend |
| Job/result | Script không scene; media/text boundary; stale token/revision; queued/uncertain/canceled unresolved; manual import hai lần |
| Pipeline | Script apply một lần; batch fail không ghi một phần; input đổi không overwrite; restart/event lặp không enqueue trùng |
| Thật/giả | Fake chỉ chứng minh routing/lifecycle; account thật/Windows riêng mới chứng minh session/website/hidden; chưa chạy ghi NOT_RUN |

URL MuseAI, account tier, selectors, identity proof, upload limits, download/reconcile/hidden support vẫn phải xác minh tại T19; tài liệu không tự gán VERIFIED hoặc bịa endpoint. Không cần biết selector trước khi chốt domain/DTO, nhưng adapter không được triển khai bằng giả định chưa xác minh.
