# Fraylo — nhật ký và tiến trình từng tác vụ

Ngày 2026-10-27 (UTC+07). **Kế hoạch đề xuất; chưa triển khai hoặc nghiệm thu.** Yêu cầu: mở một job để xem những bước nó đã thực sự đi qua, cập nhật trong lúc chạy, và theo dõi tiến trình rõ ràng. Tài liệu bổ sung [queue/Settings 1.2](QUEUE_EXECUTION_SETTINGS_PLAN.md), [T17](implementation/T17-queue-progress-ui.md), [T27b](implementation/T27b-logging-secrets.md) và [workflow ảnh/video](PRODUCT_IMAGE_VIDEO_WORKFLOW_PLAN.md); không thay lifecycle, retry, snapshot hoặc quy tắc browser của các tài liệu đó.

## 1. Hiện trạng và phạm vi

- `JobScheduler` có sự kiện trạng thái/tiến trình; `job.progress_updated` có `progressPercent` dạng số nguyên và `currentStep`. `JobService.ListActiveAsync` hiện trả `null` cho tiến trình; `useQueueState` giữ lịch sử job hoàn tất trong bộ nhớ của phiên. `JobDetailsDialog` đã có cửa sổ chi tiết. Các điểm này được đối chiếu source hiện tại, không chứng minh lịch sử log bền vững hoặc luồng provider thật đã hoạt động.
- Log ở đây là **nhật ký hoạt động cho người dùng**, có cấu trúc và lưu theo job. Log chẩn đoán ứng dụng theo T27b là phạm vi liên quan nhưng riêng; không yêu cầu lưu toàn bộ debug log trong màn hình job.
- T19–T22 và PV2–PV4 chưa có bằng chứng pipeline/provider live hoàn chỉnh. Chỉ phát log khi bước thực sự xảy ra; công cụ giả trong test không được trình bày là provider live đã xác minh.

## 2. Trải nghiệm

1. Mỗi thẻ trong hàng đợi có trạng thái, bước hiện tại, lý do chờ/lỗi nếu có và thanh tiến trình. Nút **Xem chi tiết** mở dialog hiện có, thêm mục **Nhật ký hoạt động**.
2. Nhật ký hiển thị theo thứ tự xảy ra: thời gian cục bộ, biểu tượng và nhãn mức, thông điệp tiếng Việt ngắn; ghi rõ lần thử khi retry. Có tải thêm lịch sử cũ, trạng thái đang tải/rỗng/lỗi và thử tải lại. Trong khi mở, sự kiện mới xuất hiện mà không kéo người dùng khỏi vị trí đang đọc; nút về dòng mới nhất khi cần.
3. Ví dụ những dòng chỉ ghi khi có sự kiện thật: “Đã đưa vào hàng đợi”, “Đang chờ tài khoản phù hợp”, “Đã bắt đầu lần thử 1”, “Đang gửi yêu cầu”, “Đã nhận kết quả, đang lưu”, “Cần đăng nhập lại”, “Đã lưu kết quả” hoặc “Đã hủy”. Kèm lý do/CTA thích hợp, không tiết lộ prompt hay thông tin đăng nhập.
4. Danh sách có mục **Đã hoàn tất** và **Lỗi/Đã hủy** truy vấn lịch sử có phân trang; cho phép mở lại log sau restart theo thời hạn lưu. Giữ nguyên các CTA login/manual/reconcile/cancel hiện có. Job cũ trước tính năng hiển thị “Chưa có nhật ký cho tác vụ này”; không dựng lại các bước chưa có bằng chứng.
5. Dùng theme, chữ, icon và trạng thái focus sẵn có; có keyboard, focus return, thông báo trạng thái phù hợp cho screen reader, hỗ trợ giảm chuyển động. Không phụ thuộc màu sắc để diễn đạt lỗi hay thành công.

## 3. Ý nghĩa thanh tiến trình

- Thanh job có hai chế độ: **xác định** khi backend có tỷ lệ đo được hoặc mẫu số ổn định và **không xác định** khi chỉ biết bước. Ví dụ “Đang chờ kết quả từ nhà cung cấp” dùng thanh không xác định; elapsed time có nhãn, không suy ra % từ thời gian.
- Nếu provider cung cấp tỷ lệ thật hoặc bước có đơn vị byte/số lượng đã xử lý, cho biết con số và ý nghĩa của mẫu số; trường hợp không biết dùng `null`, không dùng 0 làm giả định. `Succeeded` chỉ hiển thị 100% sau khi kết quả và metadata được lưu thành công. `Failed`, `Canceled`, `AwaitingUser` hiển thị nhãn và giá trị cuối cùng đáng tin nếu có; không tự nhảy tới 100%.
- Retry là lần thử mới: hiển thị “Lần thử 2” và tiến trình lần này; không giả vờ tỷ lệ cũ vẫn là tiến trình của lần mới. Resume từ checkpoint hiển thị mốc đã xác nhận và tình trạng hiện tại. Stale worker không ghi tiến trình mới.
- Với workflow nhiều scene, hiển thị riêng tiến trình tổng **số clip hoàn thành / tổng clip đã chốt**, kèm số đang chạy/chờ/lỗi. Chỉ tính clip hợp lệ đã lưu; scene bỏ qua hoặc bị chặn được nêu rõ và không coi là thành công. Khi chưa chốt tổng, hiện bước đang làm và số đã hoàn thành, không tính %. Các thanh job con vẫn mô tả riêng job, không suy một job là cả workflow. Đây là phần tích hợp T22/T23 khi run/job liên kết thật đã sẵn sàng.

## 4. Lưu trữ và hợp đồng đề xuất

Coordinator kiểm tra source/baseline và chốt DTO/version/migration trước khi chia việc; các tên dưới là đề xuất, không khẳng định đã tồn tại.

### 4.1. Lịch sử bền vững

- Lưu bước và tiến trình hiện hành theo job với `attempt`, `currentStep`, `progressPercent?`, nguồn/đơn vị nếu có và phiên bản cập nhật; cập nhật cùng transaction với hoạt động tương ứng. Dùng schema mở rộng tương thích migration hiện có và dữ liệu job cũ; không biến log thành nguồn điều khiển scheduler.
- Mốc chuyển trạng thái và sự kiện nghiệp vụ quan trọng được ghi cùng transaction ngắn với thay đổi job khi cùng DB; chỉ phát thông báo sau commit. Ghi log cho bước thực sự xảy ra, gồm enqueue/reserve/start, submit checkpoint, nhận/lưu kết quả, chờ, retry/resume, cancel/reconcile, success/failure và lỗi lưu. Tránh ghi lặp heartbeat hoặc từng byte; gộp/cập nhật tiến trình tần suất hợp lý, nhưng không làm mất mốc chuyển bước.
- Bước do worker báo phải kiểm state/owner/lease/fencing token và attempt trong cùng ranh giới ghi. Hành động do người dùng/recovery phải theo điều kiện chuyển trạng thái hiện hành. Một worker hết lease không thể sửa tiến trình hoặc ghi bước thành công. Không để log thay đổi quyết định resend hay giải phóng slot.
- Nếu ghi mốc trong cùng transaction thất bại, transaction không được báo thành công; báo lỗi quan sát được. Nếu provider đã có thể nhận request trước lỗi lưu, giữ/chuyển trạng thái uncertain theo recovery, đối soát trước khi gửi lại. Sự cố phát event UI sau commit không được rollback hay chạy lại công việc; UI tải lại từ DB.

### 4.2. Đọc và cập nhật giao diện

- Lưu lịch sử sau restart; mặc định đề xuất giữ **30 ngày sau khi job kết thúc**, tối đa **10.000 dòng/job**, không xóa log job còn active hoặc cần reconcile. Khi chạm giới hạn, giữ các mốc trạng thái và dòng lỗi, gộp/bỏ các cập nhật tiến trình cũ và hiển thị thông báo lịch sử đã rút gọn. Nếu dung lượng thấp hay không thể dọn vì job active, cảnh báo và tiếp tục áp dụng quy tắc lỗi ghi ở §4.1. Không xóa job, asset hoặc dữ liệu người dùng. Xác định thời gian/dung lượng cuối cùng tại T27b; kiểm tra backup/restore DB cùng T26.

## 5. Thứ tự triển khai và ownership

Trước mỗi bước coordinator xác nhận task dependency, owner đang hoạt động và working-tree baseline; không ghi chồng vùng worker. Không dispatch chỉ vì tài liệu này được tạo. Một owner persistence làm migration; coordinator sở hữu hợp đồng dùng chung/composition root.

| Bước | Phạm vi dự kiến | Phụ thuộc và kết quả bàn giao |
|---|---|---|
| L0 — chốt | Coordinator: hợp đồng, retention, ownership, version và danh sách mốc thực của từng task type | Đối chiếu T15–T18/T27b, danh sách đang thay đổi và schema migration. Chốt quy tắc đổi `%` nullable; không đánh dấu T19–T22 DONE. |
| L1 — lưu/đọc | Persistence + jobs: entity/migration, writer, progress snapshot, history query, lifecycle, recovery, tests | Sau L0; transaction/fencing và scope trước. Không giữ transaction trong lúc gọi browser hoặc chờ người dùng. |
| L2 — bridge | Coordinator/integrator: DTO, command allow-list, events, mapping C#/TS | Sau L0; triển khai nối với L1 theo contract đã chốt, tương thích consumer cũ. |
| L3 — giao diện | UI jobs: QueueDrawer, JobItemCard, JobDetailsDialog, hook/API/types và test DOM | Sau L2; history có phân trang, live/reconnect, hai chế độ progress, focus/theme. |
| L4 — tích hợp provider/workflow | Owners T20/T21/T22/T23: báo các bước adapter/run thật, liên kết scene/job, tổng clip | Khi task tương ứng đủ dependency và có adapter thật; L1–L3 không phụ thuộc website live. Không phát mốc giả cho tính năng chưa triển khai. |
| L5 — review và retention | Coordinator/reviewer: tích hợp L1–L4, kiểm T26/T27b/T30 | Chạy test phù hợp, rà an toàn dữ liệu và lưu evidence theo revision; giữ rõ NOT_RUN khi thiếu website, tài khoản hay desktop thật. |

Không đổi trạng thái T17/T27/G3 chỉ từ plan này. G3 vẫn cần T18–T24 theo [ORCHESTRATION](../agents/ORCHESTRATION.md); kiểm chứng logging/secrets và release vẫn thuộc T27/T30.

## 6. Nghiệm thu

- Test DB SQLite thật: rollback giữa job và activity; worker token cũ, concurrent writer, restart, lịch sử terminal, phân trang cùng timestamp, retention và khôi phục sau sự cố. Test side-effect không chắc chắn bảo đảm không resend hoặc giải phóng slot sai.
- Test workflow: mẫu số chỉ tính clip đã chốt, một nhánh lỗi không được tính thành hoàn thành; đối chiếu job con với scene/run. Provider không hỗ trợ tiến độ phải hiển thị bước đang chờ. Test bằng adapter giả chỉ xác nhận wiring; live/Windows/WebView2/DPI cần chạy riêng khi môi trường sẵn sàng và ghi đúng kết quả.
