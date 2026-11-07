# Facebook — đăng bài vào nhóm

> Điều hướng hiện hành 2026-11-07: **Facebook → Hàng đợi / Lịch sử / Nháp** dùng chung Nhóm/Cá nhân; mở nháp về đúng màn soạn, giữ nội dung/ảnh/giờ hẹn. [Spec và evidence thay thế phần điều hướng cũ](FACEBOOK_PUBLISHING_PLAN.md#hàng-đợi-facebook-chung--2026-11-07). Bằng chứng nghiệp vụ bên dưới được giữ theo thời điểm.

**Cập nhật 2026-11-07 — đã tích hợp cục bộ:** [AI viết lại theo lô tối đa 10 phiên bản](20261107-facebook-group-ai-rewrite.md), một bản riêng cho mỗi nhóm, lưu thay nội dung task trước đăng và dùng queue/capacity AI chung. Chọn **AI viết lại bài nhóm Facebook** trong Cài đặt → AI & Prompt trước khi tạo bài nhóm có chữ. 23 nhóm tạo 10/10/3 bản; độ dài85–115%, giữ dữ kiện/link, loại trùng nguyên văn. Chỉ mở đăng khi cả bài đủ nội dung; lỗi không fallback bản gốc, không tự retry. Chi tiết hiển thị text từng nhóm, retry đúng lô; Nhân bản dùng nguồn gốc. Bài chỉ ảnh/cá nhân/bài đã tạo trước tính năng giữ luồng cũ. Schema v13 nâng cấp giữ dữ liệu; capability ChatGPT/Gemini TRIAL, provider live/WebView2/DPI chưa nghiệm thu. Evidence kiểm chứng trong plan FBR; các bằng chứng cũ bên dưới không được dùng để gọi bước AI mới là VERIFIED.

Ngày: 2026-11-06. Phạm vi theo yêu cầu hiện hành: tải nhóm đã tham gia của tài khoản, chọn nhóm cụ thể / ngẫu nhiên N nhóm / tất cả nhóm đã tải, tạo lô đăng chữ + link + tối đa 10 ảnh. Mã đã nối từ React qua bridge đến hàng đợi SQLite và adapter Facebook. Cập nhật 2026-11-06 (schema v9): hẹn giờ, nháp lưu bền và hoàn thiện Hàng đợi/Lịch sử cho luồng nhóm. Không coi các phần Page và video của plan tổng là đã hoàn tất.


## Lựa chọn nội dung hiện hành (schema v14)

Form nhóm và từng bài hẹn giờ cho chọn **Viết lại nội dung / Đăng nguyên bản**, mặc định viết lại. Nguyên bản giữ chữ/ảnh đã nhập và không cần AI; nháp/bản sao giữ lựa chọn. Xem [spec và evidence FBR6](20261107-facebook-group-ai-rewrite.md). Các mốc schema/nghiệm thu bên dưới là lịch sử theo từng lát.

## Cập nhật tab Đăng bài (schema v10)

[Plan và evidence hiện hành](20261106-facebook-posts-tab.md): sidebar Facebook → **Đăng bài**, các mục **Bài đăng/Nháp/Lịch sử**, nút **Tạo bài đăng**. Mỗi bài có tên, lịch, nhóm và tiến độ độc lập; mở Chi tiết để xem từng nhóm. Có Nhân bản, Đổi giờ trước khi chạy, Tạm dừng/Tiếp tục và hủy phần chưa gửi có xác nhận. Chờ duyệt không tính thành đã đăng. Mã lô/revision chống replay và thao tác trên dữ liệu cũ; pause trước checkpoint không bấm Đăng, Submitting vẫn hoàn tất.

Nâng cấp v9 → v10 có backup `pre-facebook-v10` và rollback, giữ dự án/tài khoản/cấu hình/task/nháp; nháp lưu cả giờ hẹn. Bỏ quota 20 lượt/24 giờ; giữ 5 phút toàn profile và các guard phiên/hash/checkpoint. Các kết quả v8/v9 bên dưới là evidence lịch sử, không chứng minh tính năng v10 đã kiểm live. NOT_RUN: đăng thật qua scheduler, WebView2, visual theme/hẹp/DPI và DB cá nhân.

## Cách dùng

1. Cài đặt → Tài khoản → Facebook: thêm và đăng nhập, xác minh phiên.
2. Sidebar → Facebook → Đăng bài → Tạo bài đăng: chọn tài khoản, **Tải lại nhóm từ Facebook**. Nhóm đã tải được lưu SQLite theo tài khoản, mở lại ứng dụng chỉ đọc danh sách đã lưu. Chỉ nút này mới tải lại từ Facebook. Phân biệt kết quả đã tải đủ và kết quả chưa đầy đủ; nhóm của các tài khoản được giữ riêng.
3. Chọn từng nhóm, **Chọn tất cả**, hoặc nhập số nguyên N và **Chọn ngẫu nhiên**. Chọn tất cả/ngẫu nhiên áp dụng toàn danh sách của tài khoản, kể cả nhóm bị ẩn bởi tìm kiếm. Lựa chọn ngẫu nhiên được chốt trước màn xác nhận, không bốc lại khi gửi.
4. Tạo bài đăng: chữ/link trong nội dung, ảnh Library hoặc tải ảnh. **Xem lại lô đăng** liệt kê đúng tài khoản, nội dung, số ảnh và từng nhóm. Chỉ nút xác nhận mới tạo tác vụ.
5. **Thời điểm đăng:** Đăng ngay hoặc Hẹn giờ (giờ máy, tối đa 365 ngày, phải ở tương lai); cả lô dùng một giờ hẹn, giãn cách 5 phút toàn tài khoản vẫn áp dụng sau giờ hẹn, không còn quota ngày.
6. **Nháp:** Lưu nháp/Cập nhật nháp lưu tài khoản, nhóm đã chọn, nội dung và ảnh xuống SQLite (tối đa 50, còn sau khi mở lại app). Mở nháp bỏ chọn nhóm không còn trong danh sách; ảnh/nhóm được kiểm lại khi tạo lô.
7. Đăng bài → Bài đăng/Lịch sử: mỗi nhóm một kết quả. Bài hẹn giờ có **Đăng ngay**, **Đổi giờ**, **Hủy**; **Hủy cả lô** (xác nhận hai bước, chỉ bài chưa gửi). Lịch sử lọc theo trạng thái/tên nhóm, hiện giờ gửi và mã lý do, **Đăng lại** nạp lại nội dung/ảnh/nhóm vào Soạn bài (bài Failed/Unknown/Canceled). Giữ Fraylo và máy hoạt động để hàng đợi tiếp tục.

## Hành vi đã chốt

- Browser ẩn, không cờ che automation. Danh tính cookie của phiên khớp tài khoản; URL actor trong composer đối chiếu URL /me của cùng phiên. Không đăng ẩn danh, không chuyển sang Page và không dùng composer trang cá nhân cho nhóm.
- Tải nhóm qua trang **All groups you've joined (N)** đã quan sát. Cuộn và gộp URL; chỉ báo đủ khi số nhóm duy nhất bằng N. Kết quả chưa đủ giữ lại các nhóm đã lưu từ lần trước, không archive nhầm nhóm chưa tải tới.
- Mỗi nhóm là một tác vụ, bản chụp tên/URL nhóm, danh tính, chữ và SHA-256 ảnh. Hash tệp được kiểm lại trước mở Facebook và giữ tệp chỉ đọc trong lúc gửi.
- Mã lô ổn định theo bản xác nhận: gửi lại cùng mã và cùng dữ liệu trả lô cũ; dùng lại mã với dữ liệu khác bị từ chối. Chống tạo thêm bài khi bridge timeout.
- Một tài khoản chạy tuần tự: tối thiểu 5 phút giữa hai lần bắt đầu gửi xuyên mọi bài; không giới hạn số lượt/24 giờ theo yêu cầu mới. Đây là giãn cách của Fraylo, không phải hạn mức Facebook công bố.
- CAS Preparing → Submitting phải ghi thành công trước click. Hủy trước CAS không click; Submitting không hủy được. Login/hủy login/xóa profile không được phá context đang có thao tác Facebook.
- Chỉ thông báo cụ thể, hiển thị và mới được nhận là Published/PendingApproval; thông báo ẩn, cũ, generic pending và dialog đóng không chứng minh kết quả. Thiếu bằng chứng sau click → Unknown, không tự gửi lại.
- Sau khởi động lại: Queued cũ → NeedsUser để người dùng chọn Tiếp tục; Preparing bị ngắt → Failed; Submitting bị ngắt → Unknown. Không tự đăng muộn hoặc tự retry bài lỗi.
- **Hẹn giờ:** giờ hẹn lưu bảng riêng `FacebookTaskSchedule` (tác vụ vẫn `Queued`; UI suy ra "Đã hẹn giờ" khi có giờ hẹn tương lai). Chưa tới giờ thì dispatch bỏ qua, chưa kiểm phiên, chưa mở browser. Khởi động lại: hẹn giờ còn ở tương lai giữ nguyên; đã quá giờ lúc app tắt → `NeedsUser` mã `schedule_missed` ("Đã lỡ lịch"), **không tự đăng muộn**; người dùng chọn Đăng ngay (Tiếp tục), Đổi giờ hoặc Hủy. Giờ hẹn nằm trong băm lô nên replay khác giờ bị từ chối. Không dùng trạng thái DB mới vì CHECK `ck_facebook_task_state` cần dựng lại bảng.
- **Nháp:** bảng `FacebookDraft` (nhóm/ảnh lưu JSON id, không khóa ngoại để xóa ảnh/nhóm không bị nháp chặn).
- Nâng cấp DB 7 → 8 → 9 chỉ cộng bảng, có SQLite backup trước (`pre-facebook` cho bản 7, `pre-facebook-v9` cho bản 8) và transaction từng bước, giữ dự án/tài khoản/cấu hình/tác vụ. Bản 8 → 9 chạy `facebook-v9.sql`; kiểm: dữ liệu giữ nguyên, schema giống DB mới, backup đúng bản, lỗi giữa chừng rollback, bản 10 bị từ chối. DB mới dùng cùng schema. Lần thử bản 7 → 8 trên bản sao DB cá nhân: 16 bảng cũ giữ nguyên hash dữ liệu, DatabaseReset=false, DB gốc không bị sửa (bước 8 → 9 chưa thử trên bản sao đó).

## Bằng chứng và giới hạn

- Live chỉ đọc bằng đúng profile Fraylo và đường launch headless: **153/153 nhóm**, URL duy nhất 153, IsComplete=true.
- Live dry-run trong một nhóm đã tham gia: đúng actor và nhóm, chữ tiếng Việt/emoji/xuống dòng, 1 ảnh, Post enabled. Không gọi beforeSubmit và không bấm Đăng. Escape đóng composer trực tiếp, khác trang cá nhân hỏi Delete draft.
- Fixture browser kiểm cả luồng /me → nhóm → ảnh/chữ → checkpoint → một lần click, sai actor, ẩn danh, callback từ chối, login không đóng context đang gửi, lọc kết quả cũ/ẩn và dedup URL.
- SQLite smoke kiểm replay, scope tài khoản, bản chụp, CAS/hủy, Unknown không retry, chờ duyệt, phiên, hash ảnh, ngân sách browser, giãn cách/hạn mức, recovery; FinalSchema kiểm backup, parity, rollback giữa chừng và future version.
- Kiểm tích hợp: full solution Release PASS (0 error, 1 warning CS8604 cũ trong smoke test); web 52/52, UI Facebook bổ sung Enabled PASS; browser Facebook 27 checks, service Facebook 36 checks, TikTok 32 nhóm và login/checkpoint 5/5. FinalSchema PASS, diff-check PASS.
- Frontend đã kiểm TypeScript/Vite, regression điều hướng/form/lô, Light/Dark, 1280×800 và 1000×700 bằng dữ liệu fixture. WebView2/DPI 125–150% chưa kiểm.
- **Đăng thật (2026-11-06, giao diện VI, do người dùng chỉ định nội dung và tiêu chí "nhóm có chữ du lịch"):** đăng đúng 1 bài chữ + 6 ảnh webp vào 3 nhóm công khai (REVIEW DU LỊCH NAM NINH - THÁI BÌNH CỔ TRẤN; Du lịch Đông hưng - Nam Ninh Trung Quốc; Tour Trung Quốc - Review Du Lịch Trung Quốc), cách nhau hơn 5 phút, qua `FacebookGroupBrowser` (harness tạm trong `.tmp`, không qua hàng đợi/DB app). Cả 3 hiện ngay trong nhóm ("Vừa xong", "Đã chia sẻ với Nhóm công khai", 6 ảnh); adapter báo Published ở nhóm 2 và 3 (nhóm 1 đăng trước khi có bằng chứng feed, đã xác nhận bằng snapshot). **NOT_RUN:** thông báo chờ duyệt thật (PendingApproval), nhóm riêng tư, nhóm cần admin duyệt; đăng qua hàng đợi trong app thật.
- **Ngôn ngữ giao diện:** tải nhóm và soạn/đăng bài nhóm hỗ trợ **tiếng Anh và tiếng Việt** (tên accessible lấy từ ARIA snapshot live, khớp toàn chuỗi: "Đã tham gia", "Bạn viết gì đi...", "Tạo bài viết", "Ảnh/video", "File phương tiện đính kèm", "Đăng ẩn danh", "Dòng thời gian của …", "Chỉnh sửa quyền riêng tư. Đang chia sẻ với Nhóm công khai.", "Đăng"). Chưa quan sát: "Nhóm riêng tư" (đoán, sai thì chỉ dừng), thông báo chờ duyệt/từ chối bằng tiếng Việt, hộp "xóa nháp" tiếng Việt. Giao diện VI **không có thông báo thành công cụ thể** (chỉ trạng thái "Đang đăng"); bằng chứng Published là đoạn chữ đầu của bài xuất hiện thêm trong nội dung nhóm ngoài hộp soạn bài; không thấy thì Unknown. Tải danh sách VI: live chỉ đọc 1465/1475 (Facebook dừng nạp, báo chưa đủ, không archive). Facebook hiện tối đa 5 ô ảnh, ô cuối phủ "+N" với N = tổng − 4 (đo với 5, 6, 8 ảnh); adapter đếm ảnh theo công thức này nên đăng được 6–10 ảnh. Nội dung bài tiếng Việt đăng được.
- Chưa hỗ trợ video/Page; phần đó vẫn thuộc plan tổng. Hẹn giờ/nháp/hàng đợi mới (schema v9) mới kiểm bằng SQLite thật + UI fixture: FinalSchema (7→9, 8→9, rollback), Facebook application 66 PASS, web 60/60, full solution Release 0 error; **NOT_RUN:** hẹn giờ chạy đúng giờ trên Facebook thật, mở app WebView2, nâng cấp trên bản sao DB cá nhân (app sẽ tự backup và nâng cấp ở lần mở sau), Light/Dark/hẹp của các khối mới.

Nguồn: [plan tổng](FACEBOOK_PUBLISHING_PLAN.md), [bằng chứng giao diện](FACEBOOK_UI_EVIDENCE.md).
