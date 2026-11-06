# Fraylo — bằng chứng giao diện Facebook (Giai đoạn A)

Ngày: 2026-11-06. Thuộc [FACEBOOK_PUBLISHING_PLAN](FACEBOOK_PUBLISHING_PLAN.md) §9-A. Trạng thái: **ĐANG LÀM — trang cá nhân có đủ luồng ảnh + chữ, từ composer tới kết quả `Published`; chưa có Page, Group, video, `PendingApproval`, bản tiếng Việt.** Đã đăng đúng **một** bài thử `Only me` theo chỉ định của người dùng và đã chuyển vào thùng rác (E11–E14). Tên người dùng, ID, tên nhóm và URL cá nhân đã được ẩn.

## Cách thu thập

- Probe tạm (không commit) dùng **đúng đường launch của Fraylo**: `PlaywrightBrowserController`, profile kind `Facebook`, `Headless = true`, `Channel = chrome`, **không** cờ `AutomationControlled` (D4). Phiên là phiên người dùng đã đăng nhập thủ công qua Cài đặt → Tài khoản.
- Đọc cây truy cập (ARIA snapshot) và thao tác composer: mở, đính một ảnh PNG 640×360 do probe tạo, gõ chữ, đọc lại, đóng và xóa nháp. Lượt quan sát đầu **không** bấm Đăng. Sau đó có đúng **một bài thử thật** (E11), do người dùng cho phép riêng, đối tượng `Only me`; probe tự dừng nếu đối tượng khác `Only me` hoặc nội dung đọc lại không khớp.
- DB dùng bản sao tạm, không ghi vào `fraylo.db` của ứng dụng đang chạy.
- Giao diện Facebook của tài khoản test là **tiếng Anh**; tên nút dưới đây là tiếng Anh. Chưa có bằng chứng tiếng Việt.

## Kết quả

| # | Quan sát | Hệ quả cho adapter |
|---|---|---|
| E1 | Headless tới được trang chủ đã đăng nhập: có cookie `c_user` + `xs`, tiêu đề `Facebook`, không bị đẩy sang `/login` hay `/checkpoint`. URL bị chuyển sang `web.facebook.com`. | Chuẩn hóa/so khớp URL phải chấp nhận cả `www.`, `web.`, `m.facebook.com`. |
| E2 | Trong `main` có hộp thông báo "Remember Password" với nút `OK` / `Not Now`, chưa cản composer. | Giống TikTok: chỉ xử lý đúng thông báo đã biết (nút `Not Now`), không đóng hộp lạ. |
| E3 | Vùng `Create a post` chứa nút `What's on your mind, <tên>?`. Tên người dùng nằm trong tên nút. | Tìm nút bằng regex không phụ thuộc tên: `What's on your mind`. Cần regex tiếng Việt khi có bằng chứng. |
| E4 | Bấm nút mở `dialog "Create post"` (lồng một `dialog "Create post"` bên trong, nên `getByRole(dialog)` trả 2). Dùng dialog ngoài cùng. | Locator composer = dialog ngoài cùng; không dùng `.Last`. |
| E5 | Composer hiển thị tên người đăng (liên kết `<tên>'s Timeline` + chữ tên) và nút quyền riêng tư `Edit privacy. Sharing with Only me.` | Có chỗ đọc **actor** và **đối tượng xem** trước khi đăng. Tài khoản test mặc định `Only me`; tài khoản khác có thể mặc định Public. Cần quyết định adapter có kiểm/đặt đối tượng xem hay không (mục "Câu hỏi mở"). |
| E6 | `textbox "What's on your mind, <tên>?"`. Nút `Post` **disabled** khi trống; enabled khi có chữ hoặc có media. | Điều kiện sẵn sàng = `Post` enabled **và** nội dung đọc lại khớp. |
| E7 | Bấm `Photo/video` làm xuất hiện **2** `input[type=file]`, cùng `accept=image/*,image/heif,image/heic,video/*,video/mp4,…,.mov,.webm,…`. Thuộc tính `multiple` đọc ra rỗng (chưa phân biệt được "không có" và "rỗng"). | `SetInputFilesAsync` trên input trong dialog hoạt động khi chạy ẩn. Chưa xác minh nhiều ảnh một lần, chưa thử video. |
| E8 | Sau khi đính ảnh: `group "Attached media"` có `img` tên = tên tệp; nút `Remove post attachment`; `Life update` bị khóa kèm chú thích "This can't be combined…". | Có thể xác nhận số/tên media đã đính qua `Attached media`. |
| E9 | **Thứ tự quan trọng.** Gõ chữ rồi mới đính ảnh: đọc lại textbox sau khi đính cho kết quả **rỗng** (chưa cô lập được nguyên nhân: mất chữ hay locator đổi). Đính ảnh trước rồi gõ chữ: chữ đọc lại đúng. | Adapter: **đính media trước, gõ chữ sau**, và luôn đọc lại textbox + `Attached media` trước `beforeSubmit` (đã nằm trong plan §6). |
| E11 | **Bài thử thật #1 (người dùng cho phép, 2026-11-06 15:26):** chữ + 1 ảnh, đối tượng `Only me`, trang cá nhân, chạy ẩn. Trước khi bấm: đã kiểm nút `Edit privacy. Sharing with Only me.`, đọc lại textbox khớp từng ký tự, `Attached media` = 1, `Post` enabled. Chỉ bấm **một lần**. | Mẫu kiểm trước `beforeSubmit` cho adapter (đối tượng xem, nội dung đọc lại, số media). |
| E12 | Sau click: `Post` chuyển disabled, xuất hiện `alert "Posting"` + `status "Loading..."` (≥ 2 giây). Trong khoảng 3–7 giây sau click: `alert "Your post has been published"`. URL **không đổi**. Composer (`dialog "Create post"`) vẫn còn trong cây nhưng đã reset (textbox rỗng, `Post` disabled) — **không dùng "dialog đóng" làm tín hiệu**. | Bằng chứng `Published` = `role=alert` khớp `Your post has been published` (cần bản VI). `Posting` là trạng thái đang gửi, không phải thành công. Hết thời gian chờ mà không thấy alert → `Unknown`. |
| E13 | Bài hiện trên dòng thời gian: văn bản `Shared with Only me`, nút `Actions for this post by <tên>`, liên kết ảnh dạng `.../photo/?fbid=<id>&set=...`. Bài **không** nằm trong `role=article` có nội dung (chỉ có `article` placeholder "Loading…"); cây có thể có 2 nút DOM chứa cùng chữ (1 ẩn). | URL bài (tùy chọn) đọc từ liên kết `photo/?fbid=` hoặc permalink; xác nhận bài bằng chữ **hiển thị** + `Shared with …`, không dựa vào `article`. |
| E14 | Xóa bài thử: `Actions for this post` → `menuitem "Move to trash"` (mục có chữ "Items in your trash are deleted after 30 days") → `dialog "Move to your trash?"` có `Move` / `Cancel`. Sau `Move` và tải lại, bài không còn trên dòng thời gian. Menu còn `Move to archive`, `Edit post`, `Edit audience…`. | Chỉ để dọn bài thử; Fraylo bản đầu **không** xóa/sửa bài (ngoài phạm vi). Khi dọn bằng probe phải chọn đúng nút, tuyệt đối không nhầm `Move to archive`. |
| E10 | Nhấn `Escape` khi composer có nội dung mở `dialog "Save this post as a draft?"` với `Save as draft` / `Delete draft`. Bấm `Delete draft` rồi mở lại composer: composer trống (Post disabled), không còn nháp. | Nhánh dọn dẹp khi hủy giữa chừng: `Escape` → `Delete draft`. Không bao giờ bấm `Save as draft`. |

## Đánh giá cổng G-A (tạm thời)

| Điều kiện | Trạng thái |
|---|---|
| (1) Chạy ẩn qua đường launch Fraylo tới composer của cả ba loại đích | **Một phần:** trang cá nhân ĐẠT (E1–E8). Page, Group: chưa thử — cần URL đích test. |
| (2) Phân biệt `Published` / `PendingApproval` / lỗi | **Một phần:** `Published` ở trang cá nhân có bằng chứng (E12). Chưa có `PendingApproval` (cần Group bật phê duyệt bài), chưa có thông báo lỗi từ chối, chưa có bản VI. |
| (3) Đọc actor để so với đích | **Một phần:** trang cá nhân đọc được (E5). Page cần xem cách chuyển danh tính. |

## Việc còn lại của Giai đoạn A

1. Page test: mở trang, quan sát cách đăng với tư cách Page (chuyển danh tính hay composer riêng), đọc actor, đính ảnh, dừng trước `Post`.
2. Group test (đã bật phê duyệt bài): mở composer, đọc tên nhóm/đối tượng xem, đính ảnh, dừng trước `Post`.
3. Video: đính một video ngắn ở từng loại đích, quan sát trạng thái upload/xử lý và khi nào `Post` enabled.
4. Với sự đồng ý riêng cho từng bài: bài thử thật ở Page và Group — ghi thông báo thành công, thông báo chờ duyệt, dạng URL bài, thời gian. (Trang cá nhân + ảnh đã xong; còn bài chỉ chữ và bài video nếu cần.)
5. Bằng chứng tiếng Việt (đổi ngôn ngữ tài khoản test) và bằng chứng nhiều ảnh một bài.

## Câu hỏi mở rút ra từ bằng chứng

- **Đối tượng xem (audience):** E5 cho thấy composer dùng mặc định của tài khoản. Đề xuất cho bản đầu: adapter **đọc** đối tượng xem và hiển thị trong hộp xác nhận lô; chỉ đăng khi người dùng chấp nhận (không tự đổi). Cần người dùng xác nhận.
- Giới hạn ký tự, số ảnh tối đa một bài, kích thước video tối đa: chưa đo.
