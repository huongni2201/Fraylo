# Ma trận Năng lực Nhà cung cấp AI (Provider Capability Matrix) — Fraylo

Cập nhật: 2026-10-27 (UTC+07).


> **Delta hiện hành:** [Luồng tạo video chỉ chạy tự động](../plans/AUTOMATIC_VIDEO_WORKFLOW_PLAN.md) đã bỏ Manual Handoff. Automation vẫn `NOT_VERIFIED`; bỏ handoff không nâng trạng thái xác minh hoặc chứng minh website thật đã chạy.

Nguồn chuẩn: [PRODUCT_IMAGE_VIDEO_WORKFLOW_PLAN.md](../plans/PRODUCT_IMAGE_VIDEO_WORKFLOW_PLAN.md), [execution-contract-v1.1.md](execution-contract-v1.1.md), [VIDEO_FIRST_ARCHITECTURE_PLAN.md](../plans/VIDEO_FIRST_ARCHITECTURE_PLAN.md).

Tài liệu này xác lập ma trận năng lực chính thức của 3 nhà cung cấp AI: **ChatGPT Web**, **Gemini Web**, và **MuseAI** cho quy trình sản xuất video từ ảnh mẫu và sản phẩm.

---

## 1. Nguyên tắc Quản lý Năng lực (Capability Governance)

1. **Tuyệt đối không giả mạo xác minh (No Fabricated Verification):**
   - Trạng thái `VERIFIED` chỉ được cấp khi đã có bằng chứng kiểm thử tự động hóa thực tế (Playwright automation) trên giao diện website trực tiếp với tài khoản live.
   - Khi chưa có phiên đăng nhập trực tiếp của người dùng (`A13 Manual Login`) và chưa chạy xác minh selector trên website thực, trạng thái năng lực bắt buộc phải ghi là **`NOT_VERIFIED`**.
   - Tuyệt đối không đoán selector, không giả lập tiến trình tự động hóa thành công trong cơ sở dữ liệu khi chạy production.

2. **Định tuyến nghiêm ngặt, cấm Fallback ngầm (Strict Routing, No Silent Fallback):**
   - Loại tác vụ nào cấu hình cho ChatGPT thì chỉ dispatch tới tài khoản/profile của ChatGPT.
   - Tuyệt đối không tự ý hoán đổi ngầm sang Gemini hoặc ngược lại khi gặp lỗi hoặc thiếu năng lực.

3. **Chỉ thực thi tự động:**
   - Capability chưa xác minh hoặc thiếu adapter trả lỗi `automation_blocked` / `adapter_not_available`, job sang `Failed` trước submit, không retry cùng thiếu sót cấu hình.
   - Gỡ `job.get_handoff`, `job.import_result` và UI nhập kết quả. Giữ đăng nhập tài khoản; khi đã submit mà chưa rõ kết quả, ưu tiên `reconcile_required`.

4. **Hành vi âm thanh (Audio Behavior) khi tạo video:**
   - Quyết định đã chốt: **Audio là tùy chọn (Không bắt buộc)**. Mặc định là không gửi audio (`AudioMode = None`).
   - **Lưu ý đặc thù về Provider:** "Không gửi audio" có nghĩa là hệ thống Fraylo không tải tệp âm thanh lên và không gửi chỉ thị audio tới nhà cung cấp. Tuy nhiên, điều này **không đồng nghĩa video xuất xưởng từ MuseAI chắc chắn im lặng tuyệt đối**. Một số phiên bản web của MuseAI có thể tự động gán nhạc nền ngẫu nhiên hoặc xuất video không có audio track. Hệ thống Fraylo phản ánh trung thực kết quả của provider, không can thiệp cắt ghép hay lọc audio cục bộ.

---

## 2. Ma trận Năng lực Chi tiết (Capability Matrix)

| Nhà cung cấp (Provider) | Loại tác vụ (TaskType) | Năng lực đặc thù yêu cầu | Trạng thái xác minh (Verification Status) | Hỗ trợ Tự động (Automation) | Hỗ trợ Thủ công (Manual Handoff) | Ghi chú kỹ thuật & Ràng buộc |
|---|---|---|---|---|---|---|
| **ChatGPT Web** (`chatgpt-web`) | `Script` | Lập danh sách cảnh từ brief ngôn ngữ tự nhiên | `NOT_VERIFIED` | `false` | `false` | Đòi hỏi prompt cấu trúc JSON; trả về danh sách các cảnh có mục đích rõ ràng. |
| **ChatGPT Web** (`chatgpt-web`) | `Image` | **Nhận 2 ảnh tham chiếu (Mẫu + Sản phẩm)** | `NOT_VERIFIED` | `false` | `false` | Trên giao diện web, tính năng nhận 2 ảnh đính kèm đồng thời để tạo ảnh biến thể cần được kiểm chứng live; chưa verify thì chặn automation. |
| **ChatGPT Web** (`chatgpt-web`) | `VideoPrompt` | **Đọc ảnh đã chọn để viết prompt chuyển động** | `NOT_VERIFIED` | `false` | `false` | Gửi ảnh kết quả hoặc ảnh sẵn sàng vào chat, yêu cầu mô tả chuyển động camera/chủ thể; chưa verify thì chặn automation. |
| **ChatGPT Web** (`chatgpt-web`) | `VideoRender` | Tạo video clip từ ảnh và prompt | `UNSUPPORTED` | `false` | `false` | ChatGPT không hỗ trợ trực tiếp render video clip; bị chặn ngay từ khâu validate cấu hình. |
| **Gemini Web** (`gemini-web`) | `Script` | Lập danh sách cảnh từ brief ngôn ngữ tự nhiên | `NOT_VERIFIED` | `false` | `false` | Hỗ trợ xử lý văn bản ngữ cảnh dài; xuất JSON schema danh sách cảnh. |
| **Gemini Web** (`gemini-web`) | `Image` | **Nhận 2 ảnh tham chiếu (Mẫu + Sản phẩm)** | `NOT_VERIFIED` | `false` | `false` | Cần xác minh khả năng nhận đa ảnh đầu vào của giao diện Gemini Web; chưa có test live thì chặn automation. |
| **Gemini Web** (`gemini-web`) | `VideoPrompt` | **Đọc ảnh đã chọn để viết prompt chuyển động** | `NOT_VERIFIED` | `false` | `false` | Mô hình thị giác của Gemini có khả năng đọc ảnh tốt; cần kiểm chứng DOM input trên website thật. |
| **Gemini Web** (`gemini-web`) | `VideoRender` | Tạo video clip từ ảnh và prompt | `UNSUPPORTED` | `false` | `false` | Gemini Web không hỗ trợ render video trực tiếp; bị chặn tại validator. |
| **MuseAI** (`museai`) | `VideoRender` | **Tạo video clip không gửi audio** | `NOT_VERIFIED` | `false` | `false` | Chỉ nhận 1 ảnh được chọn + video prompt. Cho phép bỏ qua tệp âm thanh. Video kết quả có thể có âm thanh mặc định của provider. |
| **MuseAI** (`museai`) | `VideoRender` | **Tạo video clip có tệp audio đính kèm** | `NOT_VERIFIED` | `false` | `false` | Chỉ hỗ trợ khi người dùng chọn chế độ `CustomAudio` và cung cấp tệp MP3/WAV hợp lệ. |
| **MuseAI** (`museai`) | `Script` | Lập kịch bản | `UNSUPPORTED` | `false` | `false` | Không hỗ trợ tác vụ kịch bản. |
| **MuseAI** (`museai`) | `Image` | Tạo ảnh tĩnh | `UNSUPPORTED` | `false` | `false` | Không hỗ trợ tác vụ tạo ảnh tĩnh. |
| **MuseAI** (`museai`) | `VideoPrompt` | Viết prompt chuyển động | `UNSUPPORTED` | `false` | `false` | Không hỗ trợ tác vụ viết prompt chuyển động. |

---

## 3. Xử lý lỗi và dữ liệu lịch sử

- Lỗi mạng trước submit được retry hữu hạn; lỗi đăng nhập yêu cầu đăng nhập lại trong Cài đặt.
- Adapter/capability chưa sẵn sàng không có manual fallback. UI hiển thị lý do; backend chặn resume/retry các reason handoff cũ.
- Job lịch sử vẫn xem và hủy được, giữ snapshot và kết quả. Không chuyển dữ liệu cũ thành thành công hoặc tự gửi lại.
- `ManualSupported` giữ trên DTO để tương thích wire và luôn là `false`; không còn service/contract RPC nhập kết quả thủ công.
- Website/account thật: `NOT_RUN`. Năng lực phải có evidence riêng trước khi bật automation.
