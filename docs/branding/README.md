# Fraylo — nhận diện ứng dụng

Theo yêu cầu trực tiếp ngày 2026-11-01, **Fraylo** thay thế tên sản phẩm TiToAI. Đây là thay đổi thương hiệu; kiến trúc và dữ liệu không đổi.

- Tên hiển thị: **Fraylo**; wordmark trong sidebar: **fraylo**.
- Tagline: **Ideas into motion.**
- Logo: chữ F từ hai khung hình dịch chuyển, cam san hô `#FF5A45`. Logo giữ màu gốc trên cả giao diện sáng và tối.
- [Ảnh ý tưởng đã chọn](fraylo-concept.png) là tham khảo. SVG là bản dựng lại để tích hợp, không phải ảnh crop từ bảng ý tưởng.
- SVG gốc: [fraylo-mark.svg](../../src/Fraylo.Web/public/brand/fraylo-mark.svg). Icon SVG và ICO dùng cùng hình học, có nền cam và chữ F trắng.
- Tạo lại icon Windows 16/20/24/32/40/48/64/128/256 px bằng `powershell.exe -NoProfile -File scripts/build-brand-icons.ps1` (Windows, không cần dependency mới).

## Chạy và đóng gói

Mở [Chay-Fraylo.cmd](../../Chay-Fraylo.cmd) để build và chạy bản mới. Desktop tạo `Fraylo.exe`, launcher tạo `FrayloLauncher.exe`; bộ cài/zip mang tiền tố `Fraylo-`. Luồng build, cập nhật và bộ cài cùng dùng icon mới.

Giữ Inno Setup AppId để nâng cấp bản đã cài; bộ cài đổi shortcut sang Fraylo. Bản publish có thêm apphost `TiToAI.Desktop.exe` trỏ tới cùng assembly Fraylo để launcher cũ có thể nhận gói đổi tên. Launcher mới có thể mở bản TiToAI cũ khi quay về phiên bản trước.

Sau yêu cầu bổ sung, thư mục dự án là `D:\workspace\Fraylo`, solution `Fraylo.sln`, thư mục source/test, csproj, namespace, DbContext và resource đều dùng Fraylo. Giữ GitHub repo, ID manifest, `%LOCALAPPDATA%/TiToAI`, `titoai.db`, lock profile, origin media và alias executable cũ để dùng tiếp dữ liệu, phiên đăng nhập và đường nâng cấp. Các plan lịch sử dùng TiToAI/AIContentStudio được hiểu là Fraylo; không đổi tên hàng loạt evidence lịch sử.

## Repo GitHub

Sau khi push main, người dùng đổi tên repo GitHub từ `huongni2201/TitoAI` thành `huongni2201/Fraylo` tại Settings → General → Repository name. GitHub chuyển hướng URL Git cũ; nên cập nhật origin bằng `git remote set-url origin https://github.com/huongni2201/Fraylo.git` sau khi rename. Updater hiện dùng endpoint repo cũ và HttpClient theo redirect; không tái sử dụng tên repo cũ cho repo khác. Không đổi tên kho dữ liệu khi rename repo.
