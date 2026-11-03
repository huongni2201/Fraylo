# Fraylo — bổ sung plan giao diện sáng/tối

**Phiên bản:** 1.2 — bổ sung ngày 2026-10-26.  
**Mức ưu tiên:** P0 trong MVP.  
**Trạng thái:** spec hiện hành; nền host/UI đã có một phần, persistence và nghiệm thu theo [STATUS](../agents/STATUS.md). Các bước còn thiếu ở [plan theme](implementation/T06a-theme-persistence.md).

Tài liệu này bổ sung `SOURCE_PLAN.md` theo yêu cầu mới của người dùng. Áp dụng cùng plan gốc; các nội dung theme dưới đây mở rộng §1, §3, §5, §11, §12 và Definition of Done. Bản plan gốc được giữ nguyên để đối chiếu.

Đọc cùng [UI_BROWSER_PLAN.md](UI_BROWSER_PLAN.md) khi thiết kế/review UI: bố cục và palette tham khảo nằm tại §2–4 của plan đó. Ảnh Dark là định hướng thị giác, không thay mặc định System hoặc yêu cầu hỗ trợ Light. Browser provider chỉ hiện cho login theo plan đó; theme app không điều khiển website bên ngoài.

## 1. Hành vi người dùng

- Hỗ trợ **Sáng (Light)**, **Tối (Dark)** và **Theo hệ thống (System)**. Mặc định lần đầu là Theo hệ thống Windows.
- Có lựa chọn giao diện trong trang Cài đặt → Giao diện theo `SETTINGS_PLAN.md`; bộ chuyển nhanh trên app shell dùng cùng một preference.
- Đổi giao diện có hiệu lực ngay, không cần khởi động lại; giữ nguyên màn hình hiện tại, nội dung đang nhập và job đang chạy.
- Lựa chọn được lưu và phục hồi khi mở lại ứng dụng. System tự cập nhật khi giao diện Windows đổi; Light/Dark được chọn thủ công giữ nguyên khi Windows đổi.

## 2. Phạm vi và thiết kế

- Đồng bộ giao diện React trong WebView2 với các vùng do WPF kiểm soát: nền cửa sổ, loading/startup và hộp thoại riêng của ứng dụng. Hộp thoại Windows do hệ điều hành quản lý theo khả năng nền tảng.
- Dùng token ngữ nghĩa chung cho nền, surface, chữ, viền, accent, focus và trạng thái success/warning/error. Component lấy màu từ token; tránh đặt màu sáng/tối riêng lẻ ở từng màn hình.
- Bao phủ app shell, sidebar, form, bảng, prompt editor, library, Browser Manager, queue, pipeline, Settings và analytics khi có. Tooltip, dropdown, dialog, empty/loading/error/disabled states đều có hai theme.
- Chart/legend/grid và trạng thái job phải đọc được trong cả hai theme; trạng thái có nhãn hoặc biểu tượng, không chỉ phân biệt bằng màu.
- Văn bản, nút và focus bàn phím có độ tương phản dễ đọc ở cả hai theme. Không đảo màu ảnh/video hoặc thay đổi file media gốc.
- Theme chỉ tác động giao diện Fraylo. Không tự chỉnh theme hay nội dung website ChatGPT/Gemini/MuseAI/TikTok đang mở trong browser riêng.
- Áp dụng theme đã xác định trước khi hiển thị nội dung chính để tránh chớp nền sáng khi mở ứng dụng ở Dark.

## 3. Lưu trữ và contract

- Lưu preference ở `app_settings`, key `ui.theme`, giá trị `system | light | dark`; không cần bảng hoặc dịch vụ riêng.
- Phân biệt preference do người dùng chọn với theme hiệu lực `light | dark`. Ở System, theme hiệu lực được tính từ Windows, không ghi đè preference thành Light/Dark.
- T02 chốt contract đọc/cập nhật preference và đồng bộ theme hiệu lực giữa host/UI qua kênh đã chọn. Host và UI có một nguồn trạng thái thống nhất, không có hai bộ chuyển đổi độc lập.
- T05/T06 chuẩn bị nền host và token UI. T06a tích hợp persistence với T04; xác nhận preference hợp lệ và xử lý đổi theme trong runtime.
- Thiếu hoặc sai giá trị thì fallback System. Khi đọc thiết lập lỗi, dùng System để UI vẫn mở được và báo lỗi phù hợp; không ghi đè preference cũ chỉ vì lỗi đọc tạm thời. Nếu lưu thất bại, phản hồi rõ thay đổi chỉ có hiệu lực trong phiên hiện tại.

## 4. Task và ownership

- T02 bổ sung contract theme và danh sách token dùng chung.
- T05 phụ trách nền host/startup và đồng bộ phía WPF; T06 phụ trách token, shell và controls phía React.
- **T06a — tích hợp theme**, sau T04/T05/T06: hoàn tất ba lựa chọn, lưu preference, System runtime updates và kiểm tra khởi động lại. UI owner tích hợp; chỉnh file host/contract chung qua integrator.
- T06a implementation được coordinator nghiệm thu ngày 2026-10-26 sau SQLite smoke và WPF/WebView2 restart checks cho System/Dark/Light. Đổi Windows theme khi app đang mở và fault-injection lỗi đọc/lưu vẫn là release checks bắt buộc tại T30; đây là chuyển thời điểm kiểm tra, không miễn tiêu chí §5.
- T06a là điều kiện đạt G1. Các UI task sau đó dùng token hiện có và kiểm tra cả Light/Dark; không tạo hệ theme thứ hai.
- T30 bổ sung nhóm kiểm thử theme trước release, cùng các nhóm test trong §11 của plan gốc.

## 5. Tiêu chí nghiệm thu và Definition of Done bổ sung

1. Lần đầu mở ở Windows sáng/tối, System cho giao diện tương ứng; không chớp nền sai theme trước màn hình chính.
2. Đổi Light ↔ Dark qua Settings hoặc bộ chuyển nhanh: host/UI đồng bộ ngay, không mất dữ liệu đang nhập, navigation hay trạng thái job.
4. Đổi giao diện Windows khi app đang mở: System phản ứng ngay, lựa chọn Light/Dark thủ công không bị ghi đè.
5. Kiểm tra các màn hình và trạng thái ở §2 trong cả Light/Dark, bao gồm keyboard focus, dialog và chart nếu có; media giữ nguyên màu gốc.
6. Thiết lập thiếu/sai, lỗi đọc hoặc lỗi lưu đều có fallback/phản hồi như §3; lỗi theme không ngăn app mở hoặc làm gián đoạn scheduler.

G1 cần đạt phần tích hợp theme nền tảng. MVP chỉ hoàn tất sau khi kiểm tra theme trên toàn bộ màn hình P0 tại T30; các màn hình P1 cũng phải đáp ứng khi được triển khai. Ghi rõ bằng chứng chạy thật và NOT_RUN nếu chưa kiểm tra.
