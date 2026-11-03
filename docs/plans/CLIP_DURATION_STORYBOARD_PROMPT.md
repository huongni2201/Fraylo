# Prompt bàn giao — sửa thời lượng clip và phân cảnh

Ngày 2026-10-27. Copy nội dung trong khối dưới để giao cho AI triển khai. Nguồn acceptance duy nhất cho delta này là [plan clip/phân cảnh](CLIP_DURATION_STORYBOARD_PLAN.md).

```text
Hãy triển khai delta “Thời lượng clip và phân cảnh trong prompt” của Fraylo theo docs/plans/CLIP_DURATION_STORYBOARD_PLAN.md. Phạm vi là form, contract/validation liên quan và cách lập prompt cho từng clip. Ghép/cắt/nối video và video dài thành phẩm nằm ngoài phạm vi.

Trước khi sửa:
1. Đọc AGENTS.md, docs/agents/STATUS.md, TASKS.md, ENGINEERING_RULES.md và ORCHESTRATION.md. Áp dụng Model policy khi làm trong Antigravity. Ghi baseline working tree, dependency và owner thực tế; giữ nguyên thay đổi đang có của người khác. Chỉ coordinator cập nhật trạng thái chung.
2. Đọc plan delta, PRODUCT_IMAGE_VIDEO_WORKFLOW_PLAN.md cùng UI_THEME_PLAN.md và UI_BROWSER_PLAN.md. Nếu chạm queue/Settings, đọc plan tương ứng theo AGENTS.md. Dùng CodeGraph trước khi tìm/đọc code nếu repo có index. Dùng Context7 khi cần tài liệu API/framework cụ thể.
3. Khảo sát DTO/frontend/backend/template/capability hiện có. Chốt ownership và thay đổi contract nhỏ nhất trước triển khai. Thực hiện phần có dependency đã hoàn tất; ghi rõ phần tích hợp bị chặn, không triển khai lấn sang toàn bộ adapter/pipeline còn thiếu.

Kết quả cần đạt:
- Giữ Scene là một clip đầu ra. Shot/phân cảnh chỉ là nhịp nội dung bên trong prompt, không tạo Scene/job video riêng.
- Thay “Số cảnh tự động lập” bằng “Số clip cần tạo”, mặc định 1 cho cấu hình mới, giữ giá trị đã lưu. Thay input giây tự do bằng bộ chọn duration theo capability đúng provider/model, áp dụng đồng bộ các đường thêm/sửa có liên quan.
- Duration mới ưu tiên 15s nếu hỗ trợ, sau đó 10s nếu hỗ trợ; trường hợp khác yêu cầu chọn rõ. Chưa xác minh capability thì giữ draft và chặn chạy với lý do, không giả VERIFIED.
- Hiện tóm tắt “N clip riêng × T giây” và giải thích AI tự chia phân cảnh trong clip. Không thêm mode video dài/ghép hoặc trường thời lượng video thành phẩm.
- Muse 10s/15s là thông tin người dùng cung cấp để kiểm chứng. Chỉ bật 30s nếu provider/model có thao tác tạo trực tiếp một clip 30s đã được xác minh; 30s cần ứng dụng tạo nhiều clip rồi ghép bị loại khỏi lựa chọn.
- Prompt có nhịp hành động theo thời gian trong từng clip; timestamp là hướng dẫn, duration request vẫn gửi đúng qua adapter. Multi-shot phụ thuộc capability; nếu chỉ hỗ trợ một shot thì dùng chuyển động liên tục và hiển thị giới hạn. Brief bắt buộc nhiều góc nhưng không hỗ trợ phải báo không tương thích.
- Giữ ba chế độ ảnh, audio tùy chọn, ảnh dùng chung, invalidation theo dependency, snapshot/revision/idempotency/history và queue hiện hành. Không hardcode prompt mẫu để giả AI hoạt động.
- Dữ liệu cũ 3 × 5s vẫn mở được, không đổi ngầm/xóa/tách/gộp scenes. Nếu 5s không hỗ trợ, yêu cầu chọn lại trước chạy; backend cũng chặn request vượt UI. Output/job cũ được giữ nguyên.
- Sửa cả luồng ô “Yêu cầu nội dung / Phong cách chuyển động” theo §3.1–3.3 của plan: lần theo goalPrompt từ form → save thành công → lập clip → request AI viết prompt → snapshot/enqueue → adapter. Dùng brief có chuỗi nhận diện riêng để kiểm request thực ở ranh giới AI writer, xác định nơi mất dữ liệu trước sửa; không kết luận chỉ từ UI hay DTO lưu được.
- Đọc AI_WEB_CONTENT_CREATION_PROMPTS.md §5 để dùng cơ chế composition hiện có; không tạo engine thứ hai. Nguồn brief chung và yêu cầu riêng clip có scope rõ. Request AI writer khác prompt chuyển động output gửi Muse; không gửi cả template/schema qua Muse.
- Thêm “Xem yêu cầu gửi AI” dưới dạng tùy chọn: template/version, brief và prompt cuối; preview/enqueue/manual export dùng cùng logic và revision. Giữ dirty guard. Brief trống chặn run tự động mới; mở project/output cũ vẫn hoạt động. Sửa brief làm stale đúng dependency, giữ prompt chỉnh tay/history; retry giữ snapshot cũ. Không tự chạy lại khi gõ/lưu.

Nghiệm thu theo CD01–CD14 trong plan. Chạy DOM tests cho form/draft, checks backend cho duration/capability/snapshot, test số clip không nhân theo shot và regression request AI writer nhận đủ template + brief. Test phải bắt được mất brief sau save/start, sai template override, render lặp và snapshot bị thay; assert lưu field hoặc mock riêng composer không thay kiểm luồng thật. Chạy build/checks thích hợp theo repo. Dùng fake để kiểm logic; Muse thật, multi-shot và runtime/DPI chưa chạy phải ghi NOT_RUN, không suy PASS từ fake. Nếu dependency pipeline chưa hoàn tất, chứng minh phần sửa được trong phạm vi này và báo blocker cụ thể.

Bàn giao bằng tiếng Việt: hành vi đã đổi, file/revision, lệnh kiểm tra và kết quả, phần chưa làm/blocker. Đồng bộ tài liệu bị thay và liên kết trong phạm vi delta; giữ evidence findings chưa đóng. Không báo DONE toàn pipeline từ việc sửa UI/template.
```
