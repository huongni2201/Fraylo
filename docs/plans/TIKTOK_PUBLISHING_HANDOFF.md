# Đăng video TikTok — luồng tích hợp và kiểm chứng

Ngày 2026-10-27 (UTC+07). Owner: chat **Thêm tab Đăng video**. Người dùng đã mở quyền sửa file chung và cho phép phối hợp với hai chat Analytics/migration. Sidebar, route, CTA từ video hoàn tất, bridge và host đã nối trong source. Frontend build/kiểm thử điều hướng PASS; build backend mới chờ schema/consumer VF1 hoàn tất đồng bộ. App đang chạy cần nạp lại bản build sau tích hợp.

Yêu cầu trực tiếp mới cho phép luồng Playwright upload/đăng sau khi tạo video, thay ranh giới export-only trong phần TikTok Publishing của [video-first plan](VIDEO_FIRST_ARCHITECTURE_PLAN.md). Chat migration giữ ownership schema/consumer và STATUS/TASKS; chat này giữ module Publishing cùng wiring UI/host. Không đổi evidence lịch sử thành PASS hiện hành.

Kế hoạch mở rộng: [tab danh sách video phân trang để đăng nhiều video](20261101-bulk-publishing.md) (đề xuất, chưa triển khai).

## Module đã chuẩn bị

- `src/Fraylo.Web/src/features/publishing/`: trang form, nav item, API factory và DOM tests. Trang nhận `active`, API ổn định và callback mở Cài đặt/dự án; không tự tải nền khi chưa vào trang. Giữ trang mounted khi đổi route để không mất caption và tác vụ đang chạy.
- `src/Fraylo.Browser/Publishing/TikTokVideoUploader.cs`: context persistent riêng của account, login thủ công, browser hiển thị theo yêu cầu quan sát 2026-10-30, kiểm danh tính live trước upload; `SetInputFilesAsync`, `Keyboard.InsertTextAsync`, kiểm caption trước lệnh đăng. Quyền xem theo lựa chọn hiện có trên TikTok.
- `src/Fraylo.Desktop/Bridge/Handlers/PublishingHandlers.cs`: đã register tại `MainWindow`; cả hai nhánh cleanup đều unregister.
- `tests/Fraylo.Publishing.SmokeChecks/`: runner riêng dùng SQLite/profile/file tạm; fixture Playwright trên Edge headless. Không sửa solution hoặc runner chung.

Session dùng persistent profile có sẵn thay việc xuất thêm `storageState` JSON chứa cookie/token. Không đăng nhập bằng password tự động. Route chỉ chặn GET ảnh/font và hai host tracking được liệt kê; giữ script, media, fetch/XHR, POST/PUT phục vụ upload. Không cam kết tăng tốc 3 lần khi chưa đo thực tế.

`Preparing` → commit `Submitted` **trước click Post** → `Published` chỉ sau thông báo thành công của TikTok. Theo yêu cầu 2026-10-30, lỗi/hủy/gián đoạn chuyển `Failed`, giữ lỗi gốc và nhả guard; bỏ đối soát và không tự retry. Lịch sử `Uncertain` cũ chuyển `Failed` khi tải lịch sử hoặc bắt đầu đăng. Chỉ yêu cầu mới do người dùng gửi mới đăng lại; replay cùng request ID không gửi lại. `Published` giữ guard profile + SHA-256. Nếu TikTok đã nhận bài nhưng app chưa đọc được xác nhận, đăng lại thủ công có thể trùng bài. Lỗi đọc URL tùy chọn hoặc đóng browser sau xác nhận không đổi kết quả đã đăng.

## Wiring hiện hành

1. Hai command `publishing.list` và `publishing.publish` có trong `bridgeCommandAllowlist`; đã bỏ command đối soát theo yêu cầu 2026-10-30.
2. `MainWindow` tạo một service dùng browser controller/profile registry/session resolver hiện có. Adapter có khóa thao tác theo profile, tái dùng context ẩn bằng một page mới và chỉ đóng tài nguyên tự sở hữu; không chiếm hoặc đóng cửa sổ login visible.
3. `App.tsx` chứa route/breadcrumb `Đăng video` và `PublishingNavItem` **ngay sau TikTok Analytics**. API được khởi tạo một lần:

   ```tsx
   const publishingApi = createPublishingApi((command, payload, options) =>
     bridgeClient.invoke(command, payload, options));
   ```

   `PublishingPage` giữ mounted trong wrapper `block/hidden`, chỉ tải khi active. Callback Cài đặt chọn đúng tab Tài khoản qua navigation signal; callback xem video quay về đúng Project. Navigation vẫn giữ dirty guard Settings.
4. CTA `Đăng video` ở kết quả `VideoProductionTab` đi qua callback `ProjectPage` về shell, mang project ID và selection revision. Sau khi tải danh sách mới nhất, form tự chọn đúng output/caption. Chuyển route thông thường giữ draft; CTA mới từ Project có thể chọn revision mới. Export cũ là lựa chọn phụ.
5. Backend và UI cùng chặn output stale, nhiều clip chưa có final revision, tài khoản thiếu phiên, caption không hợp lệ và lần đăng đang chạy/đã thành công. Việc tạo video hoàn tất không tự gửi lên TikTok.

## Qualification và giới hạn cần giữ rõ

**LIVE TIKTOK NOT_RUN.** Truy cập URL upload bằng browser kiểm tra chỉ tới trang login. Hợp đồng DOM EN/VI hiện là fixture, chưa xác minh với tài khoản thật: file input duy nhất, caption contenteditable duy nhất, combobox có accessible name quyền xem, nút Post/Đăng và success alert/status. Khi TikTok dùng cấu trúc khác, module dừng báo lỗi; không bấm selector dự đoán, không vượt CAPTCHA. Coordinator cần kiểm DOM thật, cập nhật adapter/fixture tương ứng trước nghiệm thu tính năng hoạt động trên TikTok.

Chưa tích hợp vào durable generation queue; publish là thao tác người dùng chủ động với journal riêng, không tự chạy khi video vừa hoàn tất. Chưa có preview streaming mới; CTA quay về dự án để xem lại video. Không reset DB/profile hay chạm dữ liệu cá nhân khi kiểm thử.

## Kiểm chứng

2026-10-30: Publishing smoke 23/23 PASS, gồm timeout sau submit → Failed giữ lỗi gốc, replay không gửi lại, đăng lại thủ công sau Failed, recovery Submitted/Uncertain cũ và cleanup lỗi sau xác nhận vẫn thành công. Web DOM 3/3 và TypeScript PASS. Browser fixture chạy ngoài sandbox vì Edge thoát ngay khi launch trong sandbox; không liên hệ TikTok thật. Live TikTok NOT_RUN.

Baseline module trước tích hợp: Desktop/backend build PASS; **14 nhóm smoke PASS** và **2 DOM tests PASS**. Đã kiểm preview Light/Dark, bố cục một/hai cột không tràn ngang. Đây là evidence lịch sử trước migration VF1.

Sau nối UI/host: TypeScript và Vite build PASS; **11 tests PASS** trong appNavigation, videoProductionTab, publishingPage và settingsPage. Runner module đã build source hiện hành 0 warning/error, **20 nhóm smoke PASS** trên SQLite/schema mới và Playwright/Edge với HTML fixture: account hiện hành/legacy, CurrentVideoRevision, revalidate output trước Post, recovery/idempotency/hash, adapter full-flow, context ownership và khóa profile.


```powershell
dotnet tests/Fraylo.Publishing.SmokeChecks/bin/module-verification/TiToAI.Publishing.SmokeChecks.dll
```

Trong `src/Fraylo.Web`:

```powershell
pnpm exec tsc --noEmit -p tsconfig.app.json
node --test src/appNavigation.test.ts src/features/publishing/publishingPage.test.ts src/features/projects/videoProductionTab.test.ts src/features/settings/settingsPage.test.ts
pnpm exec vite build --outDir ../../tests/Fraylo.Publishing.SmokeChecks/bin/web-verification
```

Preview độc lập: chạy Vite rồi mở `/tests/publishing-preview.html`; có banner dữ liệu giả, không gọi publish thật. Không phải route đã tích hợp.

Nguồn API đã tra qua Context7: [persistent context](https://playwright.dev/dotnet/docs/api/class-browsertype#browser-type-launch-persistent-context), [SetInputFilesAsync](https://playwright.dev/dotnet/docs/api/class-locator#locator-set-input-files), [network routing](https://playwright.dev/dotnet/docs/network). Không thêm package hay nâng phiên bản.
