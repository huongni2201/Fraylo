# Một video nhiều phân cảnh và ảnh tham chiếu

Yêu cầu người dùng 2026-10-28: ChatGPT hoặc Gemini lập kịch bản theo tổng thời lượng và tạo ảnh cho từng phân cảnh; Fraylo lưu nội dung và ảnh, rồi gửi prompt cùng các ảnh cho Muse tạo **một video**. Muse chỉ làm bước tạo video. Người dùng xác nhận dùng https://muse.ai/ và nhận nhiều ảnh; model, giới hạn và cách gán ảnh chưa xác minh.

## Contract đích

- Một Project/run sinh một video, một job render. Phân cảnh là thành phần có thứ tự của storyboard, không tạo một job render riêng cho mỗi phân cảnh.
- Job kịch bản, ảnh và prompt dùng ChatGPT hoặc Gemini theo cấu hình từng tác vụ; job render dùng Muse. Không yêu cầu Muse tạo ảnh hoặc tự đổi provider khi tài khoản được chọn không sẵn sàng.
- Storyboard có tổng thời lượng, các đoạn liên tiếp từ 0 đến tổng thời lượng, mô tả hành động và hình ảnh; xác thực thời lượng, nội dung và thứ tự trước khi tạo ảnh. Mốc thời gian chỉ là yêu cầu sáng tạo, không bảo đảm điểm cắt chính xác.
- Mỗi phân cảnh liên kết ảnh thực tế có thể tái sử dụng; ảnh gốc sản phẩm được giữ làm tham chiếu. Ảnh tải về phải được xác thực và lưu bằng cơ chế Asset hiện có, kèm vai trò, thứ tự và provenance. Không lưu cookie hoặc URL phiên trong database.
- Prompt render kết hợp template đã render, storyboard và ánh xạ rõ vai trò ảnh. Snapshot của một job đóng băng prompt, thời lượng, danh sách asset và hash theo thứ tự; không âm thầm bỏ ảnh khi vượt giới hạn provider.
- Chỉ gọi render sau khi toàn bộ đầu vào bắt buộc hợp lệ; checkpoint trước side effect, đối soát kết quả không chắc chắn, không gửi lại mù. Lưu video kết quả vào revision của Project.

## Quyết định 2026-10-28: nối clip và thời lượng 30 giây

- Người dùng cho phép Muse nối nhiều clip để đạt tối đa 30 giây. Việc nối do Muse thực hiện trong phiên chat, tạo ra một video đầu ra. Fraylo vẫn không ghép video cục bộ, không dùng FFmpeg.
- Muse tự báo một clip AI tối đa 10 giây. Vết cắt giữa các clip được chấp nhận. Adapter phải ghi nhận kết quả là video nối nhiều clip, không coi là một clip liền mạch.
- Storyboard và capability cần cập nhật theo tối đa 30 giây. Hiện `ProviderCapabilities` ghi Muse hỗ trợ 10 và 15 giây, nên giá trị này phải đổi khi triển khai.
- Chưa xác nhận Muse luôn tạo đủ 30 giây khi được yêu cầu. Cần kiểm tra bằng thử nghiệm thật, và cần đối chiếu thời lượng trả về (`duration` của `<video>`) với thời lượng yêu cầu.

### Adapter render MuseAI (TRIAL) — 2026-10-28

- `MuseAiPlaywrightAutomation` (mới): mỗi job mở `https://muse.ai/thread/new` trong profile MuseAI, đính kèm ảnh theo thứ tự `ImageAssetIds`, điền prompt kèm dòng `Thời lượng yêu cầu`, ghi checkpoint trước khi bấm `Send`, chờ URL `/thread/<uuid>`, chờ tin trợ lý có `<video>` và hết `Assistant is typing`, bấm `Open animated video` rồi `Download`, lưu MP4 thành Asset kiểu `Video`.
- Lỗi sau khi gửi trả `reconcile_required` kèm URL side chat để đối soát; không gửi lại tự động.
- URL side chat được lưu trong AppSettings `job.museai.thread.<jobId>` ngay sau khi gửi. Lệnh `job.recover_output` (nút "Tải lại video từ phiên MuseAI đã lưu" trong đối soát) mở lại đúng side chat, chờ video, tải về, lưu Asset rồi đánh dấu tác vụ Succeeded và áp dụng kết quả. Không gửi lại prompt. Chưa kiểm chứng trên muse.ai thật.
- `MuseAiVideoAdapter` nhận automation tùy chọn; `VideoRender` của MuseAI là `Verified` khi có automation. Khi không có automation, adapter vẫn trả `automation_blocked` như trước.
- `JobMediaSnapshotDto.ImageAssetIds` (mới): danh sách ảnh có thứ tự; được kiểm tra scope và hash như các ảnh snapshot khác. `CreateSceneVideoAsync` hiện chỉ truyền một ảnh.
- `AssetService.SaveVideoAssetAsync` (mới): kiểm tra MP4 bằng chữ ký `ftyp`, lưu vào thư mục Videos của Project.
- `MuseAiVideoAdapter` đăng ký trong `MainWindow.xaml.cs` với automation mới. Capability Muse thêm thời lượng 30 giây.
- Build: `Fraylo.Application` và `Fraylo.Desktop` đều PASS, 0 warning/0 error (output Desktop ở `.tmp/muse-desktop-build`).
- **Chưa kiểm chứng:** chưa chạy render thật từ Fraylo; profile MuseAI của Fraylo chưa được xác nhận đăng nhập (đăng nhập trong trình duyệt tích hợp không tính); selector và đường tải `Download` qua Playwright chưa được kiểm chứng trong ứng dụng; chưa có test riêng cho `ReadRenderInput`.
- **Còn thiếu cho flow đầy đủ:** Script chưa sinh và lưu storyboard; chưa có ảnh riêng cho từng phân cảnh; `CreateSceneVideoAsync` chưa gom nhiều ảnh theo storyboard; UI chưa có trình phát video.

### Storyboard từ Script và gom ảnh theo thứ tự — 2026-10-28

- Prompt Script yêu cầu storyboard `{durationSeconds, shots:[{startSeconds,endSeconds,description,imagePrompt}]}` với tổng thời lượng lấy từ `TargetDurationPerSceneSeconds` (run đã cố định `TargetSceneCount = 1`, nên giá trị này là thời lượng cả video).
- Kết quả Script hợp lệ theo storyboard được lưu bằng `StoryboardContentService.SaveAsync`; mỗi shot thành một phân cảnh theo thứ tự, thời lượng là `end - start`, `imagePrompt` giữ làm purpose để tạo ảnh bằng đường có sẵn. Kết quả không phải storyboard vẫn đi theo đường `scenes` cũ.
- Khi Project có từ hai phân cảnh trở lên, `CreateSceneVideoAsync` gửi một job `VideoRender` cho cả Project: `ImageAssetIds` theo thứ tự phân cảnh, thời lượng là tổng, prompt liệt kê `start–end giây: <mô tả hoặc prompt video>`. Từng phân cảnh phải có ảnh; thiếu thì báo đúng phân cảnh.
- Kết quả render chỉ thành video cuối khi job phủ mọi phân cảnh (`ImageAssetIds` số lượng bằng số phân cảnh) hoặc Project chỉ có một cảnh. Clip của một cảnh lẻ không thành video cuối.
- **Kiểm thử:** `Fraylo.Storyboard.SmokeChecks` PASS. Suite `workflow` của `Fraylo.Desktop.SmokeChecks`: 27 PASS / 11 FAIL. Test 15 (multi-scene legacy) PASS sau khi khôi phục điều kiện chặn clip một cảnh. Các FAIL còn lại: Test 1–17 của bridge, T10 (template biến chưa khai báo), mục 5 (browser headless) không liên quan; mục 13 (CD06) còn kỳ vọng 30 giây bị chặn, mâu thuẫn quyết định cho phép 30 giây. Chưa xác minh được các FAIL có tồn tại trước thay đổi này, vì không stash được cây làm việc đang có nhiều thay đổi.
- **Chưa làm:** chưa có test cho đường storyboard → scene → render; chưa xác nhận Image job tạo ảnh cho từng shot (cần cấu hình workflow mode và model/product); chưa có UI cho storyboard và trình phát video.

### Chat mới, main chat và side chat — 2026-10-28

- Mở `https://muse.ai/thread/new` cho một composer trống, không có tin nhắn. Sidebar hiển thị mục `Main chat` và danh sách `Side chats`; mỗi side chat là một mục có tên, không phải thẻ `<a>` có href nên chưa biết có URL riêng hay không.
- Vào `https://muse.ai/` mặc định mở main chat có lịch sử (đã thấy khi đăng nhập).
- Đã gửi một tin thử ngắn (không yêu cầu tạo video) vào `/thread/new`. Trình duyệt chuyển sang `https://muse.ai/thread/<uuid>`. Chat mới xuất hiện dưới mục `Side chats` với tên do Muse đặt (`Acknowledge short test message`) và được chọn; `Main chat` không đổi. Kết luận: `/thread/new` tạo side chat mới, không ghi vào main chat.
- Side chat có URL riêng dạng `/thread/<uuid>`, nên adapter có thể mở đúng chat theo id. Muse đã trả lời `đã nhận` (`data-message-role="assistant"`). Người dùng chọn side chat này làm chat thử: `https://muse.ai/thread/7195d6b1-3414-4d1a-b4b2-31be2d190214`.
- Khi mở lại, sidebar hiện thêm side chat `Find today’s weekday` không do tôi tạo. Nguồn của nó chưa xác định.
- **Quyết định 2026-10-28:** mỗi lần render mở side chat mới qua `https://muse.ai/thread/new`. URL `/thread/<uuid>` chỉ có sau khi gửi tin đầu tiên, nên adapter phải ghi uuid ngay sau khi gửi. Nếu tiến trình dừng giữa lúc gửi và lúc ghi uuid, chat đó có thể bị bỏ lại; recovery phải tìm đúng chat bằng tên hoặc nội dung trước khi gửi lại, không gửi lại mù.
- Có nút `Side chat options` trong sidebar; chưa khảo sát menu này.

### Thử sáu ảnh trong một yêu cầu — 2026-10-28

- Người dùng đã đính kèm sẵn sáu ảnh trong composer. Composer có sáu thẻ `img[src^="blob:"]`, tên `images (3).jpg`, `anh-mo-ta.jpg`, `images (2).jpg`, một file JPG tên hash, `7c437bc9….jpg` và `38f1ef2b-….png`. Tin gửi hiển thị `+2` cùng các thẻ ảnh, nên chưa xác nhận Muse nhận đủ sáu ảnh đến khi đọc được phản hồi.
- Đã gửi một prompt yêu cầu video dọc 9:16 dài 30 giây từ sáu ảnh theo thứ tự, mỗi ảnh khoảng 5 giây, và yêu cầu nêu vị trí điểm cắt nếu nối clip.
- **Lệch so với quyết định:** tin này được gửi vào side chat thử `7195d6b1…` thay vì side chat mới qua `/thread/new`, vì sáu ảnh đã nằm sẵn trong composer của chat này. Lịch sử của chat chỉ có tin `đã nhận`, nên ảnh hưởng ngữ cảnh là nhỏ.
- **Kết quả:** sau khoảng 5 phút, Muse trả lời với một video. Thẻ `<video>` có `duration` 30.021 giây, 720×1280. Thứ tự ảnh được giữ, điểm cắt ở 5, 10, 15, 20, 25 giây.
- Muse mô tả cách làm: mỗi ảnh tạo một clip AI 10 giây, lấy 5 giây đầu rồi nối lại. Ảnh không đúng 9:16 được đưa về 720×1280 bằng nền mờ. Muse cũng ghi chuyển động làm biểu cảm và tư thế đổi nhẹ trong đoạn; ví dụ cô gái cyberpunk cúi đầu thấp hơn ảnh gốc.
- **Ý nghĩa cho adapter:** Muse không dựng một clip từ sáu ảnh cùng lúc. Nó sinh sáu clip riêng rồi tự nối; Fraylo chỉ gửi ảnh và prompt, không kiểm soát cách cắt. Chất lượng khớp ảnh gốc cần kiểm tra thủ công.
- Chưa xác nhận được giới hạn số ảnh: sáu ảnh đã được chấp nhận.
- Tin video này có nút `Open animated video video-ai-6-anh-30-giay-9-16.mp4`, nằm ngoài phần tử tin nhắn mà JavaScript đọc đầu tiên. Bấm nút này mở `Media preview` với cùng cấu trúc như clip 15 giây: `Close`, `Download`, `Unmute`. Thời lượng 30,021 giây, 720×1280, nguồn `blob:`. Đã đóng bằng Escape, chưa bấm `Download`.
- Cách tải lần trước (clip 15 giây) tạo file `ush.mp4` trong Downloads. Tin này chưa tải; kích thước file chưa biết trước khi tải.
- **Đã tải** bằng `Download` trong dialog `Media preview`, không gửi tin mới. File `video-ai-6-anh-30-giay-9-16.mp4` lưu trong Downloads lúc 14:04:22, 18 776 193 byte. Header MP4 đọc được: `ftyp`, 30,022 giây, 720×1280. Tên file trùng tên nút Muse hiển thị, khác `ush.mp4` của lần trước.
- Cơ chế tải vẫn nằm trong dialog `Media preview` và dùng nguồn `blob:`; không thấy URL tải riêng.

### Nút xem video trong chat — 2026-10-28

- Bấm `Open animated video <tên>.mp4` mở một dialog `role="dialog"` có tiêu đề `Media preview`, không tải file và không gửi tin. Dialog có các nút `Close`, `Download`, `Pause`, `Unmute`. Đã bấm nút xem cho clip 15 giây (`cyberpunk-tokyo-15s.mp4`), rồi đóng bằng Escape.
- Đã bấm `Download` cho clip 15 giây, không gửi tin mới. Một file `.mp4` xuất hiện trong thư mục Downloads lúc 13:30:58 (`ush.mp4`, 10 965 668 byte). Header MP4 đọc được: `ftyp isom`, thời lượng 15.000 giây, 720×1280. Tên file khác tên Muse (`cyberpunk-tokyo-15s.mp4`), nên adapter không được dựa vào tên file. Chưa có bằng chứng 100% rằng file này đến từ lần bấm này, vì không quan sát được sự kiện tải trực tiếp; thời điểm khớp với lần bấm.
- Trong network, nguồn video là `blob:` với nhiều request `206 Partial Content` và `200 OK`. Không thấy URL tải riêng có chữ ký trong các request được ghi nhận, nên có khả năng tải từ blob phía client. Chưa xác minh bằng cách đọc mã phía trang.
- Dialog hiển thị nhãn thời gian phát (`8s`, `11s`), đây là vị trí phát chứ không phải thời lượng.
- Dialog hiển thị nhãn `8s` ở phần đầu. Chưa rõ đây là thời lượng hay vị trí phát; không dùng làm bằng chứng thời lượng.

## Dependency và kiểm chứng

- Quan sát phiên Muse đã đăng nhập ngày 2026-10-28: giao diện là chat trợ lý, có ô Message và nút Attach file; input file cho phép chọn nhiều tệp. Người dùng cho phép gửi một ảnh để thử; Muse trả về một MP4, trình xem cho thấy thời lượng 5 giây, 704×1248 và nút Download lưu được file. Muse mô tả kết quả là video AI có chuyển động. Các message DOM có `data-message-id` và `data-message-role`; response và video mới nằm sau message người dùng, không xóa lịch sử. Một mẫu thao tác thủ công không xác minh adapter Fraylo, nhiều ảnh, video 15 giây hoặc xử lý crash/concurrency. Lịch sử trong phiên cũng có MP4 dựng từ ảnh tĩnh và các lần báo lỗi tạo video AI; không coi mọi MP4 là bằng chứng thành công.
- ChatGPT và Gemini có adapter thử nghiệm cho Script/Image/VideoPrompt; Muse VideoRender chưa triển khai. Không xác nhận capability chỉ dựa trên tài liệu này. Cần xác định model/chế độ Muse, số ảnh, vai trò ảnh, thời lượng 15 giây và cách theo dõi/tải kết quả trên giao diện thật.
- `Scene` hiện đại diện clip và các bước render theo scene. Cần thay contract, điều phối, snapshot, persistence và UI đồng bộ; không tái diễn giải dữ liệu cũ thành storyboard mà không có quyết định reset hoặc chuyển đổi rõ ràng.
- Kiểm tra tối thiểu: storyboard đủ và liên tục đúng tổng thời lượng; ảnh thiếu/sai scope/hash bị chặn; một render nhận đủ ảnh đúng thứ tự; recovery sau submit không gửi trùng; lưu được đúng video cuối. Test fixture không thay thế kiểm tra provider thật.

### Quan sát DOM chỉ đọc sau đăng nhập — 2026-10-28

Chỉ đọc accessibility tree và DOM; không gửi tin, không đính kèm, không bấm nút trong chat.

- Composer: `textarea` placeholder `Message`; `input[type=file]` có `multiple=true`, không có `accept`, nằm trong composer; nút `Attach file` ngay cạnh. Nút gửi chưa xác định vì chưa nhập nội dung.
- Tin nhắn: container có `data-message-id` và `data-message-role` (`user` / `assistant`). Tin trả video dùng UUID làm id (không có tiền tố `assistant-msg-`), chứa một `<video>` và nút `Open animated video <tên>.mp4`. `src` là `blob:` nên không tải trực tiếp từ DOM; cần khảo sát nút mở/tải trước khi viết adapter.
- Muse là agent hội thoại, không có form model hay chế độ video riêng quan sát được. Yêu cầu render là một tin nhắn tự nhiên; "xong" phải suy ra từ container video mới xuất hiện sau tin của người dùng, không có trạng thái job rõ ràng.
- Giới hạn: agent tự báo "một clip AI chỉ tạo được tối đa 10 giây". Bản 15 giây trong lịch sử là hai clip AI nối lại, có vết cắt quan sát được quanh giây thứ 10. Vì vậy "15 giây trong một clip" chưa được xác nhận.
- Lịch sử có clip 5 giây (704×1248) và 15 giây (720×1280, 24 fps). Một số tác vụ ghi `Failed` hoặc `Interrupted`; agent có lúc báo dịch vụ tạo video gặp sự cố. Bản "3 ảnh" trước đó được mô tả là dựng từ ảnh tĩnh, không phải chuyển động AI; không tính là bằng chứng thành công.
- Agent hỏi xác nhận trước khi thử lại (các nút trả lời nhanh như `Thử lại video AI ngay`). Adapter phải xử lý trạng thái chờ người dùng, không tự chấp nhận.
- Chat là hội thoại dài dùng chung của tài khoản. Gửi job vào chat đó sẽ lẫn với lịch sử cũ; cần chat mới hoặc cách nhận diện đúng tin trả lời.

## Tiến độ content

Đã thêm dịch vụ lưu/đọc storyboard theo Project và kiểm tra JSON, thời lượng, phân cảnh liên tiếp cùng prompt ảnh. Bridge và tab sản xuất video hiển thị nội dung đã lưu, trạng thái trống và lỗi tải; kiểm thử frontend 7/7 PASS, build frontend và desktop tại output riêng PASS. Kiểm tra SQLite tạm và trường hợp dữ liệu sai PASS. Chưa nối dịch vụ lưu vào job Script; chưa tự tạo hoặc lưu ảnh. Adapter AI thật vẫn chưa có, không báo luồng content tự động đã hoạt động. `dotnet format` không chạy được do sandbox từ chối named pipe; restore báo không truy cập được dữ liệu kiểm tra lỗ hổng NuGet.

Phân cảnh có thể tham chiếu asset ảnh đã liên kết, đúng scope; UI mở thông tin asset hoặc báo chưa có ảnh. Cửa sổ video báo rõ chưa phát trực tiếp và cho mở thư mục chứa. Ảnh thu nhỏ, player thực, tải video và điều phối tạo video chưa triển khai: `asset.get_preview` hiện chỉ trả metadata, chưa có đường media an toàn cho WebView2. Build frontend và kiểm thử storyboard PASS; chưa kiểm tra WebView2 thực tế.

Đây là delta yêu cầu mới, thay quy tắc mỗi Scene là một clip trong `CLIP_DURATION_STORYBOARD_PLAN.md` đối với luồng mới. Chưa nghiệm thu luồng đầu-cuối.
