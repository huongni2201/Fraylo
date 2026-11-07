<p align="center">
  <img src="src/Fraylo.Web/public/brand/fraylo-mark.svg" alt="Logo Fraylo" width="80" />
</p>

<h1 align="center">Fraylo</h1>

<p align="center"><strong>Ideas into motion.</strong></p>
<p align="center">Ứng dụng Windows hỗ trợ sản xuất video với AI và quản lý đăng TikTok, Facebook.</p>

Fraylo tập hợp kịch bản, ảnh tham chiếu, prompt, video và lịch sử xử lý trong một ứng dụng desktop. Dự án, tài nguyên và phiên làm việc được quản lý trên máy. Fraylo kết nối AI, TikTok và Facebook qua trình duyệt.

Tải bộ cài Windows từ [trang phát hành Fraylo](https://github.com/huongni2201/Fraylo/releases/latest).

> **Trạng thái** · Các adapter ChatGPT, Gemini và MuseAI đang ở chế độ thử nghiệm. Xem phạm vi hỗ trợ và kiểm thử Facebook trong [tài liệu đăng bài](docs/plans/FACEBOOK_PUBLISHING_PLAN.md).

## Tính năng chính

- **Quản lý dự án và nhóm:** tổ chức công việc sản xuất video, dùng chung tài nguyên và cấu hình prompt trong nhóm.
- **Sản xuất video với AI:** hỗ trợ luồng tạo nhiều video theo cảnh hoặc một video nhiều phân cảnh; quản lý kịch bản, ảnh và prompt qua từng bước.
- **Thư viện tài nguyên:** lưu trữ và xem trước ảnh, âm thanh, video ngay trong ứng dụng.
- **Theo dõi tác vụ:** xem hàng đợi, tiến trình, kết quả và nhật ký; hủy hoặc thử lại thủ công khi cần.
- **Quản lý tài khoản:** tách phiên trình duyệt cho tài khoản AI và TikTok, đăng nhập thủ công.
- **Đăng TikTok theo yêu cầu:** chọn video, tài khoản và nội dung đăng. Video tạo xong không tự động được đăng.
- **Facebook:** đăng nhóm và cá nhân, hẹn giờ, xem nhật ký từng lượt và cập nhật số liệu Fanpage.

## Bắt đầu với mã nguồn

### Yêu cầu môi trường

| Thành phần | Phiên bản / yêu cầu |
| --- | --- |
| Hệ điều hành | Windows với WebView2 Runtime |
| .NET SDK | `10.0.403`, theo [global.json](global.json) |
| Node.js | `24.19.0`, theo [package.json](src/Fraylo.Web/package.json) |
| pnpm | `12.15.0`, theo [package.json](src/Fraylo.Web/package.json) |
| Kết nối mạng | Cần để khôi phục dependency, sử dụng AI và đăng TikTok |

Các bước tạo nội dung và đăng video cần tài khoản tương ứng đã đăng nhập trong ứng dụng.

### Cài dependency và chạy

Mở PowerShell tại thư mục gốc của repository:

```powershell
dotnet restore .\Fraylo.sln
pnpm --dir .\src\Fraylo.Web install --frozen-lockfile
.\Chay-Fraylo.cmd
```

Đóng Fraylo đang chạy trước khi mở bản mới. Tập lệnh [Chay-Fraylo.cmd](Chay-Fraylo.cmd) build ứng dụng cùng giao diện web, kiểm tra tài nguyên đóng gói rồi mới khởi chạy. Nếu một bước thất bại, tập lệnh dừng và hiển thị lỗi.

### Build và kiểm tra

Build toàn bộ solution ở cấu hình Release:

```powershell
dotnet build .\Fraylo.sln -c Release -m:1
```

Build và chạy bộ kiểm thử giao diện web:

```powershell
pnpm --dir .\src\Fraylo.Web build
pnpm --dir .\src\Fraylo.Web test
```

Các bộ smoke test backend nằm trong [tests/](tests). Kết quả kiểm thử bằng fixture và trên dịch vụ thật được ghi riêng trong tài liệu của từng tính năng.

## Quy trình sử dụng

1. Kết nối tài khoản AI trong Cài đặt; kết nối TikTok khi cần đăng video.
2. Tạo dự án, chọn chiến lược video và thêm ảnh hoặc tài nguyên đầu vào.
3. Cấu hình prompt, bắt đầu sản xuất và theo dõi tác vụ trong trang Tiến trình.
4. Xem lại nội dung, ảnh và video kết quả trước khi sử dụng.
5. Chọn video và tài khoản TikTok, kiểm tra nội dung rồi chủ động yêu cầu đăng.

## Công nghệ và cấu trúc

Fraylo tổ chức theo kiến trúc modular monolith: WPF làm host desktop, WebView2 hiển thị giao diện React, SQLite lưu dữ liệu cục bộ và Playwright thực hiện các thao tác trình duyệt.

| Thư mục | Vai trò |
| --- | --- |
| `src/Fraylo.Desktop` | Ứng dụng WPF, WebView2 và bridge giữa giao diện với backend |
| `src/Fraylo.Web` | Giao diện React, TypeScript, Vite và Tailwind CSS |
| `src/Fraylo.Application` | Luồng nghiệp vụ, sản xuất video và điều phối tác vụ |
| `src/Fraylo.Contracts` | DTO và hợp đồng giao tiếp giữa các mô-đun |
| `src/Fraylo.Infrastructure` | SQLite, Entity Framework Core và lưu trữ tài nguyên |
| `src/Fraylo.Browser` | Quản lý phiên trình duyệt, adapter AI và đăng TikTok |
| `src/Fraylo.Launcher` | Khởi chạy ứng dụng và hỗ trợ cập nhật |
| `tests` | Các bộ smoke test và kiểm tra tích hợp |
| `docs` | Kiến trúc, kế hoạch triển khai, hướng dẫn và bằng chứng kiểm thử |

## Tài liệu

- [Mục lục tài liệu](docs/README.md) — điểm bắt đầu để tra cứu theo chủ đề.
- [Trạng thái hiện hành](docs/agents/STATUS.md) — tiến độ, kết quả kiểm tra và giới hạn đã biết.
- [Kiến trúc video-first](docs/plans/VIDEO_FIRST_ARCHITECTURE_PLAN.md) và [hai luồng sản xuất video](docs/plans/VIDEO_TWO_FLOW_SPLIT.md).
- [Tích hợp đăng TikTok](docs/plans/TIKTOK_PUBLISHING_HANDOFF.md).
- [Nhận diện Fraylo](docs/branding/README.md) — logo và tên sản phẩm.

## Dữ liệu và giới hạn hiện tại

- Thư mục dữ liệu mặc định là `%LOCALAPPDATA%\Fraylo`, database `fraylo.db`. Dữ liệu của bản TiToAI cũ không được chuyển sang; app tạo database trống khi chạy lần đầu.
- Tác vụ lỗi thông thường không tự động thử lại; người dùng kiểm tra nguyên nhân và quyết định thử lại thủ công.
- Nếu TikTok đã nhận bài nhưng ứng dụng chưa đọc được xác nhận, đăng lại sau lỗi có thể tạo bài trùng. Kiểm tra tài khoản TikTok trước khi đăng lại.
- Chức năng sao lưu/khôi phục đầy đủ chưa hoàn tất. Xem [STATUS](docs/agents/STATUS.md) trước khi dựa vào các khả năng đang phát triển.
