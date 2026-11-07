# Đăng bài cá nhân Facebook — bàn giao 2026-11-06

> Điều hướng hiện hành 2026-11-07: **Facebook → Hàng đợi / Lịch sử / Nháp** dùng chung Nhóm/Cá nhân; mở nháp về đúng màn soạn, giữ nội dung/ảnh/giờ hẹn. [Spec và evidence thay thế phần điều hướng cũ](FACEBOOK_PUBLISHING_PLAN.md#hàng-đợi-facebook-chung--2026-11-07). Bằng chứng nghiệp vụ bên dưới được giữ theo thời điểm.

Theo yêu cầu người dùng, nối lại `FacebookPoster` đã có vào tab Đăng bài cá nhân; không tạo một adapter thay thế.

- Chọn tài khoản Active/Enabled/Connected; soạn chữ/link, chọn hoặc upload tối đa 10 ảnh, đăng ngay hoặc hẹn giờ, lưu/mở/xóa nháp, hàng đợi/lịch sử/nhân bản và thao tác pause/resume/hủy/đổi giờ theo revision của luồng chung.
- `FacebookCreateBatchRequest.TargetKind` mặc định Group, thêm Profile; Profile chỉ chấp nhận một đích `personal:{accountId}` đúng tài khoản. Target lưu URL profile từ danh tính đã xác minh. Không thêm schema hay reset dữ liệu. Mã đích cá nhân dành riêng cho loại Profile; không cho gửi như nhóm. Group sync không archive target cá nhân.
- Dùng chung scheduler, snapshot ảnh/hash, budget browser, profile gate, pacing 5 phút + 0–30 giây, CAS trước click, recovery và Unknown không tự retry. UI tách bài/nháp/lịch sử theo mã đích; group và personal giữ mounted khi đổi màn.
- Browser ẩn: đối chiếu cookie với account, mở URL profile ID của tài khoản để lấy URL actor, kiểm link Timeline trong composer. Không chấp nhận actor khác. Thông báo thành công phải cụ thể, visible và không tồn tại trước click. Nếu không có bằng chứng kết quả thì Unknown, không tự gửi lại.
- Đối tượng xem giữ theo Facebook, được nhắc ở form và xác nhận; không tự đổi quyền riêng tư. Chưa hiển thị audience đọc trực tiếp từ Facebook trong xác nhận.

## Kiểm tra

- SQLite/application: 179 PASS, gồm replay, chặn đích tài khoản khác/loại đích sai, route tới adapter cá nhân, pacing, Unknown không gửi lại.
- UI/navigation: 10 PASS, gồm giữ form, lưu nháp đúng đích cá nhân, xác nhận chưa tạo tác vụ, giữ mã yêu cầu khi timeout, hẹn giờ và tách lịch sử nhóm.
- Desktop Release: PASS, 0 warning/error, gồm TypeScript/Vite và copy frontend vào wwwroot.
- Browser fixture: 37 PASS, gồm actor đúng/sai và bỏ qua thông báo thành công cũ. Fixture không phải bằng chứng Facebook thật.
- NOT_RUN: đăng cá nhân thật qua UI/scheduler, composer cá nhân VI thật, WebView2/DPI/visual Light/Dark. Adapter có selector nút/media EN/VI nhưng thông báo thành công hiện chỉ có hợp đồng EN; VI thiếu xác nhận sẽ thành Unknown, không tự gửi lại. Page/video chưa hỗ trợ.

Bản dùng thử: `src/Fraylo.Desktop/bin/Release/net10.0-windows/Fraylo.exe`; mở lại sau khi tác vụ đang chạy hoàn tất. Không tự mở app hoặc chạm DB/profile cá nhân trong lượt này. Giữ các thay đổi Analytics/schema v11 sẵn có.
