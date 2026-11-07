# Fraylo — giao diện và browser chạy ẩn


## 1. Điều hướng và màn hình

- **2026-11-07 — AI viết lại bài nhóm:** [luồng mới](20261107-facebook-group-ai-rewrite.md) cho chọn Viết lại nội dung hoặc Đăng nguyên bản; chỉ lựa chọn viết lại mới tự chuẩn bị nội dung sau xác nhận, tối đa10 bản/lượt, headless trên profile ChatGPT/Gemini riêng; dùng queue/slot chung. Hàng đợi Facebook hiển thị tiến độ AI, lỗi và text từng nhóm; chỉ đăng khi mọi lô đã apply. Hẹn giờ không mở Facebook sớm, AI không bỏ trạng thái lỡ lịch. Browser/login/không tự retry giữ quy tắc hiện hành; nghiệm thu provider thật/WebView2 của bước AI mới còn mở.

- **2026-11-07 — sidebar theo nền tảng:** dropdown TikTok/Facebook đặt đầu menu, trên Dự án. TikTok gồm Dự án, Prompt, TikTok Analytics, Đăng video; Facebook gồm các màn đăng bài hiện hành. Tài nguyên dùng chung, Tiến trình/Cài đặt thuộc Hệ thống. Nhớ route mỗi nền tảng, giữ draft và xác nhận rời Cài đặt khi chưa lưu; khởi động theo nền tảng đã lưu. Chỉ đổi phạm vi điều hướng, không đổi scope dữ liệu/backend.


- Project đại diện một video đích: brief và media đầu vào, tiến trình thật, kết quả và lựa chọn đăng. Scene là chi tiết sản xuất; clip trung gian không được trình bày là video cuối.
- Prompt dùng template toàn app với ba tab Script, Image, Video Prompt; mỗi loại lưu nhiều mẫu và chọn một mẫu đang dùng. Giao diện chỉnh tên và nội dung prompt, định nghĩa JSON do backend quản lý theo database. Thư viện asset dùng chung; gỡ liên kết không xóa file được tham chiếu nơi khác.
- Cài đặt có các tab Tài khoản, Lưu trữ, AI & Prompt, Giao diện; quản lý profile và login tại Tài khoản. Analytics chọn tài khoản TikTok trước khi tải hoặc đồng bộ.
- Màn Đăng video yêu cầu người dùng chủ động chọn video, account và xác nhận; chỉ báo Published khi có xác nhận phù hợp. Chi tiết và giới hạn live tại [publishing](TIKTOK_PUBLISHING_HANDOFF.md).
- Giữ form chưa lưu khi đổi trang; không áp response cũ sai Project/account. Hiển thị dữ liệu, tiến trình và lỗi thực tế, không dùng số liệu mẫu làm dữ liệu thật.

- **ReferenceVideo 2026-11-07 — đã triển khai cục bộ:** thay đúng **Chỉ sản phẩm** bằng **Làm theo video mẫu**, giữ **Mẫu + sản phẩm / Ảnh đã hoàn thiện**. Hai input bắt buộc là ảnh mặc sản phẩm và video mẫu kèm audio; nhập qua picker/Library, preview, lưu nháp. Theo lựa chọn mới nhất, MuseAI mô phỏng động tác **gần đúng**, thời lượng có thể khác; AI tự tách/ghép/co giãn âm thanh theo đầu ra. UI giải thích giới hạn và yêu cầu nghe/xem duyệt, không tự thêm nhạc khi mẫu im lặng. Một job tự động, browser ẩn, không thêm FFmpeg/mixer cục bộ. [Plan](20261107-reference-video-workflow.md), [evidence/giới hạn](REFERENCE_VIDEO_EVIDENCE.md), [STATUS](../agents/STATUS.md).

- Facebook có các màn soạn **Đăng bài nhóm / Đăng bài cá nhân** và ba mục **Hàng đợi / Lịch sử / Nháp** chung (Nhóm/Cá nhân), với bộ lọc nơi đăng/tài khoản/trạng thái. Không lặp nút/tab Hàng đợi/Lịch sử/Nháp trong màn soạn nhóm/cá nhân; form chỉ giữ thao tác Lưu/Cập nhật nháp. Nháp mở về đúng màn soạn, giữ ảnh/nội dung/giờ hẹn; sau tạo bài chuyển hàng đợi chung. Xem [spec/evidence mới](FACEBOOK_PUBLISHING_PLAN.md#hàng-đợi-facebook-chung--2026-11-07). Quản lý nhiều bài có lịch/nhóm riêng và tạo sau xác nhận; xem [plan hiện hành](20261106-facebook-posts-tab.md). Chọn/tải nhóm theo tài khoản trong trình tạo bài. Browser chạy ẩn; trạng thái chưa rõ không tự retry. Phạm vi và bằng chứng: [handoff nhóm Facebook](FACEBOOK_GROUPS_HANDOFF.md).
- Sidebar Facebook có thêm **Đăng bài cá nhân** và **Đăng bài Page** (2026-11-06). Đăng bài cá nhân đã nối adapter `FacebookPoster` vào hàng đợi: chữ/link, tối đa 10 ảnh, đăng ngay/hẹn giờ, hàng đợi, lịch sử và nháp dùng chung toàn Facebook. Page vẫn chưa hỗ trợ. Chuyển mục giữ nguyên bản nháp. Bằng chứng và giới hạn tại [handoff cá nhân](FACEBOOK_PERSONAL_HANDOFF.md).
- **Đăng bài Page** có hai tab **Đăng bài / Analytics** (yêu cầu ngày 2026-11-07); không còn mục Facebook Analytics riêng ở sidebar. Tab Analytics cho **Fanpage/Reels** [đã triển khai cục bộ](20261106-facebook-analytics.md): chọn account/Page, cập nhật chủ động bằng browser ẩn qua Business Suite tiếng Việt/Trọn đời (tối đa 50 nội dung), xem SQLite offline và lịch sử từng nội dung. Chỉ tải Analytics khi mở tab lần đầu, giữ account/Page/bộ lọc/dữ liệu khi đổi tab hoặc rời/quay lại màn Page. Giữ null/0, phạm vi và độ đầy đủ; đăng nhập ở Cài đặt → Tài khoản. [Evidence](FACEBOOK_ANALYTICS_EVIDENCE.md): live trên bản sao DB và WebView2 fixture đã kiểm, DPI/toàn luồng app cá nhân còn mở.

**Bổ sung đã triển khai cục bộ (2026-11-07):** [AI phân tích Analytics](20261107-analytics-ai-analysis.md) tự gửi số liệu đã lưu sau sync/import hợp lệ, riêng từng TikTok account/Fanpage. Báo cáo nằm ngay trong màn Analytics tương ứng; số liệu và trạng thái AI độc lập, có căn cứ/lịch sử và giữ đúng account/Page khi đổi tab. Settings có AnalyticsProvider riêng; browser AI dùng profile/pool chung hiện hành, chạy ẩn và login thủ công. Không thêm sidebar hoặc tự phân tích khi chỉ mở trang.

## 4. Hệ thiết kế và khả năng sử dụng

Đây là palette khởi điểm rút từ ảnh, cần kiểm tra tương phản trên UI thật; không phải màu đo chính xác từ ảnh. Mọi màu được dùng qua token ngữ nghĩa của UI_THEME_PLAN, không hardcode từng component.

| Token / mục đích | Dark tham khảo | Light tương ứng |
|---|---|---|
| Nền app | `#0B1220` | `#F4F6FB` |
| Surface/card | `#111D2E` | `#FFFFFF` |
| Surface nổi/hover | `#18263B` | `#EAF0F8` |
| Viền phân vùng | `#33465F` | `#D5DEEB` |
| Chữ chính | `#F4F7FC` | `#172238` |
| Chữ phụ | `#A9B8CD` | `#52627A` |
| Accent/CTA | `#6D3AF2` | `#6230DC` |
| Focus | `#B7A0FF` | `#6230DC` |

- Sidebar khoảng 208–232 px, thu gọn có tooltip/nhãn truy cập; topbar 56–64 px. Spacing theo bước 4/8 px, padding card 16–20 px, khoảng cách khối 16–24 px, bo góc 8–12 px. Tỷ lệ cần điều chỉnh theo viewport thật, không ép sáu panel nhỏ trong ảnh thành kích thước app.
- Font ưu tiên Segoe UI với fallback sans-serif hỗ trợ tiếng Việt, dùng tài nguyên local. Nội dung/form 14–16 px, tiêu đề trang 22–28 px; metadata tối thiểu 12 px. Không sao chép chữ siêu nhỏ của ảnh ghép.
- Nút chính cao tối thiểu 40 px; vùng bấm icon ít nhất 32 px ở chế độ chuột và 44 px khi cần hỗ trợ chạm. Icon nhất quán, icon-only có tên truy cập và tooltip; status có chữ đi kèm.
- Tương phản chữ thường mục tiêu ít nhất 4.5:1; focus, control và dấu trạng thái quan trọng ít nhất 3:1 với nền liền kề. Border trang trí không dùng thay cho dấu nhận diện control.
- Kiểm tra bàn phím xuyên suốt, focus rõ/không bị che; modal quản lý focus và trả về nút mở. Không dùng hover làm cách duy nhất truy cập chức năng.
- Kiểm tra cửa sổ 1280×800 và 1440×900 ở 100%, cùng DPI 125%/150% và kích thước vùng nội dung hữu dụng thực tế. Vùng hẹp thu sidebar, xếp cột; không mất CTA, không cắt form/dialog. Chốt min-window sau kiểm chứng, không hardcode gây mất nội dung ở DPI cao.
- Gradient/glow tím dùng tiết chế ở CTA/selected state; media giữ màu gốc. Animation ngắn khoảng 120–200 ms cho phản hồi, tôn trọng reduced motion, không làm chậm thao tác.
- Mọi màn P0 có loading, empty, lỗi có thử lại, disabled có lý do, lưu thành công/thất bại, mất mạng và `awaiting_user`. UI vẫn chỉnh sửa được khi worker chờ provider.

## 5. Quy tắc browser chạy ẩn — P0

- Mọi tác vụ tự động lấy/tối ưu prompt, viết kịch bản, tạo/tải ảnh, gửi/nhận video và đọc dữ liệu được phép đều chạy **ẩn**. Không tự mở tab/cửa sổ ra desktop, giành focus hoặc bật browser để báo hoàn thành/lỗi.
- Chỉ hiện browser được Fraylo quản lý khi người dùng cần **đăng nhập/đăng nhập lại thủ công**, gồm MFA hoặc xác minh gắn với phiên đăng nhập đó. Người dùng có thể chủ động chọn “Đăng nhập” để bắt đầu luồng; không cần chờ job lỗi.
- “Ẩn” là hành vi người dùng quan sát được, không đồng nghĩa chỉ thu nhỏ cửa sổ. Phương án kỹ thuật (headless hoặc cơ chế chạy ẩn được hỗ trợ) phải chứng minh không ló cửa sổ/giành focus trên Windows. Không mặc định đổi cờ launch là có thể hiện/ẩn phiên đang chạy.
- Không có tùy chọn production “Luôn hiện browser khi chạy job”. Khi adapter không đáp ứng chạy ẩn, tắt automation cho capability đó và báo giới hạn; không âm thầm chuyển sang visible.
- **Ngoại lệ trial (theo yêu cầu người dùng, 2026-10-28):** ChatGPT Web, Gemini Web và MuseAI đang bật trial mode, browser hiển thị khi job chạy để quan sát. Phải gỡ hoặc chốt lại trước khi phát hành. Trạng thái hiện tại xem [STATUS](../agents/STATUS.md).
- **Ngoại lệ kiểm tra ngày 2026-10-30:** theo yêu cầu người dùng, browser do thao tác đăng TikTok tạo ra được hiển thị để quan sát. Cần chốt lại trước khi phát hành.
- Session được lưu trong profile Fraylo đúng loại/account theo quyền dịch vụ. Login không đọc profile browser cá nhân; cookie/token không đưa vào UI/log/Git. Không tự điền/lưu mật khẩu trong form của app.
- Hạn chế tự bật nhiều cửa sổ login: thông báo trong app và chỉ mở khi user chủ động chọn “Đăng nhập”. Cho phép người dùng mở đồng thời nhiều profile độc lập; mỗi profile chỉ có 1 owner, không mở trùng khi bấm nhiều lần và tuân thủ ngân sách tài nguyên max_open_browsers.
- Browser AI mở ẩn khi job lấy slot; xong nhận/lưu kết quả thì đóng nếu toàn profile idle, giữ userDataDir/session. Nhả slot/wake queue sau close/release owner; tab khác còn bận thì chỉ nhả tab, không đóng context. Full pool hiển thị Chờ slot. Uncertain/checkpoint chưa an toàn không bị idle-close; ngoại lệ: job MuseAI chờ đối soát (đã lưu side chat) không giữ browser, vì khôi phục mở lại qua lease. Chi tiết [v1.1 §1.3/§3](../decisions/execution-contract-v1.1.md).

## 6. Login, tiếp tục và lỗi automation

- Phiên thiếu hoặc hết hạn được hiển thị cho đúng tài khoản; người dùng chủ động mở login và nhập thông tin. Không suy ra đăng nhập thành công chỉ từ việc đóng cửa sổ.
- Trong Cài đặt, ẩn nút mở browser khi phiên đã Connected và có danh tính xác minh; khi phiên hết hạn hoặc chưa xác minh, hiện lại nút đăng nhập và trạng thái cần xác minh. Việc phát hiện hết hạn dựa trên kết quả probe hoặc sự kiện runtime, không suy đoán thời hạn cookie.
- Trước khi tiếp tục, xác minh lại phiên, input và quyền sở hữu profile. Job có thể đã submit phải được đối soát; không tự gửi lại khi kết quả chưa chắc chắn.
- Capability chưa xác minh, UI thay đổi hoặc automation bị chặn phải báo lỗi rõ; không tự chuyển sang manual handoff hoặc giả kết quả. Các profile độc lập không bị pause theo nhau.

## 7. Nghiệm thu

Kiểm tra điều hướng và draft, account/login, queue, theme, bàn phím/focus, layout ở DPI 100/125/150/200% và browser ẩn trên Windows thật. Kiểm tra lỗi, hủy và đối soát cùng trạng thái thực tế. Kết quả chưa chạy phải ghi NOT_RUN; fixture browser không chứng minh website thật hoạt động. Không coi kế hoạch này là evidence đã nghiệm thu.
