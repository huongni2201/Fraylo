# Cài đè và schema cuối — 2026-11-07

Owner: Codex coordinator, tuần tự, tự review. Người dùng cấp quyền triển khai trong chat; dependency installer/bootstrapper hiện hành đã có. Giữ mọi thay đổi sẵn có. Không phát hành, commit/push hay mở DB cá nhân.

## Quyết định

- Tag v1.0.2 và v1.0.3 thực tế đều dùng schema 6. Nhánh cũ reset schema <7 làm mất dữ liệu đang dùng (dù có backup). Thay bằng rebuild Project/Asset giữ các cột hiện hành, bỏ quan hệ ProjectGroup đã ngừng hỗ trợ. Metadata/file tài nguyên giữ nguyên; thông tin nhóm cũ còn trong backup SQLite.
- Chỉ một baseline cuối final-schema.sql (v14) cho cài mới; một file upgrade-to-v14.sql cho DB v6–v13. Không đổi số schema về 1 vì DB v13 đã có; v14 thêm lựa chọn viết lại/nguyên bản cho nháp nhóm. Các SQL lịch sử chỉ nằm trong Fixtures của test, không nhúng vào ứng dụng.
- Backup bằng SQLite Backup API trước nâng cấp. Toàn bộ bước nâng cấp nằm trong một transaction, kiểm foreign_key_check trước commit. Lỗi bước cuối rollback về schema ban đầu. Phiên bản database mới hơn bị từ chối.
- Bộ cài giữ AppId/thư mục dữ liệu, chặn khi Fraylo/launcher còn chạy (bao gồm bản cũ chưa có mutex); chặn downgrade theo current.json. Không buộc tắt công việc. Máy không truy vấn được WMI sẽ dừng cài và báo lỗi.
- Launcher từ chối version sai/cùng/cũ hơn; thay current.json nguyên tử; timeout chờ app thoát không mở thêm app. Launcher của người dùng cũ chỉ được thay khi chạy Setup mới; ZIP tự cập nhật hiện không thay launcher cũ.

## Kiểm chứng

- Schema lấy nguyên từ git tag v1.0.2/v1.0.3, tạo SQLite tạm và seed Project/Asset: nâng v13, giữ dự án/metadata/quan hệ, mở lại PASS.
- Hồi quy schema v7–v12, backup, rollback, schema/model parity và từ chối schema tương lai PASS.
- Launcher Release build: 0 warning/error PASS.
- Inno Setup 6.7.3 biên dịch script mới với payload fixture PASS. Đây không phải bộ cài phát hành.
- Test bổ sung lỗi bước cuối toàn chuỗi v6: xem log .tmp/upgrade-schema-check.log.
- Format C# bootstrapper bằng dotnet format whitespace --folder PASS (workspace formatter bị sandbox chặn named pipe).

## Giới hạn nghiệm thu

NOT_RUN: chạy Setup cài đè thật trên Windows sạch, nhánh WMI/downgrade trong UI installer, cập nhật R2 thật, dữ liệu người dùng thật. Không tuyên bố đã nghiệm thu phát hành. Bản 1.0.5 đã phát hành không được vá bằng cách ghi đè artifact; đóng gói bản mới 1.0.6 sau nghiệm thu.

Nếu người dùng đã chạy 1.0.5 và DB v6 bị reset, bản sửa không tự ghép dữ liệu đã mất: cần kiểm tra backup pre-reset và phục hồi có kiểm soát, tránh ghi đè dữ liệu mới.
