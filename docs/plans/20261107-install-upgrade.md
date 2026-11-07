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

## Launcher mở lại 1.0.6 sau khi tải 1.0.7

- Log máy ghi hai lần giải nén xong nhưng lỗi tại `JsonSerializer.Serialize`. Cấu hình phát hành `PublishTrimmed=true` tắt serialization dựa trên reflection. Vì lỗi xảy ra trước khi thay `current.json`, launcher mở lại 1.0.6. [Tài liệu .NET](https://learn.microsoft.com/en-us/dotnet/core/compatibility/serialization/8.0/publishtrimmed).
- Launcher ghi hai trường JSON bằng `Utf8JsonWriter`, sau đó thay con trỏ nguyên tử như trước. Lệnh đóng gói coi cảnh báo IL2026 là lỗi để chặn việc đưa lại API không an toàn với trimming.
- Fixture Windows với launcher cài từ 1.0.6 tái hiện lỗi, ZIP đã giải nén nhưng con trỏ vẫn là 1.0.6. Publish launcher đã sửa theo cấu hình phát hành và chạy lại fixture PASS. Gói lỗi giữ bản cũ, gói hợp lệ chuyển sang 1.0.7, giữ `previous_version` và backup dữ liệu thử, chặn downgrade. Publish cảnh báo NU1900 do không lấy được dữ liệu lỗ hổng NuGet.
- Đã thay launcher ở bản cài trên máy và xác minh SHA-256, giữ bản sao launcher cũ. App 1.0.6 đang chạy tại lúc thay tệp. Người dùng bấm cập nhật lại để kiểm bước cuối trên bản cài thật. Với máy khác đang dùng launcher lỗi, cần bộ cài chứa launcher đã sửa vì gói ZIP không thay launcher. Chưa phát hành bộ cài mới.
