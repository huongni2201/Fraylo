# Kế hoạch tiếp theo — nghiệm thu tích hợp video-first

Ngày 2026-10-27 (UTC+07). Đây là kế hoạch đề xuất, chưa phải kết quả nghiệm thu. Trạng thái và ownership tiếp tục theo [TASKS](../agents/TASKS.md), checkpoint theo [STATUS](../agents/STATUS.md); kiến trúc theo [video-first](VIDEO_FIRST_ARCHITECTURE_PLAN.md).

## Mục tiêu và baseline

Hoàn tất VF0–VF5 theo thứ tự phụ thuộc, chứng minh ứng dụng hoạt động trên Windows thật trước khi kết luận video-first sẵn sàng. Bảo toàn working tree hiện có; không reset, stash hoặc ghi đè thay đổi. Các kết quả dưới đây lấy từ checkpoint, chưa chạy lại trong lượt lập kế hoạch này:

- Schema cuối, backup/reset DB cũ trên fixture, Desktop/frontend build và các smoke workflow, browser, Analytics đã có bằng chứng PASS. DB cũ không version sẽ được backup rồi reset; dữ liệu cũ đã được xác nhận là dữ liệu test.
- TikTok Analytics đã xuất và nhập thật vào DB tạm: Overview 7, Content 0, Viewers 7. Chưa nghiệm thu trong ứng dụng chính.
- VF0, VF2 và VF5 còn IN PROGRESS; VF1/VF3 triển khai cục bộ; VF4 chưa nghiệm thu TikTok thật. Adapter tạo video thật chưa được xác minh; không coi smoke hoặc UI là bằng chứng tạo video thành công.
- Nhiều thay đổi và file mới chưa commit; trạng thái cũ trong tài liệu lịch sử có thể không còn phản ánh kiến trúc hiện hành.

## Thứ tự thực hiện

### 1. Chốt baseline và phụ thuộc (VF0)

**Files:** đọc `docs/agents/TASKS.md`, `docs/agents/STATUS.md`, `docs/plans/VIDEO_FIRST_ARCHITECTURE_PLAN.md` và các file liên quan được inventory; cập nhật hai tài liệu trạng thái nếu có bằng chứng mới.

- [ ] Ghi snapshot `git status`, các file chưa theo dõi, owner/phạm vi hiện hành và xác định app có đang chạy; không thay đổi working tree.
- [ ] Đối chiếu các blocker và kết quả mới nhất trong TASKS/STATUS; không dùng kết quả lịch sử thay cho bằng chứng trên source hiện tại.
- [ ] Chốt phạm vi sửa tuần tự và ghi nhận bất kỳ phụ thuộc chưa hoàn tất trước khi nhận task tiếp.

### 2. Xác minh database và build tích hợp (VF1, VF5)

**Files:** kiểm tra `src/Fraylo.Infrastructure/Persistence/Sql/final-schema.sql`, bootstrapper và `tests/Fraylo.FinalSchema.SmokeChecks/`; sửa file liên quan chỉ khi phát hiện lỗi và theo ownership.

- [ ] Chạy build solution và frontend trên source hiện tại, dùng output riêng nếu app khóa file; ghi lệnh và kết quả.
- [ ] Chạy lại kiểm tra schema/model, DB mới, DB cũ có backup và reset trên fixture riêng; kiểm tra backup có thể mở và dữ liệu fixture được giữ trong bản backup.
- [ ] Chạy storage smoke trong môi trường Windows phù hợp nếu sandbox tiếp tục chặn `File.Move`; ghi rõ blocker nếu chưa chạy được.
- [ ] Kiểm tra thư mục asset/profile và đường dẫn dữ liệu trước khi thử app; không tự xóa dữ liệu hay file ngoài phạm vi fixture.
- [ ] Sửa lỗi thực tế nếu có và chạy lại các kiểm tra liên quan; không khẳng định đã nghiệm thu khi còn lỗi hoặc chưa chạy.

### 3. Nghiệm thu desktop và Analytics (VF2, VF5)

**Files:** `src/Fraylo.Desktop/`, `src/Fraylo.Web/src/`, các smoke test liên quan; tài liệu trạng thái.

- [ ] Khởi động bản build hiện hành trong môi trường kiểm thử sau khi xác định rõ DB sẽ dùng; kiểm tra hành vi backup/reset trước khi dùng DB chính.
- [ ] Kiểm tra Analytics chọn account, session, kéo ba báo cáo và hiển thị/import trên app; đối chiếu null, số 0 và Content chỉ có header.
- [ ] Kiểm tra lỗi session và thông báo nguyên nhân; ghi bằng chứng và che thông tin tài khoản.
- [ ] Kiểm tra WebView2, focus/keyboard, theme và các mức DPI 100/125/150/200%; ghi từng trường hợp PASS/FAIL/NOT_RUN.

### 4. Kiểm tra output và publishing (VF3, VF4)

**Files:** `src/Fraylo.Application/Publishing/`, `src/Fraylo.Browser/Publishing/`, `src/Fraylo.Web/src/features/publishing/`, `tests/Fraylo.Publishing.SmokeChecks/`; tham chiếu [handoff](TIKTOK_PUBLISHING_HANDOFF.md).

- [ ] Kiểm tra một Project chọn đúng một video revision hiện hành; file thiếu hoặc clip trung gian không được báo sẵn sàng đăng.
- [ ] Chạy lại logic và DOM publishing trong môi trường browser hoạt động; kiểm tra account, login, cancel và trạng thái chưa xác nhận.
- [ ] Với tài khoản thử phù hợp, nghiệm thu luồng upload thật chỉ sau thao tác chủ động của người dùng; chỉ ghi thành công khi có xác nhận từ TikTok. Nếu chưa có điều kiện, giữ NOT_RUN.
- [ ] Kiểm tra export và bảo toàn revision khi thử lại hoặc đổi output; ghi giới hạn thực tế.

### 5. Xác minh tạo video và đóng gate

**Files:** `src/Fraylo.Application/Content/`, provider adapters và test liên quan; `docs/agents/TASKS.md`, `docs/agents/STATUS.md`.

- [ ] Đối chiếu capability/provider matrix với hành vi adapter hiện tại; ghi rõ từng năng lực chưa xác minh, không giả dữ liệu hoặc báo Running/Ready sai.
- [ ] Kiểm tra luồng từ brief đến output bằng fixture và, khi có điều kiện, provider thật; kiểm tra lỗi, hủy, retry và recovery theo phạm vi bị ảnh hưởng.
- [ ] Chạy các regression liên quan sau sửa lỗi; ghi cụ thể lệnh, môi trường và kết quả, bao gồm mục chưa chạy.
- [ ] Đối chiếu acceptance VF0–VF5 và gate release; cập nhật TASKS/STATUS, sửa liên kết hoặc đánh dấu tài liệu lịch sử bị thay thế, giữ evidence findings chưa đóng.
- [ ] Chỉ đề xuất công việc sản phẩm tiếp theo sau khi đã phân biệt rõ phần hoàn tất, blocker và giới hạn provider thật.

## Điều kiện hoàn tất

Build và regression liên quan đạt trên source hiện hành; database backup/reset được kiểm chứng trên fixture; các luồng desktop và Analytics có bằng chứng thực tế; output/publishing có trạng thái xác nhận chính xác. Mục thiếu môi trường hoặc tài khoản phải giữ NOT_RUN và không được dùng để tuyên bố gate tổng thể đã đạt.

## Kết quả baseline VF0 — 2026-10-27

- HEAD `3d7c253c0448d260f39c2e72008a5ca468f521ec`; `git status --short` ghi 115 mục sửa, 21 mục xóa và 16 mục chưa theo dõi theo cách Git gộp thư mục. Các mục chưa theo dõi gồm `.tmp/`, plan, Publishing, final schema và smoke projects. Không xóa hoặc hoàn tác bất kỳ mục nào. Không thấy tiến trình `Fraylo.Desktop` tại thời điểm kiểm tra; không suy diễn tình trạng profile hay DB từ kết quả này.
- Publishing dùng contract app-global theo handoff. Handoff chứa các kết luận build lịch sử đã bị checkpoint STATUS/TASKS mới hơn thay thế; live TikTok vẫn NOT_RUN. Không sửa hoặc xóa evidence lịch sử.
- CodeGraph CLI và MCP không trả kết quả trong thời gian kiểm tra; inventory trên là tìm kiếm source có giới hạn, không phải kiểm tra toàn bộ call graph. Không đóng VF0 hoặc khẳng định contract đã hoàn tất.

### Việc tiếp theo sau baseline

- [ ] Kiểm tra build và schema theo bước 2 trên source hiện tại, không chạy ứng dụng với DB chính chỉ để lấy baseline.

## Sửa Project bridge — 2026-10-27



- Build Desktop smoke source hiện hành 0 warning/error; smoke Project và scene.list_by_project với SQLite tạm PASS. Frontend build PASS; TypeScript sau sửa bridge PASS; 11 kiểm tra navigation/library/workflow và 22 kiểm tra queue PASS (có cảnh báo React act). `git diff --check` PASS.

## Chuyển contract Project và Scene — 2026-10-27


## Kiểm tra lại baseline — 2026-10-27

- Build và chạy `Fraylo.FinalSchema.SmokeChecks` từ source hiện hành PASS: SQL/model parity, DB mới, reset DB legacy và backup trên fixture tạm. Không chạy app trên DB chính.
- CodeGraph MCP tiếp tục lỗi `Transport closed`. Chưa nghiệm thu WPF/WebView2, storage smoke và provider/publishing thật trong lượt này.

## Phục hồi Infrastructure smoke — 2026-10-27

- Runner mới dùng DB và thư mục tạm; build `dotnet build tests/Fraylo.Infrastructure.SmokeChecks --no-restore -m:1` PASS 0/0. Chạy DLL ngoài sandbox PASS theme, Project/Scene và storage/settings, bao gồm bảo vệ xóa khi job hoạt động, concurrency, asset dùng chung, file staging, rollback và ràng buộc Job/Scene theo Project. Trong sandbox, File.Move vẫn bị từ chối.
- Không thay đổi mã ứng dụng hoặc DB chính. Runtime desktop, provider và publishing thật vẫn NOT_RUN; VF0/VF5 chưa đóng.

## Kiểm tra tiếp theo, bỏ qua VF0 — 2026-10-28

- Theo yêu cầu người dùng, bỏ qua công việc VF0 và chạy các kiểm tra độc lập; VF0 vẫn IN PROGRESS và không coi các phụ thuộc đã đóng.
- `dotnet build Fraylo.sln --no-restore -m:1`: PASS 0 warning/error. `pnpm build` tại `src/Fraylo.Web`: PASS, cảnh báo bundle lớn hơn 500 kB. Lần chạy FinalSchema đồng thời với solution build thất bại không có chẩn đoán; build riêng và chạy DLL sau đó PASS.
- `dotnet build tests/Fraylo.FinalSchema.SmokeChecks --no-restore -m:1` và chạy DLL: PASS parity SQL/model, bootstrap DB mới, reset legacy và backup trên fixture tạm.
- `dotnet build tests/Fraylo.Publishing.SmokeChecks --no-restore -m:1`: PASS 0/0; chạy DLL ngoài sandbox với Edge và fixture: 20 nhóm PASS. `node --test src/appNavigation.test.ts src/features/publishing/publishingPage.test.ts src/features/projects/videoProductionTab.test.ts src/features/settings/settingsPage.test.ts`: 11/11 PASS, có cảnh báo React `act`.
- Không khởi động desktop: `--test-database-path` và `--test-webview2-profile-dir` cô lập DB/WebView2, nhưng `MainWindow` vẫn tạo `ProfileRegistry` và `StorageRootRegistry` với đường dẫn mặc định trong LocalAppData. Chưa xác minh cô lập toàn bộ tài nguyên để thử an toàn. Desktop navigation/restart, Analytics trong app, DPI, provider thật và TikTok thật NOT_RUN. Không chạm DB chính hoặc gửi video.
- CodeGraph MCP không trả kết quả sau hơn 50 giây; kiểm tra source liên quan bằng `rg` theo fallback. Không sửa mã ứng dụng, không đóng VF0/VF5 hoặc gate tổng thể.
