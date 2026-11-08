# Fraylo Video Saver

Tiện ích Chrome Manifest V3, chạy độc lập với Fraylo Desktop. Yêu cầu Chrome 126 trở lên.

## Dùng thử

1. Mở `chrome://extensions`, bật **Developer mode** rồi chọn **Load unpacked**.
2. Chọn thư mục `extension` bên cạnh tài liệu này.
3. Dừng ở video muốn lưu trong **Đề xuất** hoặc mở riêng bài video. Mở tiện ích và chọn **Tìm link tải video đang xem**. Tiện ích tìm địa chỉ tải TikTok cung cấp cho bài đang hiện giữa màn hình hoặc bài đang mở riêng. Bạn cũng có thể dán link HTTPS trực tiếp đến tệp video mà bạn được phép tải.
4. Đánh dấu xác nhận quyền tải rồi chọn **Lưu tệp**. Xem kết quả trong `chrome://downloads`.

Link chia sẻ bài đăng, luồng phát phân đoạn và video chỉ có nguồn phát `blob:` cần một nguồn tải tệp khác. Nếu TikTok cho phép tải bài, có thể dùng **Chia sẻ → Lưu video** trong ứng dụng TikTok. [Hướng dẫn TikTok](https://support.tiktok.com/en/using-tiktok/exploring-videos/video-downloads)

## Kiểm tra và đóng gói

Chạy từ thư mục gốc workspace bằng PowerShell.

```powershell
node --test tools/tiktok-video-extension/popup.test.cjs
New-Item -ItemType Directory -Path dist -Force | Out-Null
Compress-Archive -Path tools/tiktok-video-extension/extension/* -DestinationPath dist/fraylo-video-saver-0.1.2.zip -Force
```

Trong **Chrome Web Store Developer Dashboard**, chọn **Add new item** và tải ZIP lên. Tên, mô tả, ảnh chụp và khai báo quyền trên trang niêm yết cần phản ánh các chức năng thực tế. Tăng `version` trong manifest và trong tên ZIP khi gửi bản mới. [Hướng dẫn đăng tiện ích](https://developer.chrome.com/docs/webstore/publish)

**Mục đích duy nhất** — lưu tệp video từ liên kết tải trực tiếp do người dùng nhập hoặc do TikTok cung cấp cho bài đăng.

| Quyền | Lý do |
| --- | --- |
| `activeTab` | Đọc địa chỉ tab người dùng đang thao tác để xác định trang video TikTok. |
| `scripting` | Khi người dùng bấm tìm link, đọc địa chỉ tải của bài video trên tab đang mở. |
| `downloads` | Mở hộp thoại lưu tệp bằng trình tải xuống của Chrome khi người dùng bấm lưu. |

Tiện ích xử lý URL trong bộ nhớ của popup. Yêu cầu lấy tệp đi trực tiếp từ Chrome đến máy chủ cung cấp tệp. Mã tiện ích không có máy chủ riêng, công cụ đo lường hay kho lưu URL. Chrome quản lý lịch sử tải xuống theo cài đặt trình duyệt.
