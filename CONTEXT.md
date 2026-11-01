# Fraylo

Fraylo là ứng dụng Windows local-first tập trung tạo một video TikTok trong mỗi Project và quản lý lần đăng video đó. Thuật ngữ dưới đây là ngôn ngữ chuẩn cho thiết kế mới.

## Sản xuất video

**Project**:
Một công việc tạo và đăng đúng một video TikTok đích; là aggregate root để tìm kiếm, lưu tiến trình và giữ lịch sử revision.
_Avoid_: Workspace, Channel Project

**Video**:
Tệp đầu ra hoàn chỉnh được chọn làm bản hiện hành của Project. Các candidate/revision cũ vẫn thuộc lịch sử Project.
_Avoid_: Clip, Scene

**Scene**:
Một phân đoạn/storyboard nội bộ tùy chọn, có thể tạo clip trung gian; không phải Project hay video đăng độc lập.
_Avoid_: Workspace video, standalone video

**Asset**:
Tệp media/tài liệu local do Fraylo quản lý và có thể được nhiều Project tham chiếu.
_Avoid_: Project-owned file

## Đăng TikTok và thực thi

**TikTok identity**:
Một tài khoản TikTok đã kết nối ở cấp ứng dụng, được chọn làm đích cho một publish attempt.
_Avoid_: Workspace account, channel owner

**Publish attempt**:
Một yêu cầu đăng cụ thể cho một video revision, TikTok identity và snapshot nội dung/options.
_Avoid_: Video status, generation job

**Provider account**:
Account/profile được dùng bởi AI executor, quản lý ở cấp ứng dụng và bind vào job theo capacity.
_Avoid_: Project AI account

**Job**:
Một đơn vị tác vụ bền vững trong queue toàn app, có snapshot và lifecycle recovery riêng.
_Avoid_: Project state, publish attempt
