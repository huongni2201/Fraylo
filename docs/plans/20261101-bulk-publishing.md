# Tab danh sách video để đăng nhiều video (phân trang theo thời gian)

Ngày: 2026-11-01. Trạng thái: **ĐÃ TRIỂN KHAI — đưa vào commit đồng bộ Fraylo ngày 2026-11-01** — `CreatedAtUtc` ở DTO/service, `VideoListTab.tsx`, `Tabs` trong `PublishingPage`. Kiểm chứng: `tsc` PASS; web tests 18/18 gồm ca mới phân trang/đăng theo dòng; Vite build PASS; Publishing smoke 25 nhóm PASS (gồm ca `CreatedAtUtc`). Khác plan: Tải lại giữ trang hiện tại (chỉ lùi khi vượt tổng trang). Chưa xem preview Light/Dark bằng mắt; live TikTok `NOT_RUN`. Đã cập nhật theo yêu cầu: **không làm đăng hàng loạt tự động/hàng đợi**; chỉ làm giao diện để đăng nhiều video, mỗi video đăng bằng thao tác riêng.

## 1. Yêu cầu

Thêm một tab vào trang **Đăng video** liệt kê các video sẵn sàng, **mới nhất trước**, có **phân trang** (không giới hạn ở 10 video). Mỗi dòng:

| Video | Tài khoản | Content (caption) | Action |
|---|---|---|---|
| tên dự án · file, thời gian tạo, trạng thái | chọn tài khoản TikTok cho **riêng dòng đó** | caption sửa được | nút **Đăng** ở bên phải |

Không có nút "Đăng tất cả", không có hàng đợi, không có hộp xác nhận cả loạt.

## 2. Hiện trạng đã đối chiếu

Đọc cùng [TIKTOK_PUBLISHING_HANDOFF](TIKTOK_PUBLISHING_HANDOFF.md).

- `PublishingPage.tsx` là form một video: state `assetId/accountId/caption` đơn lẻ, không dùng lại được cho nhiều dòng.
- `publishing.list` trả toàn bộ `videos` sẵn sàng (xếp theo `Project.UpdatedAtUtc`) và 50 `attempts` gần nhất. `PublishVideoDto` **chưa có thời điểm tạo**, nên chưa sắp xếp được theo thời gian video.
- `Asset.CreatedAtUtc` đã có trong DB và `FindReadyVideosAsync` đã load asset, nên chỉ cần đưa ra DTO.
- Backend đã đủ cho đăng nhiều video: mỗi request có `clientRequestId`; guard video + tài khoản chặn đăng trùng; uploader khóa theo profile nên hai lần đăng cùng tài khoản không chạy chồng.
- `components/ui/tabs.tsx` và `Table` đã có; không cần thêm package.

## 3. Thiết kế đề xuất

### 3.1 Backend (nhỏ)

- Thêm `CreatedAtUtc` vào cuối `PublishVideoDto`, gán `asset.CreatedAtUtc` trong `FindReadyVideosAsync`.
- Không thêm command bridge, bảng hay migration; `publishing.publish` giữ nguyên.
- **Phân trang phía giao diện:** `publishing.list` đã trả đủ danh sách nên UI sắp xếp `createdAtUtc` giảm dần rồi cắt trang. Không đổi hợp đồng bridge. Khi số video lớn (hiện `ListAsync` quét mọi dự án) mới cân nhắc `limit/offset` phía server.

### 3.2 Frontend

- `PublishingPage` bọc nội dung bằng `Tabs`: **Đăng một video** (nội dung hiện tại, không đổi hành vi) và **Danh sách video** (mới). Giữ cả hai tab mounted (ẩn bằng class như wrapper route ở `App.tsx`, hoặc `forceMount` + `hidden`) để không mất caption đang sửa.
- File mới `VideoListTab.tsx` nhận prop `videos`, `attempts`, `accounts`, `api`, `onAttempt(result)`, `onOpenAccounts` từ `PublishingPage`; không tải dữ liệu riêng.
- **Phân trang:** 10 dòng/trang, điều khiển Trước/Sau và "Trang x/y", hiển thị tổng số video. Về trang 1 khi bấm Tải lại; giữ nguyên trang hiện tại khi chỉ cập nhật trạng thái. Nếu trang hiện tại vượt tổng sau reload thì lùi về trang cuối.
- State theo dòng, khóa theo `assetId`: `accountId`, `caption` (khởi tạo từ `video.caption`), trạng thái đang đăng. State nằm ở tab nên **không mất khi đổi trang** hay khi reload; reload không ghi đè caption đã sửa.
- Cột: Video (tên + file + MB + thời gian tạo `vi-VN`) · Tài khoản (`select`, nhãn "Cần đăng nhập" như form đơn) · Content (`Textarea` 3 dòng, đếm `n/2.200`) · Trạng thái · **Đăng** (cột cuối).
- Trạng thái dòng lấy từ attempt mới nhất khớp `assetId + accountId` đang chọn: chưa đăng / Đang tải / Chờ xác nhận / Đã đăng (kèm link) / Thất bại (kèm lỗi gốc).
- Nút **Đăng** bật khi: tài khoản `Connected`, caption 1–2.200 ký tự, không có attempt chặn (`blocksAnotherPublish`), và **không có dòng nào khác đang đăng** (mỗi lần mở một cửa sổ TikTok thật, nên chỉ cho một bài chạy tại một thời điểm, kể cả với tab đơn qua cờ `publishing` dùng chung ở `PublishingPage`). Người dùng tự bấm từng dòng.
- Request ID: tách đoạn "signature → `crypto.randomUUID()`" trong `PublishingPage.publish` thành helper nhỏ ở `publishingApi.ts`, dùng chung. Thất bại thì xóa ID để đăng lại là yêu cầu mới; bấm lại khi cùng nội dung đang chạy thì replay, không gửi lại.
- Không có ô Sound ở tab này (đăng không sound); cần sound thì dùng tab đơn.
- Bảng có khung cuộn ngang cục bộ ở màn hẹp; không để tràn ngang cả trang.

## 4. File dự kiến chạm

| Lớp | File | Thay đổi |
|---|---|---|
| Contract | `src/Fraylo.Contracts/Publishing/PublishingDtos.cs` | thêm `CreatedAtUtc` |
| Backend | `src/Fraylo.Application/Publishing/TikTokPublishingService.cs` | gán `CreatedAtUtc` |
| Web | `src/contracts/bridge.ts`, `features/publishing/publishingApi.ts` | kiểu `PublishVideo`, helper request ID |
| Web | `features/publishing/PublishingPage.tsx` | bọc `Tabs`, truyền prop |
| Web | `features/publishing/VideoListTab.tsx` (mới) | bảng + phân trang |
| Test | `publishingPage.test.ts`, `tests/publishing-preview.html`, `tests/Fraylo.Publishing.SmokeChecks/Program.cs` | fixture có `createdAtUtc` và ca mới |

**Ownership:** working tree đang có thay đổi chưa commit ở đúng các file trên (Sound, bỏ đối soát). Khi triển khai phải đọc diff hiện tại, sửa chồng lên và không hoàn tác. Không đụng schema/migration (thuộc chat migration).

## 5. Kiểm chứng và nghiệm thu

DOM test (`publishingPage.test.ts`) bổ sung:

1. 25 video fixture → tab hiện 10 dòng/trang, sắp theo `createdAtUtc` giảm dần; trang 2 và 3 đúng nội dung (10/10/5); nút Trước/Sau khóa đúng ở đầu/cuối.
2. Chọn tài khoản của dòng A không đổi dòng B; bấm Đăng gửi đúng `assetId`, `accountId`, caption **đã sửa** của dòng đó, không có `soundName`.
3. Caption đã sửa còn nguyên sau khi sang trang khác rồi quay lại, và sau reload.
4. Nút Đăng khóa khi: chưa chọn tài khoản / `NeedsLogin`, caption rỗng hoặc >2.200, attempt đang chạy hoặc `Published`, hoặc dòng khác đang đăng.
5. Hai lần bấm cùng nội dung → một request ID.

Smoke (`Publishing.SmokeChecks`): `ListAsync` trả `CreatedAtUtc` bằng `Asset.CreatedAtUtc` cho clip đơn và clip theo cảnh.

Lệnh chạy: như mục "Kiểm chứng" của handoff (`tsc --noEmit`, `node --test …publishingPage.test.ts`, `vite build`, smoke DLL). Xem giao diện bằng `tests/publishing-preview.html` (Light/Dark, màn hẹp).

**Chưa thể nghiệm thu:** live TikTok vẫn `NOT_RUN`.

## 6. Rủi ro và giới hạn

- `FindReadyVideosAsync` trả rỗng cho dự án còn job chạy hoặc có cảnh stale, nên video của dự án đó tạm biến khỏi danh sách và số trang có thể đổi.
- Video theo cảnh (nhiều video/dự án) mỗi clip là một dòng, caption theo cảnh.
- Phân trang client ổn ở quy mô hiện tại; `ListAsync` quét mọi dự án nên chậm dần khi dữ liệu lớn.
- Đăng lại thủ công sau `Failed` có thể trùng bài nếu TikTok đã nhận (giữ cảnh báo của handoff trên dòng lỗi).

## 7. Cần chốt

1. **Vị trí:** tab con trong trang Đăng video (đang giả định), hay mục riêng trên sidebar.
2. **"Mới nhất":** theo thời điểm tạo file video (đang giả định), hay theo dự án cập nhật gần nhất.
3. **Cỡ trang:** 10 dòng/trang (đang giả định).

## 8. Thứ tự làm

1. Contract + backend `CreatedAtUtc`, cập nhật fixture và smoke.
2. Helper request ID + `VideoListTab` (dòng, đăng từng dòng, phân trang) + `Tabs` trong `PublishingPage`.
3. DOM test, `tsc`, `vite build`, smoke; kiểm tra preview Light/Dark; ghi kết quả vào STATUS/TASKS qua coordinator.
