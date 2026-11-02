# Tài liệu Fraylo

Tài liệu về kiến trúc, vận hành và quá trình phát triển Fraylo. Xem [README dự án](../README.md) để tìm hiểu sản phẩm, yêu cầu môi trường và cách chạy từ mã nguồn.

## Trạng thái và triển khai

| Tài liệu | Nội dung |
| --- | --- |
| [Checkpoint hiện hành](agents/STATUS.md) | Tiến độ mới nhất, kết quả kiểm thử và giới hạn kiểm chứng |
| [Danh sách công việc](agents/TASKS.md) | Trạng thái, dependency và phạm vi công việc |
| [Kế hoạch tích hợp](plans/20261027-video-first-integration-next.md) | Các bước tích hợp và bằng chứng kiểm tra theo thời điểm |

## Kiến trúc và tính năng

| Tài liệu | Nội dung |
| --- | --- |
| [Kiến trúc video-first](plans/VIDEO_FIRST_ARCHITECTURE_PLAN.md) | Domain, dữ liệu và kiến trúc sản xuất video |
| [Hai luồng sản xuất video](plans/VIDEO_TWO_FLOW_SPLIT.md) | Nhiều video theo cảnh và một video nhiều phân cảnh |
| [Nhóm dự án và prompt theo cấp](plans/PROJECT_GROUPS_AND_LAYERED_PROMPTS.md) | Tài nguyên dùng chung và thứ tự ưu tiên prompt |
| [Đăng video TikTok](plans/TIKTOK_PUBLISHING_HANDOFF.md) | Luồng đăng chủ động, trạng thái tích hợp và giới hạn live |
| [Nhận diện Fraylo](branding/README.md) | Tên, logo và các định danh TiToAI được giữ để tương thích |

## Quy ước phát triển

- [Quy tắc kỹ thuật](agents/ENGINEERING_RULES.md).
- [Điều phối Antigravity](agents/ORCHESTRATION.md), áp dụng khi làm việc trong Antigravity.
- [Mục lục implementation](plans/implementation/README.md).
- [Kế hoạch nguồn](plans/SOURCE_PLAN.md), đọc cùng các cập nhật kiến trúc và checkpoint mới hơn.

Các kế hoạch và bằng chứng kiểm thử phản ánh thời điểm được ghi trong từng tài liệu. Khi tra cứu tiến độ hoặc khả năng đã được xác minh, ưu tiên mục **Trạng thái hiện tại** trong [STATUS](agents/STATUS.md).
