# Tab Đăng bài Facebook — nhiều bài, lịch riêng, mỗi 5 phút một nhóm

> Điều hướng hiện hành 2026-11-07: **Facebook → Hàng đợi / Lịch sử / Nháp** dùng chung Nhóm/Cá nhân; mở nháp về đúng màn soạn, giữ nội dung/ảnh/giờ hẹn. [Spec và evidence thay thế phần điều hướng cũ](FACEBOOK_PUBLISHING_PLAN.md#hàng-đợi-facebook-chung--2026-11-07). Bằng chứng nghiệp vụ bên dưới được giữ theo thời điểm.

Ngày: 2026-11-06 (UTC+07). Trạng thái: **ĐÃ TRIỂN KHAI CỤC BỘ — còn nghiệm thu trực quan/live**. Owner đề xuất: Codex coordinator, đã thực hiện tuần tự theo yêu cầu triển khai.

## 1. Yêu cầu đã chốt

- Tách chức năng thành tab **Đăng bài** riêng ở sidebar của khu Facebook.
- Tạo và quản lý nhiều bài độc lập: mỗi bài có nội dung, ảnh, tài khoản, danh sách nhóm và giờ bắt đầu riêng.
- Ví dụ: bài A ngày 07/11 đăng 20 nhóm, bài B ngày 08/11 đăng 30 nhóm; không phải chờ bài A hoàn tất mới soạn hoặc lên lịch bài B.
- Mỗi tài khoản gửi tối đa một nhóm tại một thời điểm, cách ít nhất **5 phút giữa hai lần bắt đầu gửi**. Áp dụng xuyên các bài của cùng tài khoản, không phải mỗi bài có một đồng hồ riêng để chạy đồng thời.
- Theo trả lời trực tiếp của người dùng: **bỏ giới hạn 20 lượt gửi/24 giờ**. Không thêm giới hạn ngày thay thế hoặc cấu hình quota vào bản này. Vẫn giữ kiểm tra phiên, quyền tài khoản, khóa profile và xử lý lỗi hiện hành.
- Theo yêu cầu trực tiếp (2026-11-06): khoảng cách giữa hai lượt gửi là **5 phút + 0–30 giây ngẫu nhiên** để không đều tăm tắp. Độ lệch suy ra từ mã lượt vừa gửi (ổn định khi kiểm tra lại/khởi động lại); mọi chỗ ghi "5 phút" dưới đây hiểu là 5 phút tối thiểu, tối đa 5 phút 30 giây. UI dự kiến dùng trung bình 5 phút 15 giây.
- Phạm vi bản này: nhóm Facebook, chữ/link và tối đa 10 ảnh như hiện tại. Chưa mở rộng Page, video, lịch lặp hoặc sửa bài đã đăng trên Facebook.

## 2. Hiện trạng và phần thay thế

Đọc cùng [handoff hiện hành](FACEBOOK_GROUPS_HANDOFF.md), [plan Facebook tổng](FACEBOOK_PUBLISHING_PLAN.md), [quy tắc kỹ thuật](../agents/ENGINEERING_RULES.md), [UI/browser](UI_BROWSER_PLAN.md) và [theme](UI_THEME_PLAN.md).

- `FacebookPage.tsx` hiện phục vụ Soạn bài/Hàng đợi/Lịch sử; `App.tsx` có ba route tương ứng trong khu Facebook. Có nháp lưu SQLite và hẹn giờ, không cần xây lại adapter đăng nhóm.
- `FacebookPostBatch` hiện chỉ có ID, profile, hash yêu cầu và ngày tạo. `FacebookPostTask` là từng lượt gửi vào một nhóm; `FacebookTaskSchedule` giữ giờ hẹn từng task. Thiếu bản ghi bài có tên và cấu hình để quản lý xuyên suốt từ nháp đến hoàn tất.
- `FacebookPublishingService` hiện giữ tạo lô, dispatch, recovery và pacing 5 phút/20 lượt trong 24 giờ. Dùng lại luồng này, bổ sung quản lý bài và sửa chính sách pacing; không tạo scheduler thứ hai.
- Yêu cầu này thay chính sách 20 lượt/24 giờ và cách điều hướng Soạn bài/Hàng đợi/Lịch sử rời nhau trong spec cũ. Giữ bằng chứng kiểm thử/đăng thật cũ trong handoff; không xem bằng chứng đó là nghiệm thu tính năng mới.
- Working tree trước lập plan có thay đổi sẵn ở `design-system/MASTER.md`, `src/Fraylo.Web/index.html`, `package.json`, `pnpm-lock.yaml`, `src/main.tsx`, `src/styles/globals.css`. Giữ nguyên; trước triển khai phải kiểm tra lại ownership/baseline.
- CodeGraph MCP lỗi `Transport closed`, CLI không trả kết quả trong cửa sổ khảo sát; những chi tiết trên đã đối chiếu trực tiếp bằng source/handoff. Không đổi hoặc tạo lại index.

## 3. Giao diện đề xuất

Trong khu Facebook, sidebar có **Đăng bài**. Bên trong gồm **Bài đăng · Nháp · Lịch sử**; quản lý/chọn nhóm nằm trong trình soạn, dùng danh sách đã lưu theo tài khoản. Chỉ nút **Tải lại nhóm từ Facebook** mới gọi Facebook.

Màn Bài đăng có nút **Tạo bài đăng** và danh sách theo bài, không trải 50 nhóm thành 50 dòng ở màn chính:

| Bài | Bắt đầu | Nhóm | Tiến độ | Trạng thái |
|---|---|---:|---|---|
| Bài A | 07/11/2026 08:00 | 20 | 0/20 lượt đã xử lý | Đã hẹn giờ |
| Bài B | 08/11/2026 08:00 | 30 | 0/30 lượt đã xử lý | Đã hẹn giờ |

Các hàng trên là ví dụ trong tài liệu, không phải dữ liệu mẫu trong ứng dụng.

Trình tạo/sửa bài có:

1. Tên bài để dễ quản lý, tài khoản Facebook, nội dung chữ/link và ảnh.
2. Chọn nhóm cụ thể / tất cả / ngẫu nhiên N; tìm kiếm, đếm nhóm, giữ thứ tự đã xác nhận. Ngẫu nhiên được chốt một lần trước xác nhận.
3. Đăng ngay hoặc ngày giờ bắt đầu theo giờ địa phương. Hiển thị **Khoảng cách: 5 phút/nhóm**; bản này dùng cố định 5 phút.
4. Tóm tắt tài khoản, nội dung/ảnh, nhóm theo thứ tự, thời điểm bắt đầu và thời gian dự kiến gửi nhóm cuối. Nêu rõ dự kiến có thể trễ do tác vụ trước/phiên/mạng.
5. **Lưu nháp** hoặc **Xác nhận lên lịch/Đăng ngay**. Chỉ thao tác xác nhận mới tạo lượt gửi; lưu nháp không mở browser.

Chi tiết bài hiển thị trạng thái từng nhóm, giờ gửi thực tế, kết quả, mã lỗi và liên kết bài nếu có. Phân biệt **Đã đăng**, **Chờ duyệt**, **Lỗi**, **Chưa rõ kết quả**, **Đã hủy**; không gộp chờ duyệt vào số đã đăng thành công.

Thao tác theo bài: xem chi tiết, nhân bản thành nháp, đổi giờ khi chưa chạy, tạm dừng/tiếp tục phần chưa gửi, hủy phần chưa gửi. Chỉ sửa nội dung/tài khoản/nhóm khi là nháp; bài đã lên lịch muốn đổi các phần này thì hủy phần chưa gửi và nhân bản để xác nhận lại. Không sửa snapshot của lượt đã tạo.

## 4. Quy tắc lịch và thực thi

- Một bài tương ứng một lô; mỗi nhóm một task. Lưu toàn bộ xuống SQLite trước đánh thức worker; chuyển tab hoặc tạo bài khác không làm mất nội dung/lịch.
- Khi có nhiều bài cùng tài khoản: xếp theo giờ bắt đầu tăng dần, sau đó thời điểm tạo và ID để ổn định. Trong bài dùng thứ tự nhóm đã xác nhận. Bài đến sau chờ bài ưu tiên trước; bài tạm dừng/đang cần người dùng xử lý không chặn toàn bộ bài khác đã sẵn sàng, trừ lỗi phiên/profile ảnh hưởng cả tài khoản.
- Thời điểm được gửi = thời điểm muộn hơn giữa giờ bắt đầu bài và lần bắt đầu gửi gần nhất của tài khoản + 5 phút. Không giữ browser/transaction trong lúc chờ 5 phút. Task chỉ Failed trước checkpoint gửi không tiêu thụ lượt gửi; giữ mốc Submitting/Submitted hiện có làm nguồn pacing, kiểm lại bằng test.
- Bỏ nhánh truy vấn/tính thời điểm chờ do đạt 20 lượt/24 giờ; lượt thứ 21 và 30 vẫn chạy nếu đủ khoảng cách và điều kiện còn lại.
- Không chạy bù dồn: máy bận/sleep rồi hoạt động lại vẫn cách 5 phút theo mốc gửi thực tế; không gửi ngay nhiều nhóm để đuổi kịp các mốc dự kiến.
- Hai tài khoản độc lập có thể chạy theo browser budget hiện hành; không lấy cooldown của tài khoản A áp lên tài khoản B.
- Bài tương lai được phục hồi sau restart, chưa tới giờ thì không kiểm phiên/mở browser. Bài đã quá giờ bắt đầu hoặc bị gián đoạn khi app tắt giữ chính sách hiện hành: **Lỡ lịch/Cần tiếp tục**, không tự đăng muộn; người dùng chọn Tiếp tục, Đổi giờ hoặc Hủy. Phân biệt trễ do xếp hàng trong phiên đang chạy với lỡ lịch do tắt app.
- Không tự retry Failed/Unknown. Sau checkpoint gửi, lỗi không chắc kết quả giữ Unknown. Tạm dừng/hủy chỉ ảnh hưởng lượt chưa gửi; lượt đã Submitting được phép hoàn tất và ghi kết quả, UI phải nói rõ.
- Khi Tiếp tục hoặc Đăng ngay vẫn tuân thủ 5 phút toàn tài khoản. Login vẫn do người dùng chủ động; tự động đăng dùng browser ẩn, không vượt checkpoint/CAPTCHA.
- Khoảng thời gian lý tưởng đến lần bắt đầu gửi nhóm cuối là `(N − 1) × 5 phút`: 20 nhóm = 95 phút, 30 nhóm = 145 phút. Hoàn tất lượt cuối có thể muộn hơn vì thời gian thao tác Facebook.

## 5. Dữ liệu và contract dự kiến

Ưu tiên mở rộng `FacebookPostBatch` thành bản ghi quản lý bài, dùng lại ID lô thay vì tạo thêm một thực thể chiến dịch/lớp queue. Coordinator chốt DTO và schema trước code UI/backend.

- Bài: tên, profile, nội dung/media snapshot, danh sách nhóm có thứ tự, giờ bắt đầu UTC tùy chọn, thời điểm tạo/cập nhật, revision và cờ tạm dừng. Dùng `FacebookDraft` hiện có cho nháp; nhân bản đưa dữ liệu về nháp, xác nhận mới tạo batch mới.
- Lưu ordinal từng task để thứ tự không phụ thuộc các timestamp bằng nhau. Mỗi task phải có BatchId và thuộc đúng tài khoản của batch.
- Tiến độ/trạng thái tổng hợp suy từ task và cờ tạm dừng; không duy trì bộ đếm thành công độc lập dễ lệch. Cần thứ tự ưu tiên hiển thị: cần xử lý/lỡ lịch, tạm dừng, đang đăng, chờ/hẹn giờ, rồi kết quả hoàn tất/hủy/một phần lỗi. Bài đã xử lý hết mà có PendingApproval hiện **Đã gửi hết · Có bài chờ duyệt**.
- Giữ request ID/hash chống tạo trùng khi xác nhận bị timeout. Thao tác sửa lịch/tạm dừng/hủy dùng revision/CAS, chặn dispatch sau khi lệnh đã commit; không thay Submitting.
- Command dự kiến theo nhóm: danh sách/chi tiết bài, tạo bài, đổi giờ, pause/resume, hủy phần chưa gửi. Dùng lại command nháp và nhóm; tên DTO/command cuối cùng chốt sau khảo sát đầy đủ. Trả số liệu tổng hợp/phân trang từ DB, không suy tổng số bài từ history đang giới hạn 200 task.
- Giờ hiển thị theo local, persist UTC. Đổi giờ bài cập nhật giờ đủ điều kiện cho toàn bộ task chưa gửi trong cùng transaction; không để cấu hình batch và `FacebookTaskSchedule` lệch nhau. Chốt một nguồn giờ hẹn cho batch; nếu giữ bảng schedule hiện có thì cập nhật tập trung qua một service.
- Quyết định triển khai thay phương án reset dự kiến: nâng cấp cộng v9 → v10 (`facebook-v10.sql`), giữ dữ liệu. Chỉ thêm tên/pause/revision của batch, thứ tự task và giờ hẹn nháp; backup `pre-facebook-v10` trước nâng cấp v9, transaction/rollback. DB v7/v8 đi qua các bước cũ rồi lên v10, không reset. Test fixture schema v9 được giữ cố định để kiểm nâng cấp, không dựng từ schema mới bằng phép xóa cột.
- Không thêm bản chụp nội dung trùng lên batch: task đã giữ snapshot bất biến; batch là bản ghi quản lý tên/pause/revision. `FacebookTaskSchedule` tiếp tục là nguồn giờ hẹn; thao tác cả bài cập nhật trong cùng transaction. Nháp giữ `DueAtUtc`.
- Danh sách bài suy từ toàn bộ task SQLite, không còn cắt history 200 khiến mất nhóm/kết quả của bài; phân trang 20 bài tại UI. Chưa có phân trang truy vấn DB: cần chuyển sang list summary/detail theo trang nếu lịch sử lớn làm phản hồi chạm giới hạn bridge 4 MB.

## 6. Các lát triển khai, dependency và ownership

Mỗi lát chỉ có một owner. Coordinator giữ contract, persistence, bridge và app shell; triển khai tuần tự. FB2 là nền đã triển khai cục bộ; qualification WebView2/hẹn giờ thật còn mở, phải mang theo vào nghiệm thu. Yêu cầu triển khai được cấp trong chat hiện hành; không đăng Facebook thật hoặc chạm DB cá nhân.

### P1 — Chốt bài/lịch và persistence

Files: `src/Fraylo.Contracts/Facebook/FacebookDtos.cs`; `src/Fraylo.Infrastructure/Persistence/Entities/FacebookPostBatch.cs`, `FacebookPostTask.cs`; các configuration tương ứng, DbContext, SQL/bootstrap liên quan; `tests/Fraylo.FinalSchema.SmokeChecks/` (xác minh tên thực trước giao).

- [x] Chốt DTO, transition, thứ tự nhóm, aggregate status, pause/cancel CAS và nguồn giờ hẹn.
- [x] Triển khai schema/constraints/index tối thiểu theo quyết định reset/nâng cấp rõ ràng.
- [x] Test SQLite thật: liên kết bài/task/profile, ordinal, backup/reset hoặc upgrade/rollback và parity DB mới.

### P2 — Quản lý bài và pacing xuyên bài

Dependency: P1. Files: `src/Fraylo.Application/Facebook/FacebookPublishingService.cs`; `src/Fraylo.Desktop/Bridge/Handlers/FacebookHandlers.cs`; allow-list/composition root nếu cần; `tests/Fraylo.Facebook.Application.SmokeChecks/Program.cs`.

- [x] Bổ sung list/detail theo batch, thao tác lịch/pause/resume/cancel và nhân bản qua nháp.
- [x] Bỏ quota 20/24h, giữ cooldown 5 phút toàn profile, dispatch ổn định và recovery không tự đăng muộn.
- [x] Test fake clock + SQLite: 20/30 nhóm, lượt 21/30 không bị quota chặn; hai bài cùng/trùng giờ; hai profile; pause/cancel tranh chấp checkpoint; timeout/replay; restart/sleep; Unknown không retry; ảnh thiếu/hash đổi và hết phiên.

### P3 — Tab Đăng bài và trình soạn nhiều bài

Dependency: P2. Files: `src/Fraylo.Web/src/App.tsx`, `src/features/facebook/FacebookPage.tsx`, `facebookApi.ts`; component danh sách/chi tiết mới chỉ khi cần; `src/appNavigation.test.ts`, `src/features/facebook/facebookPage.test.ts`, `facebookSchedule.test.ts` (các đường dẫn `src/features` thuộc `src/Fraylo.Web`).

- [x] Gom điều hướng vào Đăng bài; danh sách theo bài, bộ lọc trạng thái và badge theo bài cần xử lý.
- [x] Tạo/sửa nháp độc lập, tên bài, nhóm theo thứ tự, preview lịch và xác nhận.
- [x] Chi tiết tiến độ nhóm; pause/resume/hủy/nhân bản, lỗi có lý do, giữ form khi đổi trang/theme.
- [x] DOM regression: tạo A/B không ghi đè, 20/30 nhóm khác nhau; refresh/reopen; lịch UTC/local; tiến độ Published/PendingApproval/Unknown; route và thao tác bàn phím.

### P4 — Nghiệm thu và đồng bộ tài liệu

Dependency: P1–P3. Files: smoke/UI tests liên quan và các tài liệu dẫn ở §2; coordinator cập nhật `docs/agents/STATUS.md` và `TASKS.md` khi thực sự giao/đóng task.

- [x] Chạy build .NET/TypeScript/Vite và suites Facebook application, browser, FinalSchema; hồi quy TikTok/navigation theo phạm vi ảnh hưởng. Dùng lệnh hiện có của repo, không nâng dependency.
- [ ] Kiểm WebView2 Light/Dark, 1280×800 và hẹp, keyboard/focus, restart/sleep, UI nhận đúng tiến độ; dùng DB/profile test riêng.
- [ ] Chỉ đăng thật khi có yêu cầu cụ thể về tài khoản/nội dung/nhóm; chưa chạy ghi NOT_RUN, không coi fixture là nghiệm thu live.
- [x] Sửa mô tả quota/điều hướng cũ trong plan Facebook và UI/browser, cập nhật handoff giữ evidence hữu ích; rà liên kết, không tạo báo cáo trùng.

## 7. Acceptance criteria

1. Lưu cùng lúc A: 07/11, 20 nhóm và B: 08/11, 30 nhóm; nội dung, ảnh, nhóm, lịch không lẫn nhau, còn sau restart.
2. Màn chính chỉ có hai bài tương ứng; mở từng bài mới xem 20/30 kết quả nhóm.
3. Fake clock chứng minh mốc bắt đầu sớm nhất 08:00…09:35 cho A và 08:00…10:25 cho B khi đủ điều kiện, kể cả sau lượt 20. Trước giờ hẹn không mở browser.
4. Trùng lịch trên cùng tài khoản không gửi song song hoặc cách nhau dưới 5 phút; UI giải thích bài chờ và cập nhật dự kiến.
5. Tạm dừng/hủy/đổi giờ không thay kết quả đã gửi, không vượt CAS, không tự gửi lại khi Unknown; Đăng ngay không bỏ qua cooldown.
6. Không còn text hoặc logic chặn 20 lượt/24 giờ trong luồng hiện hành; evidence lịch sử ghi rõ chính sách ở thời điểm cũ.
7. Lỡ lịch khi app tắt có hành động rõ ràng, không tự đăng muộn; thông báo cần giữ máy/app hoạt động xuất hiện khi lên lịch.
8. Build/test phù hợp đạt; trạng thái chưa kiểm Facebook thật/WebView2/DPI được ghi rõ. Bằng chứng triển khai ở phần dưới.

## 8. Bằng chứng triển khai và giới hạn

- Full solution Release: PASS 0 error, 1 warning CS8604 có sẵn tại AccountConcurrencyAndBrowserLifecycleSmokeTests.cs:943 ở lượt build đầu; lượt build cuối sau formatter PASS 0 warning/0 error (incremental). Đã đóng gói web vào bản Release Fraylo.
- TypeScript/Vite: PASS; UI suite 60/60 PASS, có DOM regression bài 20/30 nhóm, trạng thái tách Published/PendingApproval/Unknown, pause/resume/hủy qua revision, clone và navigation.
- Facebook application: 148 PASS với SQLite thật/browser giả; fake clock kiểm đủ 20/30 lượt, mốc 95/145 phút, không gửi dồn, cooldown xuyên bài, tài khoản độc lập, pause trong Preparing chặn checkpoint, revision cũ bị từ chối, hủy chưa gửi và nháp giữ lịch. Các kiểm recovery/session/hash/Unknown cũ vẫn chạy.
- FinalSchema: PASS schema/model parity, DB mới, legacy reset, nâng cấp v7/v8/v9 giữ dữ liệu, backup, rollback và từ chối future version 11. Schema SQLite sau ALTER có thứ tự cột khác DB mới; test so định nghĩa cột/constraint/index đã chuẩn hóa thứ tự và khoảng trắng, không bỏ kiểm schema.
- Browser fixture Facebook: 34 PASS; hồi quy TikTok Publishing: 32 nhóm PASS. Hai suite chạy thành công ngoài sandbox sau khi Edge bị sandbox đóng ở lần launch đầu. Chỉ dùng fixture/profile tạm, không Facebook/TikTok live.
- NOT_RUN: WebView2 thật, kiểm trực quan Light/Dark/hẹp/DPI, đăng nhóm thật qua scheduler, chạy lịch trên account thật, nâng cấp bản sao DB cá nhân. Thử preview bằng browser tool bị ERR_CONNECTION_TIMED_OUT tới localhost; không coi DOM test là visual QA.
- Giữ mọi sửa đổi có sẵn, gồm file FacebookPage và các thay đổi ở assets/prompts/settings/theme. Không tự commit/push, reset DB hoặc khởi động lại app đang dùng.
