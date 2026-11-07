# ReferenceVideo — evidence TM0

Ngày 2026-11-07 (UTC+07). Owner: Codex coordinator. Nguồn phạm vi: [plan](20261107-reference-video-workflow.md). **Hướng cuối: MuseAI mô phỏng chuyển động gần đúng, thời lượng độc lập và tự ghép/co giãn audio gốc. Đã có trial thật và adapter recovery→asset→apply→Completed trên bản sao DB. Full UI generation trên app cá nhân còn NOT_RUN.** Các phần login/direct/10giây/stream-copy dưới đây là evidence lịch sử theo từng yêu cầu, không thay quyết định cuối ở mục mới nhất.

## Quyền và phạm vi

- Người dùng đã yêu cầu triển khai plan: chỉ thay Chỉ sản phẩm, giữ nguyên Mẫu + sản phẩm và Ảnh đã hoàn thiện.
- Người dùng cho phép tự chọn một ảnh mẫu và một video bất kỳ để kiểm chứng, tiếp tục dùng MuseAI và tự thêm account/login trong Fraylo.
- Mọi lần đọc DB gốc dùng `Mode=ReadOnly`, SQLite Backup API tạo bản sao dưới `.tmp/reference-video-probe/out/`. Không bootstrap/nâng schema/sửa DB gốc; không đọc hoặc xuất cookie/token.
- Probe browser dùng `ProfileRegistry`/`PlaywrightBrowserController` và `ProfileOwner` hiện có, đúng profile MuseAI của Fraylo, chạy headless. Chỉ mở `/thread/new`, đọc DOM và đóng browser; chưa đính kèm hoặc gửi yêu cầu.

## Kết quả đã quan sát

| Kiểm tra | Bằng chứng / kết quả |
|---|---|
| Baseline source sau WFS | Workflow smoke **30/30 PASS**, chạy DLL Release hiện hành; không có sửa ứng dụng trong TM0. |
| DB trong sandbox | Mở được đường dẫn nhưng Backup API gặp SQLite14; lượt chạy ngoài sandbox với nguồn ReadOnly tạo bản sao thành công. Không suy lỗi này là DB hỏng. |
| Danh mục ban đầu | Không có profile MuseAI Active; Library có ảnh Linked, không có video Linked. |
| Sau người dùng thêm account | Có một profile MuseAI Active, trạng thái `Unverified`. |
| Trang công khai | `https://muse.ai/thread/new` chuyển về `https://muse.ai/`, hiển thị đăng nhập. Trang giới thiệu không đủ bằng chứng về motion-reference video. |
| Probe trên profile Fraylo | Cũng chuyển về `/`; không có composer, không có file input. Có textbox `Mobile number or email`, nút `Continue`, `Log in`, nội dung `Log in or create an account`. Browser đóng sau kiểm tra. |
| Locator composer cũ | Lượt đầu timeout15s vì chỉ chờ textarea/email/tel; đã bổ sung capture DOM sau timeout. DOM xác nhận form login dùng `input[type=text]`, không coi timeout là bằng chứng provider không hỗ trợ. |
| Capability ảnh + video / giới hạn / chất lượng | **NOT_RUN**: cần hoàn tất login trong profile này trước. Không bật cờ hỗ trợ dựa trên trang login. |

Các hàng login phía trên là checkpoint ban đầu. Sau khi người dùng xác nhận hoàn tất đăng nhập, đã quan sát profile `Connected`, composer `textarea[placeholder=Message]` và file input có `multiple=true`, `accept` rỗng.

## Kiểm chứng sau đăng nhập và quyết định mới

- Đính kèm chung hai file trong một `SetInputFilesAsync` chỉ còn một preview ở lượt quan sát. Đính kèm **lần lượt** ảnh rồi video tạo đúng hai preview với `alt=model-outfit.jpg` (1320×1878) và `alt=reference-runway.mp4` (4096×2160), thẻ video có biểu tượng play. Input được reset sau khi nhận file; không dùng `input.files` rỗng để suy ảnh/video bị mất.
- Request POST `/api/auth/save-account` là kết nối nền kéo dài, không phải upload. Chờ toàn bộ POST kết thúc khiến lượt probe đầu timeout trước submit. Sau khi loại đúng endpoint đã quan sát khỏi pending upload, request file/preview và nút Send đủ điều kiện để thử. Không bỏ qua các request upload chưa xác định hoặc suy network idle là bằng chứng render xong.
- Có hai video5.041667s/480×480 của avatar Muse trong trang; **không phải video kết quả**. Chỉ xét video trong message của assistant ở đúng chat/run.
- Lượt direct đã gửi đúng một lần trong cuộc trò chuyện thử nghiệm, lưu checkpoint trước click và URL sau gửi. MuseAI trả lời công cụ video không nhận video làm motion reference, chỉ nhận ảnh tĩnh và prompt; không tạo output (`videos=[]`). Không đổi câu trả lời này thành suy đoán đã tạo video.
- Sau kết quả trên, **người dùng chọn giữ MuseAI, phân tích video thành prompt và chấp nhận chuyển động gần đúng**. Đây là thay đổi phạm vi trực tiếp, được đồng bộ vào plan; direct motion capability vẫn không được mở.
- Lượt mô phỏng trong chat mới hết thời gian chờ navigation trước khi lưu URL. Đối soát UI sau khi danh sách Side chats nạp xong chỉ thấy chat direct; mở chat đó xác nhận chỉ có đúng yêu cầu direct và câu từ chối, không có prompt mô phỏng mới. Không resend mù hoặc xóa checkpoint để lách guard.
- Tiếp tục **lựa chọn mới của người dùng** bằng một follow-up trong chính chat đã có hai tệp: yêu cầu tạo video gần đúng10s và trả prompt giữa `MOTION_PROMPT_BEGIN`/`MOTION_PROMPT_END`. Đây là ngoại lệ thử nghiệm có kiểm tra trước rằng chat chỉ có một lượt direct; production vẫn cần chat mới mỗi job.
- Checkpoint follow-up riêng đã lưu; lần đọc đầu thấy `typing=true`, Activity “Checking extracted frames for video generation”, chưa có video. Các lượt poll chỉ đọc lại URL đã lưu, không gửi yêu cầu mới. Chưa đủ bằng chứng bật `SupportsReferenceVideoApproximation` cho production.

Raw evidence trong workspace: `out/trial-checkpoint.json`, `out/trial-result.json`, `out/trial-attachments.json/.png`, `out/approx-checkpoint.json` (navigation chưa đối soát URL), `out/approx-followup-checkpoint.json`, `out/approx-followup-result.json`. Không có cookie/token trong các artifact này.

## Cặp dữ liệu thử đã chuẩn bị

Chỉ sao chép/tải vào workspace; không sửa file gốc, không import vào DB cá nhân, chưa gửi tới MuseAI.

| Vai trò | Tệp / nguồn | Dung lượng | SHA-256 |
|---|---|---|---|
| Ảnh người mẫu mặc sản phẩm | `.tmp/reference-video-probe/samples/model-outfit.jpg`, sao chép từ Library asset `cde1e49947964bd9b160ba779d8fdf4f`; đã xem ảnh, một người mặc bộ đồ màu kem. | 375601 bytes | `4B1D613A3008E31246D924B8D9C199F60D75B3E929E2DBA61800B5A7ED7B9DE0` |
| Video chuyển động | `.tmp/reference-video-probe/samples/reference-runway.mp4`, [Pexels — A Woman Posing while Walking on the Runway](https://www.pexels.com/video/a-woman-posing-while-walking-on-the-runway-9511253/), cottonbro studio; trang nguồn ghi Free,17giây,4096×2160,25fps. Tải qua link download chính thức chuyển tới `videos.pexels.com`. | 34456186 bytes | `AEB0753A625D9FDD454A86BBFAA399293B8C05A065CAF0003578A57557265A1D` |

Thông số clip trong bảng lấy từ trang nguồn, chưa phải metadata do host Fraylo kiểm tra. Ảnh chỉ thấy tới phần chân trên, clip catwalk là toàn thân; phải ghi nhận ảnh hưởng này khi đánh giá chất lượng. Không cắt/ghép/transcode mẫu và không thêm FFmpeg.

## Tái lập và bước tiếp theo

Harness: `.tmp/reference-video-probe/Program.cs` và project cùng thư mục. `inventory` đọc danh mục, `media` đọc metadata Library/roots; `inspect` mở ẩn và lưu `out/composer.json`, `out/composer.png`. Các file JSON/ảnh chỉ là evidence môi trường hiện tại, không đóng gói vào ứng dụng.

Checkpoint login và trial cũ đã hoàn tất theo các phần dưới. Hướng/policy hiện hành theo mục cuối, không chạy lại yêu cầu cùng thời lượng/direct motion đã bị thay thế.

## Kết quả trial mô phỏng

Follow-up đã hoàn tất trong cùng side chat: một video10s,736×1056, tải thành công về `out/approx-followup-output.mp4`,4557387bytes, SHA-256 `8B0ADD8A9D16BE7B23E69253585F25C7B1C3F7264ABE7113C88798130D09A420`. MuseAI trả prompt thật giữa hai marker và mô tả đã cô đọng chuỗi tạo dáng→quay lưng→đi xa vào10s; không chuyển động trực tiếp từ video.

Đọc metadata bằng player Chromium trên file local: video nguồn **16.56s**, output **10s**. Chụp tại5%,45%,85% mỗi video bằng seek của player, tự đối chiếu: người/beret/tóc/bộ đồ kem từ ảnh được giữ nhận diện chính; output có quay lưng và đi xa, nhưng nhịp bị rút gọn, mức chuyển động camera và chi tiết nền/đồ có khác. Không đánh giá là giữ chính xác từng chi tiết hoặc từng frame. Khung hình ở `out/reference-runway-*.png`, `out/approx-followup-output-*.png`.

**TM0 đạt cho mô phỏng gần đúng**, mở TM1 và các dependency tiếp theo theo plan. Cờ direct motion không được mở. Bản đầu đề xuất output10s đã thử; giới hạn input của app sẽ chốt trong contract, không coi là giới hạn tối đa đã đo của MuseAI. Trial follow-up tái sử dụng context có input; TM5 vẫn phải chạy adapter production từ chat mới và kiểm recovery/preview/DB/bridge. Chưa có sửa ứng dụng tại thời điểm đóng TM0, không nâng schema hoặc sửa DB cá nhân.

## Quyết định cuối và trial audio fitting

Người dùng sau đó yêu cầu cùng nhạc/thời lượng, rồi xác nhận **thời lượng khác vẫn chấp nhận**, và cuối cùng **gửi audio cho AI tự ghép/co giãn theo độ dài video**. Quyết định cuối thay cả kiểm byte/audio gốc và chỉ cắt đầu nhạc. Fraylo không thêm công cụ ghép audio cục bộ.

- Mẫu có audio: [MDN CC0 flower.mp4](https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.mp4), tải thành `.tmp/reference-video-probe/samples/audio-reference.mp4`,1128375bytes, SHA-256 `0CD83D944A6CA7822B4A8306CECC60A36E859B041F6702C6A1AD9EAD78924451`. Container5.055s, audio header5.056s.
- Hệ thống duyệt tự động ban đầu từ chối upload cặp video10s đã tạo + mẫu MDN vì cần xác nhận payload cụ thể. Người dùng đã xác nhận **cho phép gửi đúng hai tệp này lên MuseAI** trước khi thử; không né rejection hoặc xuất cookie/token.
- Trial stream-copy3s trước: file3.008s,141sample AAC đầu khớp payload/timing/cấu hình decoder gốc. Bitrate hint/track ID thay do remux nên không coi toàn bộ stsd byte là nội dung nhạc. Đây là proof lịch sử; checker sample-hash đã bỏ khỏi production theo chính sách AI fitting mới.
- Trial AI fitting trong cuộc trò chuyện thử âm thanh: MuseAI dùng toàn bộ audio5.055s, báo `atempo=1.685`, AAC re-encode để ghép vào hình3s. File mới `out/audio-fit-output.mp4`,1097718bytes, SHA-256 `B9B7D7162257E80FC8AEA1304BB94635470E5F6520B0AB3A790F765465AAE927`.
- Host kiểm file thật: container3s/736×1056, có1track audio; audio header3.010125s, provider báo phần nghe2.989s do priming/rounding AAC. Kiểm cấu trúc/dung sai0.25s PASS; không dùng metadata để chứng minh đúng bài hoặc bảo toàn cao độ. Người dùng nghe duyệt, provenance ghi `ProviderReported_UserReview`.
- Trial đều có checkpoint trước gửi; poll chỉ mở lại chat. Không nhận avatar/video cũ làm file mới. Raw: `out/audio-clip-*`, `out/audio-fit-*`.

## Adapter production và giới hạn qualification

Harness `AdapterTrial` dùng DB backup workspace và storage root riêng, tạo Project/job/snapshot thật, gắn URL trial đã có để kiểm **recovery-only**; callback chặn mọi submit/upload. `MuseAiPlaywrightAutomation` thật đọc đúng chat, tải file mới, kiểm metadata/audio, lưu Asset và phản hồi policy/response thật. Apply qua `ProjectWorkflowService` trả true, run **Completed**, Asset đầu ra được chọn. **Không gửi lại prompt, không sửa DB gốc.** Raw `out/adapter-recovery-checkpoint.json`, `out/adapter-result.json`, file ở `out/adapter-storage/`.

Đây không phải full UI generation từ chat mới. Năng lực sinh chuyển động và audio fitting đã thử riêng; chuỗi production Start→fresh submit→render→fit tự động trong app cá nhân, WebView2/DPI125–150 và ít nhất3cặp mẫu còn **NOT_RUN**. Fixture/local tests không thay evidence này. Kết quả build/regression cuối ghi tại STATUS.

## Kiểm chứng source cuối

- Solution Release và Desktop Release sau sửa cuối kèm TypeScript/Vite PASS;1warning nullable cũ ở `AccountConcurrencyAndBrowserLifecycleSmokeTests.cs:943`.
- ReferenceVideo integration PASS: MP4/metadata/audio duration, silent source, độ dài output khác nguồn, import và shared Library, hash thay sau snapshot bị chặn, mode/policy và metadata bất biến, bấm Start lặp không nhân job, apply→Completed, run mới giữ lịch sử nhưng không dùng video cũ làm output.
- Workflow30/30, provider8/8, project-first PASS. Toàn web chạy tuần tự **69/69 PASS**; còn cảnh báo React act cũ, không là FAIL.
- Visual fixture Light/Dark tại600–1280px đã quan sát, không tràn ngang; không thay cho WebView2/DPI thật. Preview server/tab đã đóng.
- Lỗi tạm đã xử lý trong lượt: sandbox không cho atomic move dưới Temp→dùng TEMP/TMP workspace; một build web chạy trùng output gây ENOENT→lượt build lại tuần tự PASS; lỗi compile helper/test trong khi đang thêm file đã sửa và build lại. Không thay Settings để né lỗi, không chạm DB gốc/schema hoặc hoàn tác thay đổi có sẵn.
