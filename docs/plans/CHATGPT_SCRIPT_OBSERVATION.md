# Quan sát luồng kịch bản ChatGPT

Ngày 2026-10-28, thử thủ công trên chatgpt.com trong trình duyệt không đăng nhập, bằng prompt mẫu về bình nước tái sử dụng. Đây là evidence UI, chưa phải xác minh adapter production.

## Đã quan sát

1. Trang mới có ô nhập ChatGPT và nút gửi chỉ khả dụng sau khi nhập prompt.
2. Sau khi gửi, prompt xuất hiện trong hội thoại, địa chỉ chuyển sang hội thoại mới và nút dừng tạo xuất hiện.
3. Phản hồi xuất hiện dần, gồm nhiều khối nội dung và ảnh; nội dung chưa đầy đủ trong lúc đang tạo.
4. Khi phản hồi hoàn tất xuất hiện nhóm thao tác, gồm nút sao chép phản hồi. Kết quả mẫu gồm ba cảnh và lời thoại.

## Quy trình cần triển khai cho Fraylo

- Lấy đúng profile tài khoản và quyền sở hữu browser; kiểm tra phiên, mở hội thoại sạch và nạp prompt từ snapshot của job.
- Trước khi gửi, ghi checkpoint side effect theo contract hiện hành; gửi một lần, ghi nhận hội thoại và theo dõi phản hồi của lượt gửi đó.
- Chờ tín hiệu hoàn tất đáng tin cậy; lấy toàn bộ nội dung phản hồi, kiểm tra không rỗng và định dạng phù hợp trước khi lưu kết quả. Không coi nội dung đang xuất hiện từng phần là kết quả hoàn chỉnh.
- Nếu mất kết nối hoặc hủy sau khi gửi, giữ trạng thái chưa chắc chắn và đối soát trước khi gửi lại. Nếu chưa đăng nhập, bị chặn hoặc UI thay đổi, báo lỗi cụ thể thay vì giả thành công.
- Đóng tab và nhả quyền sở hữu theo chính sách browser ẩn; không đọc profile trình duyệt cá nhân.

## Chưa kiểm chứng

Phiên đăng nhập trong profile Fraylo, selector DOM, cấu trúc trả lời phù hợp parser, browser ẩn, lỗi mạng, hủy và phục hồi chưa được kiểm thử. Không bật capability Verified hoặc AutomationSupported chỉ dựa trên lượt thử này. Các bước tạo ảnh và video chưa được thử.

## Cập nhật triển khai 2026-10-28

- Đã thêm `ChatGptPlaywrightAutomation` cho Script/Image/VideoPrompt, dùng profile của scheduler, trang hội thoại mới, rendered prompt và checkpoint trước khi bấm gửi. Image tải mẫu rồi sản phẩm từ snapshot, lấy ảnh tạo thật trong phản hồi và lưu Asset. Lỗi sau checkpoint trả reference hội thoại để đối soát.
- Adapter đã được nối ở composition root. Theo yêu cầu người dùng ngày 2026-10-28, Script/Image/VideoPrompt tạm công bố `TRIAL` và `AutomationSupported=true` để thử trên tài khoản thật; browser hiện ra trong ba tác vụ này. Đây là trạng thái thử nghiệm, không phải `VERIFIED`. MuseAI render vẫn chưa sẵn sàng.
- `muse.ai` được người dùng chỉ định cho render, nhưng trang công khai hiện mô tả video hosting/search, chưa có bằng chứng về image-to-video generation. Cần URL màn hình tạo video hoặc chọn nhà cung cấp render có chức năng đó trước khi viết adapter render.
- Build mặc định song song dừng ở MSBuild `_GetProjectReferenceTargetFrameworkProperties` với 0 diagnostic; build Desktop tuần tự `--no-restore -m:1` PASS vào `.tmp/chatgpt-trial-build`. Kiểm thử website thật: `NOT_RUN`.
