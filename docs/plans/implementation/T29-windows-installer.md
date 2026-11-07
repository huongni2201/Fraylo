# Installer Windows

Phạm vi theo [kiến trúc video-first](../VIDEO_FIRST_ARCHITECTURE_PLAN.md); trạng thái, dependency và evidence hiện hành xem [TASKS](../../agents/TASKS.md), [STATUS](../../agents/STATUS.md). Tên task là định danh lịch sử, không tự xác nhận tính năng đã nghiệm thu.

## Phạm vi

Đóng gói ứng dụng self-contained cùng nội dung React local và chiến lược WebView2/browser dependency; bảo toàn dữ liệu theo lựa chọn người dùng.

## Lựa chọn dữ liệu khi gỡ cài đặt (2026-11-06)

- Sau khi gỡ chương trình, hỏi có xóa toàn bộ `%LOCALAPPDATA%\Fraylo` hay không; mặc định **Không** để giữ dữ liệu cho lần cài sau.
- Chọn **Có** xóa database/dự án, cấu hình, profile/phiên đăng nhập, logs, backup và media bên trong thư mục này. Không thể hoàn tác.
- Tệp lưu hoặc xuất ở root tùy chỉnh bên ngoài được giữ nguyên; hộp thoại ghi rõ giới hạn này. Không xóa thư mục tùy ý lấy từ database hoặc cấu hình.
- Gỡ im lặng (`/SILENT`, `/VERYSILENT`) hoặc bỏ qua hộp thoại (`/SUPPRESSMSGBOXES`) luôn giữ dữ liệu.
- Nếu xóa không hết (ví dụ tệp đang bị khóa), báo thư mục còn lại và hướng dẫn đóng ứng dụng/browser rồi xóa thủ công.

## Kiểm tra và nghiệm thu

Kiểm tra install, chạy, upgrade và uninstall trên Windows sạch; ghi rõ NOT_RUN nếu chưa có môi trường.

Ngày 2026-11-06: Inno Setup 6.7.3 biên dịch nguyên script với payload fixture và icon thật PASS; không chạy bộ cài fixture. Kiểm tra Có/Không, silent/suppress, tệp bị khóa và dữ liệu ngoài root trên Windows sạch: **NOT_RUN**. Chưa đóng gói lại bộ cài phát hành.
