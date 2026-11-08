# Đăng bài cá nhân Facebook — bàn giao 2026-11-06

> Điều hướng hiện hành 2026-11-07: **Facebook → Hàng đợi / Lịch sử / Nháp** dùng chung Nhóm/Cá nhân; mở nháp về đúng màn soạn, giữ nội dung/ảnh/giờ hẹn. [Spec và evidence thay thế phần điều hướng cũ](FACEBOOK_PUBLISHING_PLAN.md#hàng-đợi-facebook-chung--2026-11-07). Bằng chứng nghiệp vụ bên dưới được giữ theo thời điểm.

Theo yêu cầu người dùng, nối lại `FacebookPoster` đã có vào tab Đăng bài cá nhân; không tạo một adapter thay thế.

- Chọn tài khoản Active/Enabled/Connected; soạn chữ/link, chọn hoặc upload tối đa 10 ảnh, đăng ngay hoặc hẹn giờ, lưu/mở/xóa nháp, hàng đợi/lịch sử/nhân bản và thao tác pause/resume/hủy/đổi giờ theo revision của luồng chung.
- `FacebookCreateBatchRequest.TargetKind` mặc định Group, thêm Profile; Profile chỉ chấp nhận một đích `personal:{accountId}` đúng tài khoản. Target lưu URL profile từ danh tính đã xác minh. Không thêm schema hay reset dữ liệu. Mã đích cá nhân dành riêng cho loại Profile; không cho gửi như nhóm. Group sync không archive target cá nhân.
- Dùng chung scheduler, snapshot ảnh/hash, budget browser, profile gate, pacing 5 phút + 0–30 giây, CAS trước click, recovery và Unknown không tự retry. UI tách bài/nháp/lịch sử theo mã đích; group và personal giữ mounted khi đổi màn.
- Browser ẩn: đối chiếu cookie với account, mở URL profile ID của tài khoản để lấy URL actor, kiểm link Timeline trong composer. Không chấp nhận actor khác. Thông báo thành công phải cụ thể, visible và không tồn tại trước click. Nếu không có bằng chứng kết quả thì Unknown, không tự gửi lại.
- Đối tượng xem giữ theo Facebook, được nhắc ở form và xác nhận; không tự đổi quyền riêng tư. Chưa hiển thị audience đọc trực tiếp từ Facebook trong xác nhận.

## Preview ảnh — 2026-11-08

Theo ảnh báo lỗi của người dùng, màn cá nhân chỉ render đường dẫn và checkbox; không dựng preview ở cả thư viện và ảnh vừa tải lên. Owner Codex coordinator, tuần tự/tự review; dependency FB2/FB3 và luồng cá nhân đã tích hợp. Phạm vi `FacebookPersonalPage.tsx`, `facebookPersonal.test.ts` và tài liệu bàn giao; giữ thay đổi Analytics/tools hiện có.

- Thư viện hiển thị lưới ảnh thu nhỏ, checkbox và tên tệp; ảnh đã chọn có preview, thứ tự và nút Bỏ ảnh. Bấm ảnh đã chọn để xem lớn; hộp xác nhận cũng hiện ảnh và mở được ảnh lớn.
- Dùng `AssetMediaPreview` và URL media theo Asset ID hiện có. Nháp/bản sao và ảnh tải lên vẫn xem được khi asset không nằm trong trang thư viện đang tải; lỗi đọc ảnh hiện thông báo thay vì để ô trống. Không đổi bridge, schema, dữ liệu hay nghiệp vụ đăng.
- Hai ca kiểm thử chọn thư viện/tải lên FAIL trước sửa do không có phần tử ảnh, sau sửa PASS. Thêm kiểm nháp ngoài thư viện, preview xác nhận và lỗi tệp. Toàn frontend **79/79 PASS**; Desktop Release gồm TypeScript/Vite **0 warning/error PASS**.
- Edge với bridge/media fixture: giải mã ảnh thư viện + ảnh upload ngoài trang hiện tại, Light1280/Dark800/Light600 không tràn ngang; mở ảnh lớn từ xác nhận rồi Escape giữ nguyên xác nhận. Ảnh tại `.tmp/facebook-personal-preview-{light-1280,dark-800,light-600,dialog}.png`; harness `.tmp/facebook-personal-preview-visual.cjs`. Sandbox chặn Edge/named pipe, chạy lại ngoài sandbox với profile tạm PASS.
- NOT_RUN: WebView2 với tệp/DB cá nhân và DPI125–150; đăng Facebook thật. Chưa commit/push/phát hành. Mở lại bản Release sau khi tác vụ đang chạy kết thúc để dùng UI mới.

## Kiểm tra ban đầu (2026-11-06)

- SQLite/application: 179 PASS, gồm replay, chặn đích tài khoản khác/loại đích sai, route tới adapter cá nhân, pacing, Unknown không gửi lại.
- UI/navigation: 10 PASS, gồm giữ form, lưu nháp đúng đích cá nhân, xác nhận chưa tạo tác vụ, giữ mã yêu cầu khi timeout, hẹn giờ và tách lịch sử nhóm.
- Desktop Release: PASS, 0 warning/error, gồm TypeScript/Vite và copy frontend vào wwwroot.
- Browser fixture: 37 PASS, gồm actor đúng/sai và bỏ qua thông báo thành công cũ. Fixture không phải bằng chứng Facebook thật.
- NOT_RUN: đăng cá nhân thật qua UI/scheduler, composer cá nhân VI thật, WebView2/DPI/visual Light/Dark. Adapter có selector nút/media EN/VI nhưng thông báo thành công hiện chỉ có hợp đồng EN; VI thiếu xác nhận sẽ thành Unknown, không tự gửi lại. Page/video chưa hỗ trợ.

Bản dùng thử: `src/Fraylo.Desktop/bin/Release/net10.0-windows/Fraylo.exe`; mở lại sau khi tác vụ đang chạy hoàn tất. Không tự mở app hoặc chạm DB/profile cá nhân trong lượt này. Giữ các thay đổi Analytics/schema v11 sẵn có.
