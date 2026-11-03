# Fraylo — bổ sung plan Cài đặt

>
> **Yêu cầu mới nhất 2026-10-27:** [Luồng tạo video chỉ chạy tự động](AUTOMATIC_VIDEO_WORKFLOW_PLAN.md). Trong phạm vi này, Settings không cung cấp Manual Mode; capability chưa xác minh/không hỗ trợ thì báo automation chưa sẵn sàng, không hướng dẫn nhập kết quả handoff. Quản lý tài khoản và đăng nhập vẫn giữ nguyên.

**Phiên bản:** 1.2 — cập nhật ngày 2026-10-26.  
**Mức ưu tiên:** P0 trong MVP.  
**Trạng thái:** spec hiện hành; thay đổi mới chưa nghiệm thu. [Plan queue/Settings 1.2](QUEUE_EXECUTION_SETTINGS_PLAN.md) chốt luồng thực thi, tab ngang và ownership; [STATUS](../agents/STATUS.md) giữ checkpoint.

Tài liệu này bổ sung `SOURCE_PLAN.md` §1, §3–5, §7–8, §10–12 và Definition of Done theo yêu cầu người dùng. Giữ bản nguồn để đối chiếu. Quy định §2–3 dưới đây thay thế giả định mọi tài liệu/ảnh/video luôn nằm trong một root LocalAppData cố định. Giao diện sáng/tối theo `UI_THEME_PLAN.md`; bố cục và browser ẩn/login theo `UI_BROWSER_PLAN.md`.

## 1. Trang Cài đặt

- Giữ draft/context công việc khi mở Settings. Đổi tab giữ nháp; rời trang khi chưa lưu có lựa chọn ở lại hoặc bỏ thay đổi. Keyboard tabs/focus, scroll và hai theme theo [plan 1.2 §3](QUEUE_EXECUTION_SETTINGS_PLAN.md#3-trang-cài-đặt).
- Roots, provider và budget thuộc cùng một draft; Lưu xác thực rồi commit atomically với expectedRevision. Conflict/lỗi giữ draft và saved state cũ; Hủy về cấu hình đã lưu. Theme áp/lưu ngay độc lập.
- Thiết lập toàn app theo Windows user; settings và danh sách tài khoản thực thi áp dụng toàn app. Thiếu/lỗi đọc có retry, không tự ghi default để dispatch.

## 2. Chọn nơi lưu tài liệu, ảnh và video

Ba trường chọn thư mục độc lập, mỗi trường hiển thị đường dẫn hiện tại và nút **Chọn thư mục…** mở hộp thoại Windows:

| Trường | Nội dung được lưu | Root mặc định |
|---|---|---|
| Tài liệu | Kịch bản, prompt và tài liệu được người dùng lưu/xuất thành file | `%LOCALAPPDATA%/Fraylo/documents` |
| Ảnh AI | Ảnh AI tạo ra và ảnh tham chiếu được nhập vào library | `%LOCALAPPDATA%/Fraylo/images` |
| Video AI | Video AI trả về hoặc được nhập thủ công vào library | `%LOCALAPPDATA%/Fraylo/videos` |

- Có thể chọn các ổ/thư mục khác nhau cho từng loại hoặc dùng cùng một root. App phân tách bên dưới theo `<project-id>/<assets|videos|documents>/...` để tránh trùng tên và lẫn kênh. Tên file an toàn và ID duy nhất ngăn ghi đè ngoài ý muốn.
- Đây là nơi lưu bản file thật. Nội dung editor/template tiếp tục lưu version trong DB; chọn thư mục không tự xuất file ở mọi lần gõ. Audio gốc giữ layout nội bộ của plan nguồn.
- DB, browser profiles, logs và secrets ở `%LOCALAPPDATA%/Fraylo`; trang này không chuyển DB hoặc session browser.
- Host chuẩn hóa đường dẫn và kiểm tra có thể tạo/ghi file; báo rõ đường dẫn sai, thiếu quyền hoặc ổ không sẵn sàng. Thư mục chưa tồn tại được tạo khi lưu hợp lệ; không lưu lựa chọn mới không dùng được.
- Khi ghi file, hết dung lượng/mất ổ/mất quyền phải báo lỗi, giữ thông tin job và kết quả đã nhận để thử lưu lại. Không âm thầm đổi thư mục hoặc gửi lại yêu cầu sinh AI chỉ vì lưu file thất bại.
- Host kiểm tra đường dẫn sau chuẩn hóa, kể cả junction/symlink, vẫn thuộc root đã cấp. UI chọn thư mục qua contract cho phép; không có lệnh đọc/ghi file tùy ý.

## 3. Đổi nơi lưu và giữ dữ liệu cũ

- Đổi thư mục chỉ áp dụng cho asset mới hoặc job mới tạo sau khi Lưu. MVP không tự di chuyển/xóa file đã có; trang hiển thị rõ điều này trước khi lưu.
- Asset lưu `storage_root_id` và `relative_path`; mapping root được lưu bền vững. Khi đổi đường dẫn, tạo tham chiếu root mới, giữ root cũ còn được asset/job tham chiếu; không sửa mapping cũ để trỏ tất cả dữ liệu sang nơi mới. Metadata tài liệu xuất cũng dùng cơ chế này.
- Khi tạo job, chốt root đích theo loại đầu ra. Job queued/running/awaiting_user và retry/resume giữ root đã chốt; đổi cài đặt không chuyển đích của job cũ. File do người dùng chủ động lưu/xuất dùng root hiệu lực khi bắt đầu thao tác.
- Root cũ không truy cập được thì library báo file/ổ không sẵn sàng, cho thử lại sau khi kết nối đúng root; không báo asset bị xóa hoặc tự sinh lại bằng AI.
- Backup gồm tất cả root còn được tham chiếu, kể cả ngoài LocalAppData. Manifest ánh xạ root và relative path; thiếu root phải báo backup chưa đầy đủ. Restore vào root đích được người dùng chọn/xác nhận, kiểm tra đường dẫn và remap tham chiếu; không ghi mù vào đường dẫn tuyệt đối trên máy nguồn.

## 4. Provider và tài khoản trong luồng thực thi độc lập


| Tác vụ | Provider theo cấu hình | Account thực thi |
|---|---|---|
| Kịch bản | Gemini Web hoặc ChatGPT Web | Dispatcher lấy account đúng provider còn capacity khi reserve |
| Ảnh | Gemini Web hoặc ChatGPT Web | Tương tự |
| Prompt video | Gemini Web hoặc ChatGPT Web | Tương tự, độc lập hai lựa chọn trên |
| Video | MuseAI | Account MuseAI hợp lệ còn slot |

Lần đầu provider Chưa chọn; đủ 8 tổ hợp cho ba tác vụ. Lưu/restart giữ lựa chọn; thiếu/sai provider chặn enqueue tác vụ đó; không fallback provider ngầm.

Mọi nguồn đưa job vào một queue SQLite chung; FIFO giữa các job đủ điều kiện, skip job đang chờ mà không chặn phần còn lại. Tài khoản/profile chỉ là tài nguyên executor. Project/scene giữ provenance và địa chỉ trả kết quả, không là khóa chọn account.

Một account một profile/session/identity được xác minh; capacity theo account/task/capability và budget toàn app. Mặc định budget browser 4/job 8; ChatGPT/Gemini maxTabs 3, mỗi task 1, MuseAI 1 job/account khi capability cho phép. Một luồng logic có thể chạy nhiều job theo giới hạn. Binding đã reserve giữ cho retry/reconcile; uncertain/Closing không nhả slot mù.


## 5. Lưu cấu hình và contract

- Dùng `app_settings` cho `storage.documentsRootId`, `storage.imagesRootId`, `storage.videosRootId` và mapping `storage.roots` từ root ID sang đường dẫn do host quản lý. Đường dẫn tuyệt đối chỉ nằm tại mapping; asset/job giữ root ID và relative path, không phụ thuộc root hiện tại.
- T02 chốt schema/DTO đọc, chọn thư mục, xác thực và lưu cấu hình; capability/session status; snapshot job; metadata `document`/`storage_root_id`; cách import dữ liệu cũ dùng root mặc định. Dùng bridge/API đã chọn, có validation và mã lỗi. Persistence/assets owners thống nhất mapping root, metadata và backup trước triển khai.
- Lần đầu thiếu nơi lưu thì dùng root mặc định sau khi kiểm tra; thiếu provider thì Chưa chọn. Provider key sai/bị vô hiệu hóa yêu cầu chọn lại. Lỗi đọc settings đã có phải báo và cho thử lại; không ghi đè bằng mặc định hoặc dispatch job bằng provider/root suy đoán.

## 6. Task và nghiệm thu

- T02 chốt contract §5; T06 dựng entry/trang và nhóm Giao diện; T09 hoàn tất nhóm Lưu trữ và xử lý asset theo §2–3. T15 lưu root/provider snapshot theo contract; T18 hoàn tất nhóm AI & Prompt và dispatch qua registry; T20 tích hợp adapter theo lựa chọn. Coordinator phân file UI/backend cụ thể để các owner không ghi chồng.
- T22 dùng cấu hình trong pipeline; T26 backup/restore nhiều root; T27 kiểm tra Settings tích hợp; T30 kiểm tra các tiêu chí dưới đây. Không đánh dấu nhóm storage/provider hoàn tất chỉ vì trang đã mở được.
- Phần bổ sung browser được theo dõi bằng T02a (review contract/schema) và T13a (profile isolation/migration); không dùng DONE của T13/T14 baseline để đóng phần mới. Settings revision/expectedRevision và fieldErrors giữ invariant v1.1; queue/navigation theo plan 1.2.

1. Bấm Cài đặt mở trang bốn tab ngang, active state/keyboard đúng; đổi tab giữ nháp, quay lại giữ form công việc. Lưu/Hủy/rời trang và theme theo §1; mọi connect/account flow ở tab Tài khoản.
2. Chọn ba thư mục khác nhau, lưu và restart: tài liệu xuất, ảnh và video thực sự ghi đúng root và Project; cũng kiểm tra dùng chung root.
3. Thiếu quyền, đường dẫn thoát root/junction, mất ổ và hết dung lượng cho lỗi hữu ích; cấu hình cũ còn hiệu lực khi lưu Settings lỗi, không false success hoặc gửi lại AI vì lỗi ghi đĩa.
4. Đổi root khi có asset cũ và job queued/running/awaiting_user: dữ liệu cũ mở đúng nơi, job cũ ghi root cũ, job mới dùng root mới; không tự di chuyển/xóa/ghi đè file.
5. Dùng Gemini Web cho ảnh và ChatGPT Web cho prompt, rồi đảo lại; dispatch đúng provider và giữ lựa chọn sau restart. Fake xác định chỉ chứng minh routing; account thật/manual mode ghi evidence riêng theo T19/T20/T30.
6. Thiếu/sai provider, chưa đăng nhập, mất session hoặc không hỗ trợ automation: hướng dẫn/handoff đúng provider, không ngầm chọn dịch vụ khác. Đổi cấu hình không thay snapshot hoặc resend job cũ.
7. Backup/restore nhiều root ngoài LocalAppData và root cũ: manifest đủ, restore đúng đích đã chọn, metadata/library nhất quán; mất root nguồn được báo backup chưa đầy đủ.
9. Nâng cấp từ shared_ai giữ legacy/checkpoint và snapshot; yêu cầu login lại từng provider, không copy cookie, không resend job uncertain. Hai form Settings save cùng revision chỉ một commit; form stale giữ draft và báo conflict.

MVP chỉ hoàn tất khi trang, lưu bền vững, routing provider, lưu file thật và backup/restore đạt các tiêu chí trên. Ghi kiểm tra đã chạy và NOT_RUN; tài liệu này chưa phải bằng chứng triển khai.
