# Facebook — đăng bài vào nhóm

Ngày: 2026-11-06. Phạm vi theo yêu cầu hiện hành: tải nhóm đã tham gia của tài khoản, chọn nhóm cụ thể / ngẫu nhiên N nhóm / tất cả nhóm đã tải, tạo lô đăng chữ + link + tối đa 10 ảnh. Mã đã nối từ React qua bridge đến hàng đợi SQLite và adapter Facebook. Không coi các phần Page, video, hẹn giờ và nháp lưu bền của plan tổng là đã hoàn tất.

## Cách dùng

1. Cài đặt → Tài khoản → Facebook: thêm và đăng nhập, xác minh phiên.
2. Sidebar → Facebook → Nhóm Facebook: chọn tài khoản, **Tải lại nhóm từ Facebook**. Nhóm đã tải được lưu SQLite theo tài khoản, mở lại ứng dụng chỉ đọc danh sách đã lưu. Chỉ nút này mới tải lại từ Facebook. Phân biệt kết quả đã tải đủ và kết quả chưa đầy đủ; nhóm của các tài khoản được giữ riêng.
3. Chọn từng nhóm, **Chọn tất cả**, hoặc nhập số nguyên N và **Chọn ngẫu nhiên**. Chọn tất cả/ngẫu nhiên áp dụng toàn danh sách của tài khoản, kể cả nhóm bị ẩn bởi tìm kiếm. Lựa chọn ngẫu nhiên được chốt trước màn xác nhận, không bốc lại khi gửi.
4. Soạn bài: chữ/link trong nội dung, ảnh Library hoặc tải ảnh. **Xem lại lô đăng** liệt kê đúng tài khoản, nội dung, số ảnh và từng nhóm. Chỉ nút xác nhận mới tạo tác vụ.
5. Hàng đợi/Lịch sử: mỗi nhóm một kết quả. Giữ Fraylo và máy hoạt động để hàng đợi tiếp tục.

## Hành vi đã chốt

- Browser ẩn, không cờ che automation. Danh tính cookie của phiên khớp tài khoản; URL actor trong composer đối chiếu URL /me của cùng phiên. Không đăng ẩn danh, không chuyển sang Page và không dùng composer trang cá nhân cho nhóm.
- Tải nhóm qua trang **All groups you've joined (N)** đã quan sát. Cuộn và gộp URL; chỉ báo đủ khi số nhóm duy nhất bằng N. Kết quả chưa đủ giữ lại các nhóm đã lưu từ lần trước, không archive nhầm nhóm chưa tải tới.
- Mỗi nhóm là một tác vụ, bản chụp tên/URL nhóm, danh tính, chữ và SHA-256 ảnh. Hash tệp được kiểm lại trước mở Facebook và giữ tệp chỉ đọc trong lúc gửi.
- Mã lô ổn định theo bản xác nhận: gửi lại cùng mã và cùng dữ liệu trả lô cũ; dùng lại mã với dữ liệu khác bị từ chối. Chống tạo thêm bài khi bridge timeout.
- Một tài khoản chạy tuần tự: tối thiểu 5 phút giữa hai lần bắt đầu gửi; tối đa 20 lần gửi/24 giờ. Chọn vài chục hoặc tất cả vẫn tạo đủ tác vụ, phần vượt hạn mức tiếp tục chờ. Đây là giới hạn của Fraylo, không phải hạn mức Facebook công bố.
- CAS Preparing → Submitting phải ghi thành công trước click. Hủy trước CAS không click; Submitting không hủy được. Login/hủy login/xóa profile không được phá context đang có thao tác Facebook.
- Chỉ thông báo cụ thể, hiển thị và mới được nhận là Published/PendingApproval; thông báo ẩn, cũ, generic pending và dialog đóng không chứng minh kết quả. Thiếu bằng chứng sau click → Unknown, không tự gửi lại.
- Sau khởi động lại: Queued cũ → NeedsUser để người dùng chọn Tiếp tục; Preparing bị ngắt → Failed; Submitting bị ngắt → Unknown. Không tự đăng muộn hoặc tự retry bài lỗi.
- Nâng cấp DB 7 → 8 cộng bảng, có SQLite backup trước và transaction, giữ dự án/tài khoản/cấu hình. DB mới dùng cùng schema; DB version cao hơn bị từ chối. Đã thử trên bản sao DB cá nhân: 16 bảng cũ giữ nguyên hash dữ liệu, schema=8, DatabaseReset=false. DB gốc không bị sửa.

## Bằng chứng và giới hạn

- Live chỉ đọc bằng đúng profile Fraylo và đường launch headless: **153/153 nhóm**, URL duy nhất 153, IsComplete=true.
- Live dry-run trong một nhóm đã tham gia: đúng actor và nhóm, chữ tiếng Việt/emoji/xuống dòng, 1 ảnh, Post enabled. Không gọi beforeSubmit và không bấm Đăng. Escape đóng composer trực tiếp, khác trang cá nhân hỏi Delete draft.
- Fixture browser kiểm cả luồng /me → nhóm → ảnh/chữ → checkpoint → một lần click, sai actor, ẩn danh, callback từ chối, login không đóng context đang gửi, lọc kết quả cũ/ẩn và dedup URL.
- SQLite smoke kiểm replay, scope tài khoản, bản chụp, CAS/hủy, Unknown không retry, chờ duyệt, phiên, hash ảnh, ngân sách browser, giãn cách/hạn mức, recovery; FinalSchema kiểm backup, parity, rollback giữa chừng và future version.
- Kiểm tích hợp: full solution Release PASS (0 error, 1 warning CS8604 cũ trong smoke test); web 52/52, UI Facebook bổ sung Enabled PASS; browser Facebook 27 checks, service Facebook 36 checks, TikTok 32 nhóm và login/checkpoint 5/5. FinalSchema PASS, diff-check PASS.
- Frontend đã kiểm TypeScript/Vite, regression điều hướng/form/lô, Light/Dark, 1280×800 và 1000×700 bằng dữ liệu fixture. WebView2/DPI 125–150% chưa kiểm.
- **NOT_RUN:** bấm đăng thật vào nhóm, thông báo Published/PendingApproval thật của nhóm và luồng nhóm riêng tư. Regex chờ duyệt là hợp đồng fixture, chưa phải bằng chứng live; không khớp thì Unknown. Cần một nhóm và bài thử do người dùng chỉ định trước khi nghiệm thu live.
- Hiện hỗ trợ DOM Facebook **tiếng Anh** đã quan sát. Nội dung bài bằng tiếng Việt được; website Facebook tiếng Việt/chuyển giao diện chưa được xác minh và sẽ dừng rõ thay vì đoán selector. Chưa hỗ trợ video/Page, hẹn giờ và lưu nháp xuống DB. Các phần đó vẫn thuộc plan tổng.

Nguồn: [plan tổng](FACEBOOK_PUBLISHING_PLAN.md), [bằng chứng giao diện](FACEBOOK_UI_EVIDENCE.md).
